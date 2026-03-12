using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace QuickRestart;

[ModInitializer("Initialize")]
public static class QuickRestartMod
{
    private static bool _isRestarting;

    public static void Initialize()
    {
        var harmony = new Harmony("com.quickrestart.sts2");
        harmony.PatchAll(typeof(QuickRestartMod).Assembly);
    }

    public static async Task DoQuickRestart()
    {
        if (_isRestarting)
            return;

        var runManager = RunManager.Instance;
        if (runManager is not { IsInProgress: true })
            return;

        _isRestarting = true;
        try
        {
            // Load the save (written when the current room was entered)
            var saveResult = SaveManager.Instance.LoadRunSave();
            if (!saveResult.Success || saveResult.SaveData == null)
                return;

            var save = saveResult.SaveData;

            // Capture current room before teardown.
            // The game saves BEFORE the room is created, so for event rooms
            // save.PreFinishedRoom is null. Without this fix, reloading would
            // re-roll the room type and event selection, potentially producing
            // a different event.
            var preFinishedRoom = save.PreFinishedRoom;
            if (preFinishedRoom == null)
            {
                try
                {
                    var runState0 = runManager.DebugOnlyGetState();
                    var currentRoom = runState0?.CurrentRoom;
                    if (currentRoom != null)
                        preFinishedRoom = currentRoom.ToSerializable();
                }
                catch { }
            }

            // Capture map drawings before teardown (preserves routes drawn since last auto-save)
            var savedDrawings = NRun.Instance?.GlobalUi?.MapScreen?.Drawings?.GetSerializableMapDrawings();

            // Capture enemy positions before teardown (sprite bounds can vary between loads,
            // causing auto-layout to produce slightly different positions)
            var savedEnemyPositions = new List<Vector2>();
            try
            {
                var combatRoom = NCombatRoom.Instance;
                if (combatRoom != null)
                {
                    foreach (var node in combatRoom.CreatureNodes)
                    {
                        if (node.Entity.Side == CombatSide.Enemy)
                            savedEnemyPositions.Add(node.Position);
                    }
                }
            }
            catch { }

            // Tear down current run (mirrors NPauseMenu.CloseToMenu + NGame.ReturnToMainMenu)
            runManager.ActionQueueSet.Reset();
            NRunMusicController.Instance?.StopMusic();
            await NGame.Instance.Transition.FadeOut();
            runManager.CleanUp();

            // Reconstruct run state from save (mirrors NMainMenu.OnContinueButtonPressedAsync)
            var runState = RunState.FromSerializable(save);
            runManager.SetUpSavedSinglePlayer(runState, save);
            NGame.Instance.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());

            // Load back into the run (mirrors NGame.LoadRun)
            await PreloadManager.LoadRunAssets(
                runState.Players.Select(p => p.Character));
            await PreloadManager.LoadActAssets(runState.Act);
            runManager.Launch();
            NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(runState));
            await runManager.GenerateMap();

            // Deserialize the room.
            // FromSerializable only handles Monster/Elite/Boss/Event.
            // For Shop, Treasure, RestSite the game will re-create the room
            // naturally from the map point type when preFinishedRoom is null.
            AbstractRoom? deserializedRoom = null;
            try
            {
                deserializedRoom = AbstractRoom.FromSerializable(preFinishedRoom, runState);
            }
            catch { }

            await runManager.LoadIntoLatestMapCoord(deserializedRoom);

            // Restore enemy positions to match the original layout
            if (savedEnemyPositions.Count > 0)
            {
                try
                {
                    var combatRoom = NCombatRoom.Instance;
                    if (combatRoom != null)
                    {
                        int idx = 0;
                        foreach (var node in combatRoom.CreatureNodes)
                        {
                            if (node.Entity.Side == CombatSide.Enemy && idx < savedEnemyPositions.Count)
                            {
                                node.Position = savedEnemyPositions[idx];
                                idx++;
                            }
                        }
                    }
                }
                catch { }
            }

            // Restore map route drawings (GenerateMap calls SetMap which clears them)
            if (savedDrawings != null)
            {
                NRun.Instance?.GlobalUi?.MapScreen?.Drawings?.LoadDrawings(savedDrawings);
            }

            await NGame.Instance.Transition.FadeIn();

            // Restore map marker AFTER everything is loaded and faded in.
            // SetMap() hides it, and Open() skips placement for boss/starting rows.
            // Must be deferred so the map screen has finished processing.
            if (runState.VisitedMapCoords.Count > 0)
            {
                var lastCoord = runState.VisitedMapCoords[runState.VisitedMapCoords.Count - 1];
                // Wait one frame for the scene tree to settle
                await NGame.Instance.ToSignal(NGame.Instance.GetTree(), SceneTree.SignalName.ProcessFrame);
                NMapScreen.Instance?.InitMarker(lastCoord);
            }
        }
        finally
        {
            _isRestarting = false;
        }
    }
}

[HarmonyPatch(typeof(NGame), "_Input")]
public static class PatchNGameInput
{
    [HarmonyPrefix]
    public static void Prefix(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey { Pressed: true } key
            && key.Keycode == Key.F5
            && !key.Echo
            && RunManager.Instance?.IsInProgress == true
            && RunManager.Instance?.IsGameOver != true
            && !NGame.Instance.Transition.InTransition)
        {
            TaskHelper.RunSafely(QuickRestartMod.DoQuickRestart());
        }
    }
}
