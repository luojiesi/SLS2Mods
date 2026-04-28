using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

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

    /// <summary>
    /// Quick restart = quit to menu + continue, without showing the menu.
    /// The game auto-saves when entering each room, so loading that save
    /// and re-entering the room reproduces the exact same encounter.
    /// </summary>
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
            // 1. Load the auto-save (written when the current room was entered)
            var saveResult = SaveManager.Instance.LoadRunSave();
            if (!saveResult.Success || saveResult.SaveData == null)
            {
                Log.Write("No save found, aborting");
                return;
            }
            var save = saveResult.SaveData;
            Log.Write($"Save loaded: act={save.CurrentActIndex} preFinished={save.PreFinishedRoom?.RoomType} visitedCoords={save.VisitedMapCoords.Count}");

            // When PreFinishedRoom is set, it means a room-completion save has
            // overwritten the room-entry save. This happens after:
            //   - Ancient event selection (Neow / act start): SaveRun(eventRoom)
            //   - Combat victory: SaveRun(combatRoom)
            // The game's CopyBackup() preserves the previous save as .backup,
            // which is the room-entry state (pre-combat HP, pre-selection).
            // Load the backup so F5 restarts from the beginning of the room.
            bool usedBackup = false;
            if (save.PreFinishedRoom != null)
            {
                Log.Write($"PreFinishedRoom detected ({save.PreFinishedRoom.RoomType}), loading backup save");
                var backupSave = LoadBackupSave();
                if (backupSave != null)
                {
                    save = backupSave;
                    usedBackup = true;
                    Log.Write($"Backup loaded: preFinished={save.PreFinishedRoom?.RoomType} visitedCoords={save.VisitedMapCoords.Count}");
                }
                else
                {
                    Log.Write("Backup load failed, using original save");
                }
            }

            // 2. Capture current room for non-completion restarts.
            //    For ? rooms, the room type (Event/Monster/etc) is determined by RNG.
            //    Passing the serialized room to LoadRun ensures the same room type;
            //    passing null would re-roll it and potentially change Event → Combat.
            SerializableRoom? capturedRoom = null;
            if (!usedBackup)
            {
                try
                {
                    var room = runManager.DebugOnlyGetState()?.CurrentRoom;
                    if (room != null)
                    {
                        var serializedRoom = room.ToSerializable();
                        if (serializedRoom.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss or RoomType.Event)
                        {
                            capturedRoom = serializedRoom;
                            Log.Write($"Captured current room: type={capturedRoom.RoomType} modelId={capturedRoom?.EncounterId}");
                        }
                        else
                        {
                            Log.Write($"Current room type {serializedRoom.RoomType} uses map-point recreation");
                        }
                    }
                }
                catch (Exception ex) { Log.Write($"Room capture error: {ex.Message}"); }
            }

            // Log RNG + map point state for diagnosing room type consistency
            try
            {
                var rngSet = runManager.DebugOnlyGetState()?.Rng;
                if (rngSet != null)
                {
                    var rngs = AccessTools.Field(rngSet.GetType(), "_rngs")?.GetValue(rngSet)
                        as System.Collections.IDictionary;
                    if (rngs != null)
                    {
                        foreach (System.Collections.DictionaryEntry entry in rngs)
                        {
                            var rng = entry.Value;
                            var seed = AccessTools.Property(rng!.GetType(), "Seed")?.GetValue(rng);
                            var counter = AccessTools.Property(rng.GetType(), "Counter")?.GetValue(rng);
                            Log.Write($"RNG {entry.Key}: seed={seed} counter={counter}");
                        }
                    }
                }
                var odds = runManager.DebugOnlyGetState()?.Odds?.UnknownMapPoint;
                if (odds != null)
                {
                    Log.Write($"UnknownOdds: monster={odds.MonsterOdds:F3} elite={odds.EliteOdds:F3} treasure={odds.TreasureOdds:F3} shop={odds.ShopOdds:F3} event={odds.EventOdds:F3}");
                }
            }
            catch (Exception ex) { Log.Write($"RNG log error: {ex.Message}"); }

            // 3. Capture enemy positions before teardown (cosmetic: keeps enemies in same spots)
            var savedEnemyPositions = CaptureEnemyPositions();

            // 3. Tear down current run (mirrors NPauseMenu.CloseToMenu → NGame.ReturnToMainMenu)
            runManager.ActionQueueSet.Reset();
            NRunMusicController.Instance?.StopMusic();
            await NGame.Instance.Transition.FadeOut();
            runManager.CleanUp();
            Log.Write("Teardown complete");

            // 4. Reconstruct run state from save (mirrors NMainMenu.OnContinueButtonPressedAsync)
            var runState = RunState.FromSerializable(save);
            await runManager.SetUpSavedSinglePlayer(runState, save);
            NGame.Instance.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
            Log.Write($"RunState reconstructed: act={runState.Act} floor={runState.TotalFloor}");

            // 5. Load the run (mirrors NGame.LoadRun)
            //    For backup cases (Neow/post-combat): pass null to re-enter room fresh.
            //    For normal cases: pass capturedRoom to preserve the room type for ? rooms.
            //    Map drawings are restored by LoadRun via RunManager.MapDrawingsToLoad.
            Log.Write($"Calling LoadRun with room={(!usedBackup ? capturedRoom?.RoomType.ToString() : "null")}");
            await NGame.Instance.LoadRun(runState, usedBackup ? null : capturedRoom);
            // Log what room we actually got
            try
            {
                var actualRoom = RunManager.Instance.DebugOnlyGetState()?.CurrentRoom;
                Log.Write($"LoadRun complete: actualRoom={actualRoom?.GetType().Name} type={actualRoom?.RoomType} modelId={actualRoom?.ModelId}");
            }
            catch { Log.Write("LoadRun complete"); }

            // When we loaded from backup, overwrite current_run.save with the backup's
            // content so the backup file stays correct on subsequent F5s. We can't use
            // SaveRun(null) because LoadRun already incremented TotalFloor. By copying
            // the raw backup bytes, we preserve the original room-entry state.
            if (usedBackup)
                CopyBackupToMainSave();

            // 6. Restore enemy positions (cosmetic — RandomizeEnemyScalesAndHues shifts them)
            RestoreEnemyPositions(savedEnemyPositions);

            // 7. Fade back in
            await NGame.Instance.Transition.FadeIn();
            Log.Write("=== QuickRestart complete ===");

            // 8. Restore map marker after scene settles
            //    LoadRun → GenerateMap → SetMap hides the marker; re-init it.
            if (runState.VisitedMapCoords.Count > 0)
            {
                var lastCoord = runState.VisitedMapCoords[runState.VisitedMapCoords.Count - 1];
                await NGame.Instance.ToSignal(NGame.Instance.GetTree(), SceneTree.SignalName.ProcessFrame);
                NMapScreen.Instance?.InitMarker(lastCoord);
            }
        }
        finally
        {
            _isRestarting = false;
        }
    }

    /// <summary>
    /// Copy the .backup file over current_run.save so CopyBackup preserves
    /// the correct room-entry state on subsequent saves.
    /// Uses Godot.Godot.FileAccess directly (same approach as the game's CopyBackup).
    /// </summary>
    private static void CopyBackupToMainSave()
    {
        try
        {
            var savePath = GetCurrentRunSavePath();
            if (savePath == null) return;

            var backupPath = savePath + ".backup";

            // Use the game's ISaveStore for path resolution (handles user:// prefix)
            var runSaveManager = AccessTools.Field(typeof(SaveManager), "_runSaveManager")
                ?.GetValue(SaveManager.Instance);
            if (runSaveManager == null) return;

            var store = AccessTools.Field(runSaveManager.GetType(), "_saveStore")
                ?.GetValue(runSaveManager);
            if (store == null) return;

            // ReadFile returns string (JSON text), WriteFile(string,string) writes text
            var readMethod = AccessTools.Method(store.GetType(), "ReadFile", new[] { typeof(string) });
            var writeMethod = AccessTools.Method(store.GetType(), "WriteFile",
                new[] { typeof(string), typeof(string) });
            if (readMethod == null || writeMethod == null)
            {
                Log.Write("CopyBackupToMainSave: ReadFile/WriteFile methods not found");
                return;
            }

            var content = (string?)readMethod.Invoke(store, new object[] { backupPath });
            if (content == null)
            {
                Log.Write($"CopyBackupToMainSave: backup file empty or missing");
                return;
            }

            writeMethod.Invoke(store, new object[] { savePath, content });
            Log.Write("Copied backup to main save for backup consistency");
        }
        catch (Exception ex) { Log.Write($"CopyBackupToMainSave error: {ex.Message}"); }
    }

    /// <summary>
    /// Get the full Godot path to current_run.save via reflection.
    /// </summary>
    private static string? GetCurrentRunSavePath()
    {
        try
        {
            var runSaveManager = AccessTools.Field(typeof(SaveManager), "_runSaveManager")
                ?.GetValue(SaveManager.Instance);
            if (runSaveManager == null) return null;

            return (string?)AccessTools.Property(runSaveManager.GetType(), "CurrentRunSavePath")
                ?.GetValue(runSaveManager);
        }
        catch { return null; }
    }

    /// <summary>
    /// Load the .backup save file using the game's own deserialization pipeline.
    /// The game creates a .backup before every save write (CopyBackup), so it
    /// contains the previous version of current_run.save.
    /// </summary>
    private static SerializableRun? LoadBackupSave()
    {
        try
        {
            var savePath = GetCurrentRunSavePath();
            if (savePath == null) return null;
            var backupPath = savePath + ".backup";

            // Use MigrationManager.LoadSave<SerializableRun>(backupPath) — same pipeline as LoadRunSave
            var runSaveManager = AccessTools.Field(typeof(SaveManager), "_runSaveManager")
                ?.GetValue(SaveManager.Instance);
            if (runSaveManager == null) return null;

            var migrationManager = AccessTools.Field(runSaveManager.GetType(), "_migrationManager")
                ?.GetValue(runSaveManager);
            if (migrationManager == null) return null;

            var loadMethod = AccessTools.Method(migrationManager.GetType(), "LoadSave", new[] { typeof(string) });
            var genericLoad = loadMethod?.MakeGenericMethod(typeof(SerializableRun));
            if (genericLoad == null) return null;

            var result = genericLoad.Invoke(migrationManager, new object[] { backupPath });
            if (result == null) return null;

            var successProp = result.GetType().GetProperty("Success");
            var dataProp = result.GetType().GetProperty("SaveData");
            if (successProp != null && (bool)successProp.GetValue(result)!)
                return (SerializableRun?)dataProp?.GetValue(result);

            Log.Write("Backup save exists but failed to deserialize");
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"LoadBackupSave error: {ex.Message}");
            return null;
        }
    }

    private static List<(uint combatId, Vector2 pos)> CaptureEnemyPositions()
    {
        var positions = new List<(uint combatId, Vector2 pos)>();
        try
        {
            var combatRoom = NCombatRoom.Instance;
            if (combatRoom == null) return positions;

            // Capture alive enemies
            foreach (var node in combatRoom.CreatureNodes)
            {
                if (node.Entity.Side == CombatSide.Enemy)
                    positions.Add((node.Entity.CombatId ?? 0, node.Position));
            }
            // Capture dead/dying enemies (moved to RemovingCreatureNodes)
            foreach (var node in combatRoom.RemovingCreatureNodes)
            {
                if (GodotObject.IsInstanceValid(node) && node.Entity.Side == CombatSide.Enemy)
                    positions.Add((node.Entity.CombatId ?? 0, node.Position));
            }
            // Sort by CombatId to match post-restart ordering (all enemies alive, ordered by id)
            positions.Sort((a, b) => a.combatId.CompareTo(b.combatId));
            Log.Write($"Captured {positions.Count} enemy positions");
        }
        catch (Exception ex) { Log.Write($"Enemy position capture error: {ex.Message}"); }
        return positions;
    }

    private static void RestoreEnemyPositions(List<(uint combatId, Vector2 pos)> savedPositions)
    {
        if (savedPositions.Count == 0) return;
        try
        {
            var combatRoom = NCombatRoom.Instance;
            if (combatRoom == null) return;

            var enemyNodes = combatRoom.CreatureNodes
                .Where(n => n.Entity.Side == CombatSide.Enemy).ToList();

            if (enemyNodes.Count != savedPositions.Count)
            {
                Log.Write($"Position restore skipped: count mismatch ({enemyNodes.Count} vs {savedPositions.Count})");
                return;
            }
            for (int i = 0; i < enemyNodes.Count; i++)
            {
                enemyNodes[i].Position = savedPositions[i].pos;
            }
            Log.Write($"Restored {enemyNodes.Count} enemy positions");
        }
        catch (Exception ex) { Log.Write($"Enemy position restore error: {ex.Message}"); }
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
