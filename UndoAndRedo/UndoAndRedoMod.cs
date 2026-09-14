using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Encounters;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRedo;

/// <summary>
/// File + console logger. Writes to &lt;Godot user data&gt;/logs/UndoAndRedo.log.
/// </summary>
internal static class Log
{
    private static readonly string LogPath = System.IO.Path.Combine(
        OS.GetUserDataDir(), "logs", "UndoAndRedo.log");

    private static bool _cleared;

    internal static void Write(string msg)
    {
        try
        {
            if (!_cleared)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                System.IO.File.WriteAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] === Log cleared (new session) ==={System.Environment.NewLine}");
                _cleared = true;
            }
            System.IO.File.AppendAllText(LogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{System.Environment.NewLine}");
            GD.Print($"[UndoAndRedo] {msg}");
        }
        catch { }
    }
}

/// <summary>
/// Combat undo/redo built on top of the game's own deterministic combat replay system.
///
/// The game records every player-driven combat event (card plays, potion use, end turn, player choices,
/// action resumptions, hook actions) into <c>CombatReplayWriter</c>, together with a full run save taken
/// when the map point was entered. Undo = rebuild the run from that save and replay all recorded events
/// except the last player decision, in the game's non-interactive replay mode. Redo = feed the removed
/// events back in. No game state is ever patched by hand.
/// </summary>
[ModInitializer("Initialize")]
public static class UndoAndRedoMod
{
    public const string Version = "2.0.0";

    public static void Initialize()
    {
        Log.Write($"UndoAndRedo {Version} initializing");
        ReplayRecorder.VerifyReflection();
        RewindEngine.VerifyReflection();

        var harmony = new Harmony("com.undoandredo.sts2");
        harmony.PatchAll(typeof(UndoAndRedoMod).Assembly);
        Log.Write("Harmony patches applied");
    }

    /// <summary>Shows a short full-screen text flash (same widget the game uses for debug toggles).</summary>
    public static void Toast(string text)
    {
        try
        {
            var node = NFullscreenTextVfx.Create(text);
            if (node != null)
                NGame.Instance?.AddChildSafely(node);
        }
        catch (Exception ex)
        {
            Log.Write($"Toast failed: {ex.Message}");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Harmony patches
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Left Arrow = undo, Right Arrow = redo.</summary>
[HarmonyPatch(typeof(NGame), "_Input")]
internal static class Patch_NGame_Input
{
    [HarmonyPrefix]
    public static void Prefix(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        switch (key.Keycode)
        {
            case Key.Left:
                RewindEngine.RequestUndo();
                break;
            case Key.Right:
                RewindEngine.RequestRedo();
                break;
        }
    }
}

/// <summary>
/// While we rebuild the combat behind a black screen, the game's room entry would fade the screen back in
/// before the replay has caught up. Suppress that fade; RewindEngine fades in itself when done.
/// </summary>
[HarmonyPatch(typeof(NTransition), nameof(NTransition.RoomFadeIn))]
internal static class Patch_NTransition_RoomFadeIn
{
    [HarmonyPrefix]
    public static bool Prefix(ref Task __result)
    {
        if (!RewindEngine.ReplayModeActive)
            return true;
        __result = Task.CompletedTask;
        return false;
    }
}

/// <summary>A run has been set up (new, loaded, or rebuilt by us): attach the recorder to its action queue.</summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
internal static class Patch_RunManager_Launch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        ReplayRecorder.Attach();
    }
}

/// <summary>The run is being torn down: detach the recorder and drop redo history unless it is our own rebuild.</summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
internal static class Patch_RunManager_CleanUp
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        ReplayRecorder.Detach();
        RewindEngine.OnRunCleanUp();
    }
}

/// <summary>Our own teardown does not need the replay written to disk; skip the serialization + file write.</summary>
[HarmonyPatch(typeof(CombatReplayWriter), nameof(CombatReplayWriter.WriteReplay))]
internal static class Patch_CombatReplayWriter_WriteReplay
{
    [HarmonyPrefix]
    public static bool Prefix(CombatReplayWriter __instance, bool stopRecording)
    {
        if (!RewindEngine.SkipReplayWrite)
            return true;
        if (stopRecording)
            __instance.StopRecording();
        return false;
    }
}

/// <summary>
/// During a rewind the combat room assets are already loaded (we were just in this encounter). The game's
/// room preload unloads "missed" cache entries and runs a full GC.Collect; skip it while replaying.
/// </summary>
[HarmonyPatch(typeof(PreloadManager), nameof(PreloadManager.LoadRoomCombatAssets))]
internal static class Patch_PreloadManager_LoadRoomCombatAssets
{
    [HarmonyPrefix]
    public static bool Prefix(ref Task __result)
    {
        if (!RewindEngine.ReplayModeActive)
            return true;
        __result = Task.CompletedTask;
        return false;
    }
}
