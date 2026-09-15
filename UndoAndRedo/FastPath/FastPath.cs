using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace UndoAndRedo.FastPath;

public enum FastPathMode
{
    /// <summary>Fast path code does not run at all.</summary>
    Off,
    /// <summary>Dual run: try the fast restore, measure it, put the live state back, then do the normal replay.</summary>
    Shadow,
    /// <summary>Use the fast path for real; replay only when no verified snapshot is available or something fails.</summary>
    On,
}

/// <summary>
/// Snapshot-based rewind. Inert unless logs/UndoAndRedo.fastpath contains "shadow" or "on".
///
/// Capture: right before a player decision starts executing, while it is the only queued action, an in-place
/// memento of the whole model graph is taken (<see cref="ModelSnapshot"/>, a few ms).
///
/// Undo: the memento is written back into the same object instances, the state checksum is compared with the
/// one recorded at capture time, the decision is detached from the (restored) action queue, the recorder and
/// the game's replay log are truncated to the kept prefix, and the combat visuals are rebuilt from the model
/// (<see cref="VisualRebuild"/>). If any step fails or the checksum differs, the caller falls back to the
/// replay path, which rebuilds everything from the save and does not depend on the live state.
/// </summary>
internal static class FastPath
{
    private static string ModePath => System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "UndoAndRedo.fastpath");
    private static readonly string FastLogPath = System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "UndoAndRedo.fastpath.log");
    private const double VisualTimeScale = 25.0;

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

    /// <summary>True while the combat visuals are being rebuilt: hand card motion is snapped (see the NHandCardHolder patches).</summary>
    public static bool VisualSyncActive { get; private set; }

    private static void FastLog(string msg)
    {
        try { System.IO.File.AppendAllText(FastLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{System.Environment.NewLine}"); } catch { }
        Log.Write("fastpath: " + msg);
    }

    // ── reflection into the action queue (validity check + detaching the undone decision) ──

    private static readonly FieldInfo? QueuesField = AccessTools.Field(typeof(ActionQueueSet), "_actionQueues");
    private static readonly FieldInfo? WaitingField = AccessTools.Field(typeof(ActionQueueSet), "_actionsWaitingForResumption");
    private static readonly FieldInfo? NextIdField = AccessTools.Field(typeof(ActionQueueSet), "_nextId");
    private static readonly PropertyInfo? RunningProp = AccessTools.Property(typeof(ActionExecutor), "CurrentlyRunningAction");
    private static FieldInfo? _queueActionsField; // ActionQueueSet.ActionQueue.actions (private nested type)

    public static void VerifyReflection()
    {
        Log.Write($"FastPath reflection: _actionQueues={(QueuesField != null ? "OK" : "NULL")} _actionsWaitingForResumption={(WaitingField != null ? "OK" : "NULL")} " +
                  $"_nextId={(NextIdField != null ? "OK" : "NULL")} CurrentlyRunningAction={(RunningProp?.SetMethod != null ? "OK" : "NULL")}; " +
                  VisualRebuild.ReflectionReport());
    }

    private static List<List<GameAction>> QueueLists(ActionQueueSet set)
    {
        var result = new List<List<GameAction>>();
        if (QueuesField?.GetValue(set) is not IEnumerable queues) throw new InvalidOperationException("ActionQueueSet._actionQueues unavailable");
        foreach (var q in queues)
        {
            _queueActionsField ??= AccessTools.Field(q.GetType(), "actions");
            if (_queueActionsField?.GetValue(q) is List<GameAction> list) result.Add(list);
            else throw new InvalidOperationException("ActionQueue.actions unavailable");
        }
        return result;
    }

    private static int WaitingForResumptionCount(ActionQueueSet set) =>
        WaitingField?.GetValue(set) is ICollection c ? c.Count : -1;

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

    /// <summary>A memento plus the decision it was taken in front of.</summary>
    public sealed class Capture
    {
        public required ModelSnapshot Snapshot;
        public required GameAction Decision;
    }

    // ── capture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by the recorder right before a player decision starts executing. Only valid when that decision
    /// is the only action in the queues: then "restore + detach the decision" is exactly the idle state the
    /// player was in before acting.
    /// </summary>
    public static Capture? CaptureBeforeDecision(int eventIndex, GameAction decision)
    {
        if (Mode == FastPathMode.Off) return null;
        try
        {
            var rm = RunManager.Instance;
            var queued = QueueLists(rm.ActionQueueSet).SelectMany(l => l).ToList();
            int waiting = WaitingForResumptionCount(rm.ActionQueueSet);
            if (queued.Count != 1 || !ReferenceEquals(queued[0], decision) || waiting != 0)
            {
                FastLog($"no capture for event {eventIndex} ({decision.GetType().Name}): queue holds {queued.Count} action(s), {waiting} waiting for resumption");
                return null;
            }
            var snap = ModelSnapshot.Capture($"before event {eventIndex} ({decision.GetType().Name})", Roots(rm));
            FastLog($"captured {snap.Label}: {snap.ObjectCount} objects in {snap.CaptureMs:F1} ms");
            return new Capture { Snapshot = snap, Decision = decision };
        }
        catch (Exception ex)
        {
            FastLog($"capture failed: {ex}");
            return null;
        }
    }

    // ── undo ─────────────────────────────────────────────────────────────────

    private static bool IsIdlePlayPhase(out string why)
    {
        var rm = RunManager.Instance;
        var cm = CombatManager.Instance;
        var cs = cm.DebugOnlyGetState();
        var hand = NPlayerHand_Instance();
        why = "";
        if (!cm.IsInProgress) why = "combat not in progress";
        else if (!rm.ActionQueueSet.IsEmpty) why = "action queue not empty";
        else if (rm.ActionExecutor.IsRunning) why = "executor running";
        else if (rm.ActionQueueSynchronizer.CombatState != ActionSynchronizerCombatState.PlayPhase) why = $"sync state {rm.ActionQueueSynchronizer.CombatState}";
        else if (cs?.CurrentSide != CombatSide.Player) why = $"side {cs?.CurrentSide}";
        else if (cm.EndingPlayerTurnPhaseOne || cm.EndingPlayerTurnPhaseTwo) why = "ending turn";
        else if (cm.IsPaused) why = "combat paused";
        else if (hand != null && (hand.InCardPlay || hand.IsInCardSelection)) why = "card play / selection in progress";
        return why.Length == 0;
    }

    private static MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand? NPlayerHand_Instance()
    {
        try { return MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand.Instance; } catch { return null; }
    }

    /// <summary>
    /// Restores the model to the captured state, verifies it, detaches the undone decision, rebuilds the
    /// visuals. Returns false (without having changed anything, or after rolling back) when the fast path
    /// cannot be used; the caller then runs the replay path. Throws only for errors after the point of no
    /// return, which the caller also answers with the replay path.
    /// </summary>
    public static async Task<bool> TryFastUndo(ReplayRecorder rec, int target, Capture capture, uint? expectedChecksum, int prefixCount)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string T() => $"[+{sw.ElapsedMilliseconds} ms]";
        var rm = RunManager.Instance;
        var cm = CombatManager.Instance;

        if (!expectedChecksum.HasValue) { FastLog($"undo -> event {target}: no expected checksum; using replay"); return false; }
        if (!IsIdlePlayPhase(out var why)) { FastLog($"undo -> event {target}: not idle ({why}); using replay"); return false; }
        var rs = rm.DebugOnlyGetState();
        var cs = cm.DebugOnlyGetState();
        if (rs == null || cs == null || rs.CurrentRoom is not CombatRoom room || NRun.Instance == null || NCombatRoom.Instance == null)
        {
            FastLog($"undo -> event {target}: no live combat room; using replay");
            return false;
        }

        ModelSnapshot live;
        try { live = ModelSnapshot.Capture("live state at undo time", Roots(rm)); }
        catch (Exception ex) { FastLog($"live capture failed: {ex.Message}; using replay"); return false; }

        await ScreenCover.Show();

        // Point of no return starts after the checksum check below.
        var r = capture.Snapshot.Restore();
        uint? sum = rec.CurrentChecksum();
        FastLog($"{T()} restored {capture.Snapshot.Label}: {r.objects} objects, {r.arrays} arrays, {r.errors} errors; checksum {sum} expected {expectedChecksum}");
        if (!sum.HasValue || sum.Value != expectedChecksum.Value)
        {
            var back = live.Restore();
            FastLog($"{T()} checksum mismatch; live state put back ({back.errors} errors); using replay");
            await ScreenCover.Hide();
            return false;
        }

        // The decision is back in its queue (it was about to execute when the snapshot was taken): take it out,
        // hand its id back to the queue, and cut the game's replay log + our bookkeeping to the kept prefix.
        DetachDecision(rm, capture.Decision);
        var replay = rec.Replay;
        if (replay != null && replay.events.Count > prefixCount)
            replay.events.RemoveRange(prefixCount, replay.events.Count - prefixCount);
        rec.TruncateTracking(prefixCount);
        FastLog($"{T()} decision detached; recorded events = {replay?.events.Count}, next action id = {rm.ActionQueueSet.NextActionId}");

        var fastMode = SaveManager.Instance.PrefsSave.FastMode;
        double savedTimeScale = Engine.TimeScale;
        try
        {
            VisualSyncActive = true;
            SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant;
            try { NGame.Instance?.AudioManager?.SetSfxVol(0f); } catch (Exception ex) { Log.Write($"mute failed: {ex.Message}"); }
            Engine.TimeScale = VisualTimeScale;

            await VisualRebuild.Rebuild(rs, cs, room, T, FastLog);
            await VisualRebuild.WaitForVisuals(rs, minFrames: 5, timeoutMs: 1500, FastLog);
            // The old room is freed at the end of the frame it was removed in; by now it is gone, so any model
            // that cached one of its nodes must forget it (lazy caches re-resolve against the new room).
            int cleared = ModelSnapshot.ClearDisposedGodotReferences(Roots(rm));
            if (cleared > 0) FastLog($"{T()} cleared {cleared} reference(s) to freed nodes");
        }
        catch
        {
            VisualRebuild.MakeCurrentRoomSafe(cs);
            throw;
        }
        finally
        {
            Engine.TimeScale = savedTimeScale;
            SaveManager.Instance.PrefsSave.FastMode = fastMode;
            VisualSyncActive = false;
            _ = TaskHelper.RunSafely(RestoreSfxVolumeSoon());
        }
        await ScreenCover.Hide();
        FastLog($"{T()} fast undo -> event {target} complete");
        return true;
    }

    private static void DetachDecision(RunManager rm, GameAction decision)
    {
        var set = rm.ActionQueueSet;
        bool removed = false;
        foreach (var list in QueueLists(set))
            removed |= list.Remove(decision);
        if (!removed) FastLog($"warning: {decision} was not in any queue after restore");
        if (decision.Id.HasValue && NextIdField != null) NextIdField.SetValue(set, decision.Id.Value);
        RunningProp?.SetValue(rm.ActionExecutor, null);

        // Side effects the game applied when the decision was *enqueued* are inside the memento too and must be
        // undone by hand: a potion is flagged IsQueued before its UsePotionAction is enqueued, and the potion
        // popup disables "use" and "discard" while that flag is set.
        if (decision is UsePotionAction use)
        {
            var potion = use.Player.GetPotionAtSlotIndex((int)use.PotionIndex);
            if (potion != null)
            {
                potion.AfterUsageCanceled();
                try { NRun.Instance?.GlobalUi.TopBar.PotionContainer.OnPotionUseOrDiscardCanceled(potion); }
                catch (Exception ex) { FastLog($"potion holder re-enable: {ex.Message}"); }
                FastLog($"cleared queued flag on {potion.Id.Entry}");
            }
        }
    }

    private static async Task RestoreSfxVolumeSoon()
    {
        try
        {
            var t0 = Time.GetTicksMsec();
            while (Time.GetTicksMsec() - t0 < 400) await ScreenCover.NextFrame();
            if (!RewindEngine.ReplayModeActive && !VisualSyncActive)
                NGame.Instance?.AudioManager?.SetSfxVol(SaveManager.Instance.SettingsSave.VolumeSfx);
        }
        catch (Exception ex) { Log.Write($"unmute failed: {ex.Message}"); }
    }

    // ── shadow trial (research) ──────────────────────────────────────────────

    /// <summary>
    /// Shadow trial: restore the snapshot, checksum, restore the live state, checksum again. Returns true
    /// when the fast restore reproduced the expected checksum and the live state came back intact.
    /// </summary>
    public static bool ShadowTrial(ReplayRecorder rec, int target, Capture capture, uint? expectedChecksum,
                                   ShadowSnapshot.Snapshot? expectedShadow)
    {
        var before = capture.Snapshot;
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
