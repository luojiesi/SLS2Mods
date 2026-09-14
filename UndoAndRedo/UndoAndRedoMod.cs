using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Encounters;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using System.Reflection;
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
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{System.Environment.NewLine}";
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var fs = new System.IO.FileStream(LogPath, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                    fs.Write(bytes, 0, bytes.Length);
                    break;
                }
                catch (System.IO.IOException) { System.Threading.Thread.Sleep(5); }
            }
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

    /// <summary>Small, quiet toast at the top of the screen (no full-screen flash, no sound).</summary>
    public static void Toast(string text)
    {
        try
        {
            var game = NGame.Instance;
            if (game == null) return;
            var layer = new CanvasLayer { Layer = 110 };
            var label = new Label
            {
                Text = text,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            label.AddThemeFontSizeOverride("font_size", 28);
            label.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.95f));
            label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
            label.AddThemeConstantOverride("outline_size", 6);
            label.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
            label.OffsetTop = 40;
            label.OffsetLeft = -400;
            label.OffsetRight = 400;
            label.OffsetBottom = 90;
            layer.AddChild(label);
            game.AddChild(layer);
            var tween = label.CreateTween();
            tween.TweenInterval(1.0);
            tween.TweenProperty(label, "modulate:a", 0f, 0.5);
            tween.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(layer)) layer.QueueFree(); }));
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

/// <summary>
/// Hand card holders move with per-frame Lerp(target, delta * k) loops whose weight is not clamped, so any
/// time scale above 1 (or a slow frame) makes them diverge to infinity. While replaying, move them instantly.
/// </summary>
[HarmonyPatch(typeof(NHandCardHolder))]
internal static class Patch_NHandCardHolder_SnapWhileReplaying
{
    private static readonly FieldInfo? TargetPos = AccessTools.Field(typeof(NHandCardHolder), "_targetPosition");
    private static readonly FieldInfo? TargetAngle = AccessTools.Field(typeof(NHandCardHolder), "_targetAngle");
    private static readonly FieldInfo? TargetScale = AccessTools.Field(typeof(NHandCardHolder), "_targetScale");

    [HarmonyPrefix, HarmonyPatch("AnimPosition")]
    public static bool AnimPosition(NHandCardHolder __instance, ref Task __result)
    {
        if (!RewindEngine.ReplayModeActive || TargetPos == null) return true;
        if (TargetPos.GetValue(__instance) is Vector2 t) __instance.Position = t;
        __result = Task.CompletedTask;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch("AnimAngle")]
    public static bool AnimAngle(NHandCardHolder __instance, ref Task __result)
    {
        if (!RewindEngine.ReplayModeActive || TargetAngle == null) return true;
        if (TargetAngle.GetValue(__instance) is float a) __instance.RotationDegrees = a;
        __result = Task.CompletedTask;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch("AnimScale")]
    public static bool AnimScale(NHandCardHolder __instance, ref Task __result)
    {
        if (!RewindEngine.ReplayModeActive || TargetScale == null) return true;
        if (TargetScale.GetValue(__instance) is Vector2 sc) __instance.Scale = sc;
        __result = Task.CompletedTask;
        return false;
    }
}
