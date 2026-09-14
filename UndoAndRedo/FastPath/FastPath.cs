using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRedo.FastPath;

public enum FastPathMode
{
    /// <summary>Fast path code does not run at all (release default).</summary>
    Off,
    /// <summary>Dual run: try the fast restore, measure it, put the live state back, then do the normal replay.</summary>
    Shadow,
    /// <summary>Reserved: use the fast path for real, replay only as fallback. Not implemented yet.</summary>
    On,
}

/// <summary>
/// Snapshot-based rewind, developed in the shadow of the replay path. Everything in this class is inert
/// unless logs/UndoAndRedo.fastpath contains "shadow" (or, later, "on"). The replay path is the oracle:
/// in Shadow mode an undo first restores the model from the snapshot taken before the undone decision,
/// checks the state checksum against the one recorded then, restores the live state again, and only
/// then hands over to the replay path as usual — so the player never sees the fast path's result.
/// </summary>
internal static class FastPath
{
    private static string ModePath => System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "UndoAndRedo.fastpath");
    private static readonly string FastLogPath = System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "UndoAndRedo.fastpath.log");

    public static FastPathMode Mode
    {
        get
        {
            try
            {
                if (!System.IO.File.Exists(ModePath)) return FastPathMode.Off;
                var text = System.IO.File.ReadAllText(ModePath).Trim().ToLowerInvariant();
                return text == "on" ? FastPathMode.On : text == "shadow" ? FastPathMode.Shadow : FastPathMode.Off;
            }
            catch { return FastPathMode.Off; }
        }
    }

    private static void FastLog(string msg)
    {
        try { System.IO.File.AppendAllText(FastLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{System.Environment.NewLine}"); } catch { }
        Log.Write("fastpath: " + msg);
    }

    /// <summary>Everything the model-level rewind has to cover, in one place.</summary>
    private static object?[] Roots(RunManager rm) => new object?[]
    {
        rm.DebugOnlyGetState(),
        CombatManager.Instance,
        NetCombatCardDb.Instance,
        rm.ActionQueueSet,
        rm.ActionExecutor,
        rm.ActionQueueSynchronizer,
        rm.PlayerChoiceSynchronizer,
        rm.RewardsSetSynchronizer,
        rm.ChecksumTracker,
        rm.CombatReplayWriter,
    };

    /// <summary>Called by the recorder right before a player decision starts executing.</summary>
    public static ModelSnapshot? CaptureBeforeDecision(int eventIndex, string what)
    {
        if (Mode == FastPathMode.Off) return null;
        try
        {
            var snap = ModelSnapshot.Capture($"before event {eventIndex} ({what})", Roots(RunManager.Instance));
            FastLog($"captured {snap.Label}: {snap.ObjectCount} objects in {snap.CaptureMs:F1} ms");
            return snap;
        }
        catch (Exception ex)
        {
            FastLog($"capture failed: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Shadow trial: restore the snapshot, checksum, restore the live state, checksum again. Returns true
    /// when the fast restore reproduced the expected checksum and the live state came back intact.
    /// </summary>
    public static bool ShadowTrial(ReplayRecorder rec, int target, ModelSnapshot before, uint? expectedChecksum,
                                   ShadowSnapshot.Snapshot? expectedShadow)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rm = RunManager.Instance;
        uint? liveBefore = rec.CurrentChecksum();
        ModelSnapshot live;
        try { live = ModelSnapshot.Capture("live state at undo time", Roots(rm)); }
        catch (Exception ex) { FastLog($"live capture failed: {ex}"); return false; }

        bool ok = false;
        uint? fastSum = null;
        ShadowSnapshot.Snapshot? fastShadow = null;
        (int objects, int arrays, int errors) r1 = default, r2 = default;
        try
        {
            var t0 = sw.Elapsed.TotalMilliseconds;
            r1 = before.Restore();
            var t1 = sw.Elapsed.TotalMilliseconds;
            fastSum = rec.CurrentChecksum();
            if (expectedShadow != null) fastShadow = ShadowSnapshot.Capture($"fast restore to event {target}");
            FastLog($"restore {before.Label}: {r1.objects} objects, {r1.arrays} arrays, {r1.errors} errors in {t1 - t0:F1} ms; checksum {fastSum} expected {expectedChecksum}");
            ok = fastSum.HasValue && expectedChecksum.HasValue && fastSum.Value == expectedChecksum.Value;
        }
        catch (Exception ex)
        {
            FastLog($"restore threw: {ex}");
        }
        finally
        {
            try
            {
                r2 = live.Restore();
                uint? liveAfter = rec.CurrentChecksum();
                bool back = liveAfter == liveBefore;
                FastLog($"live state put back: {r2.objects} objects, {r2.errors} errors; checksum {liveAfter} (was {liveBefore}) -> {(back ? "identical" : "DIFFERENT")}");
                if (!back) ok = false;
            }
            catch (Exception ex)
            {
                FastLog($"putting live state back threw: {ex}");
                ok = false;
            }
        }
        sw.Stop();
        FastLog($"shadow trial for undo -> event {target}: {(ok ? "PASS" : "FAIL")} in {sw.Elapsed.TotalMilliseconds:F1} ms");
        if (expectedShadow != null && fastShadow != null)
            ShadowSnapshot.CompareInBackground(expectedShadow, fastShadow, $"FASTPATH restore -> event {target}");
        return ok;
    }
}
