using System.Linq;
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

internal static class Log
{
    private static readonly string LogPath = System.IO.Path.Combine(
        OS.GetUserDataDir(), "logs", "QuickRestart.log");

    private static bool _cleared;

    public static void Write(string message)
    {
        try
        {
            if (!_cleared)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                System.IO.File.WriteAllText(LogPath,
                    $"[{System.DateTime.Now:HH:mm:ss.fff}] === Log cleared (new session) ==={System.Environment.NewLine}");
                _cleared = true;
            }
            var line = $"[{System.DateTime.Now:HH:mm:ss.fff}] {message}{System.Environment.NewLine}";
            System.IO.File.AppendAllText(LogPath, line);
            GD.Print($"[QuickRestart] {message}");
        }
        catch { }
    }
}

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
        Log.Write("=== QuickRestart triggered ===");
        try
        {
            // Load the save (written when the current room was entered)
            var saveResult = SaveManager.Instance.LoadRunSave();
            if (!saveResult.Success || saveResult.SaveData == null)
                return;

            var save = saveResult.SaveData;
            Log.Write($"Save loaded: EventsSeen=[{string.Join(", ", save.EventsSeen)}] PreFinishedRoom={save.PreFinishedRoom?.RoomType}");

            // Capture current room before teardown.
            // The game saves BEFORE the room is created, so for event rooms
            // save.PreFinishedRoom is null. Without this fix, reloading would
            // re-roll the room type and event selection, potentially producing
            // a different event.
            var preFinishedRoom = save.PreFinishedRoom;
            if (preFinishedRoom != null)
            {
                Log.Write($"Save has PreFinishedRoom: type={preFinishedRoom.RoomType} modelId={preFinishedRoom.EncounterId}");
            }
            else
            {
                try
                {
                    var runState0 = runManager.DebugOnlyGetState();
                    var currentRoom = runState0?.CurrentRoom;
                    if (currentRoom != null)
                    {
                        Log.Write($"Current room: class={currentRoom.GetType().Name} type={currentRoom.RoomType} modelId={currentRoom.ModelId}");
                        preFinishedRoom = currentRoom.ToSerializable();
                        Log.Write($"Serialized room: type={preFinishedRoom?.RoomType} encounterId={preFinishedRoom?.EncounterId}");
                    }
                    else
                    {
                        Log.Write("No current room found");
                    }
                }
                catch (Exception ex) { Log.Write($"Room capture error: {ex}"); }
            }

            // Capture map drawings before teardown (preserves routes drawn since last auto-save)
            var savedDrawings = NRun.Instance?.GlobalUi?.MapScreen?.Drawings?.GetSerializableMapDrawings();

            // Capture enemy positions before teardown.
            // We need ALL enemy positions (including dead/dying) to restore correctly.
            // CreatureNodes excludes dead enemies (moved to _removingCreatureNodes),
            // so we also search there. Without this, killing a monster then restarting
            // causes position mismatch: fewer saved positions than enemies after restart.
            var savedEnemyPositions = new List<(uint combatId, Vector2 pos, bool alive)>();
            try
            {
                var combatRoom = NCombatRoom.Instance;
                if (combatRoom != null)
                {
                    // Alive enemies from active list
                    foreach (var node in combatRoom.CreatureNodes)
                    {
                        if (node.Entity.Side == CombatSide.Enemy)
                        {
                            savedEnemyPositions.Add((node.Entity.CombatId ?? 0, node.Position, true));
                            Log.Write($"Enemy position (alive): id={node.Entity.CombatId} pos={node.Position}");
                        }
                    }
                    // Dead/dying enemies from removing list
                    foreach (var node in combatRoom.RemovingCreatureNodes)
                    {
                        if (GodotObject.IsInstanceValid(node) && node.Entity.Side == CombatSide.Enemy)
                        {
                            savedEnemyPositions.Add((node.Entity.CombatId ?? 0, node.Position, false));
                            Log.Write($"Enemy position (dead/dying): id={node.Entity.CombatId} pos={node.Position}");
                        }
                    }
                    // Sort by CombatId so ordering matches post-restart (all enemies alive, ordered by id)
                    savedEnemyPositions.Sort((a, b) => a.combatId.CompareTo(b.combatId));
                    Log.Write($"Captured {savedEnemyPositions.Count} enemy positions ({savedEnemyPositions.Count(p => p.alive)} alive, {savedEnemyPositions.Count(p => !p.alive)} dead), sorted by CombatId");
                }
            }
            catch (Exception ex) { Log.Write($"Enemy position capture error: {ex.Message}"); }

            // Tear down current run (mirrors NPauseMenu.CloseToMenu + NGame.ReturnToMainMenu)
            Log.Write("Tearing down current run...");
            runManager.ActionQueueSet.Reset();
            NRunMusicController.Instance?.StopMusic();
            await NGame.Instance.Transition.FadeOut();
            runManager.CleanUp();
            Log.Write("CleanUp complete");

            // Reconstruct run state from save (mirrors NMainMenu.OnContinueButtonPressedAsync)
            var runState = RunState.FromSerializable(save);
            runManager.SetUpSavedSinglePlayer(runState, save);
            NGame.Instance.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
            Log.Write($"RunState reconstructed: act={runState.Act} floor={runState.TotalFloor} visitedCoords={runState.VisitedMapCoords.Count}");

            // Load back into the run (mirrors NGame.LoadRun)
            await PreloadManager.LoadRunAssets(
                runState.Players.Select(p => p.Character));
            await PreloadManager.LoadActAssets(runState.Act);
            runManager.Launch();
            NGame.Instance.RootSceneContainer.SetCurrentScene(NRun.Create(runState));
            await runManager.GenerateMap();
            Log.Write("Map generated, loading into room...");

            // Deserialize the room.
            // FromSerializable only handles Monster/Elite/Boss/Event.
            // For Shop, Treasure, RestSite the game will re-create the room
            // naturally from the map point type when preFinishedRoom is null.
            AbstractRoom? deserializedRoom = null;
            try
            {
                if (preFinishedRoom != null)
                {
                    Log.Write($"Deserializing room: type={preFinishedRoom.RoomType} modelId={preFinishedRoom.EncounterId}");
                    deserializedRoom = AbstractRoom.FromSerializable(preFinishedRoom, runState);
                    Log.Write($"Deserialized room: {deserializedRoom?.GetType().Name} type={deserializedRoom?.RoomType}");
                }
                else
                {
                    Log.Write("No preFinishedRoom to deserialize (null)");
                }
            }
            catch (Exception ex) { Log.Write($"Room deserialization FAILED: {ex}"); }

            // Fix TotalFloor for encounter RNG: the game saves BEFORE AppendToMapPointHistory,
            // but StartCombat runs AFTER it. When we pass preFinishedRoom to LoadIntoLatestMapCoord,
            // it skips AppendToMapPointHistory, making TotalFloor 1 less than the original flow.
            // This changes the encounter RNG seed, causing different monster generation.
            if (deserializedRoom != null && runState.VisitedMapCoords.Count > 0)
            {
                try
                {
                    var lastCoord = runState.VisitedMapCoords[runState.VisitedMapCoords.Count - 1];
                    var mapPoint = runState.Map.GetPoint(lastCoord);
                    if (mapPoint != null)
                        runState.AppendToMapPointHistory(mapPoint.PointType, deserializedRoom.RoomType, deserializedRoom.ModelId);
                }
                catch { }
            }

            // Fix duplicate events: the save is written BEFORE PullNextEvent runs,
            // so the restored _visitedEventIds doesn't include the current event.
            // EnterRoomInternal will call MarkRoomVisited (advancing eventsVisited counter),
            // but won't add the event to _visitedEventIds. Without this fix, the same event
            // can be selected again when another Unknown room rolls Event.
            if (deserializedRoom is EventRoom eventRoom)
            {
                Log.Write($"Adding event to visited: {eventRoom.CanonicalEvent.Id}");
                try { runState.AddVisitedEvent(eventRoom.CanonicalEvent); }
                catch (Exception ex) { Log.Write($"AddVisitedEvent error: {ex.Message}"); }
            }

            Log.Write($"Before LoadIntoLatestMapCoord: room={deserializedRoom?.RoomType} visitedEvents=[{string.Join(", ", runState.VisitedEventIds)}]");

            Log.Write($"LoadIntoLatestMapCoord: deserializedRoom={deserializedRoom?.RoomType} modelId={deserializedRoom?.ModelId}");
            await runManager.LoadIntoLatestMapCoord(deserializedRoom);
            Log.Write("LoadIntoLatestMapCoord complete");

            // Restore enemy positions (RandomizeEnemyScalesAndHues may shift them slightly).
            // After restart all enemies are alive, so we use the full saved list (alive + dead).
            // Only restore if counts match — a mismatch means the encounter changed.
            try
            {
                var combatRoom = NCombatRoom.Instance;
                if (combatRoom != null)
                {
                    var enemyNodes = combatRoom.CreatureNodes
                        .Where(n => n.Entity.Side == CombatSide.Enemy).ToList();
                    Log.Write($"Post-restart enemy count: {enemyNodes.Count}, saved positions: {savedEnemyPositions.Count}");
                    if (enemyNodes.Count == savedEnemyPositions.Count)
                    {
                        for (int i = 0; i < enemyNodes.Count; i++)
                        {
                            var oldPos = enemyNodes[i].Position;
                            enemyNodes[i].Position = savedEnemyPositions[i].pos;
                            Log.Write($"Restored enemy id={enemyNodes[i].Entity.CombatId}: {oldPos} -> {savedEnemyPositions[i].pos} (saved id={savedEnemyPositions[i].combatId})");
                        }
                    }
                    else
                    {
                        Log.Write($"Skipping position restore: count mismatch ({enemyNodes.Count} vs {savedEnemyPositions.Count})");
                    }
                }
            }
            catch (Exception ex) { Log.Write($"Enemy position restore error: {ex.Message}"); }

            // Restore map route drawings (GenerateMap calls SetMap which clears them)
            if (savedDrawings != null)
            {
                NRun.Instance?.GlobalUi?.MapScreen?.Drawings?.LoadDrawings(savedDrawings);
            }

            Log.Write("Fading in...");
            await NGame.Instance.Transition.FadeIn();
            Log.Write("=== QuickRestart complete ===");

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
