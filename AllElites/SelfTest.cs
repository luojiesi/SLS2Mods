using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AllElites;

/// <summary>
/// Automated check, only when &lt;user data&gt;/logs/AllElites.selftest exists at the main menu:
/// starts an unsaved Ironclad run, checks the map has no monster nodes left, enters a row-1
/// node and a "?"-style monster room, expects elite fights, logs PASS/FAIL to
/// logs/AllElites.log and quits.
/// </summary>
internal static class SelfTest
{
    private static string LogDir => System.IO.Path.Combine(OS.GetUserDataDir(), "logs");
    private static string FlagPath => System.IO.Path.Combine(LogDir, "AllElites.selftest");
    private static string LogPath => System.IO.Path.Combine(LogDir, "AllElites.log");
    private static bool _started;

    private static void Write(string msg)
    {
        try { System.IO.File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{System.Environment.NewLine}"); } catch { }
        GD.Print($"[AllElites] {msg}");
    }

    public static void MaybeStart()
    {
        if (_started || !System.IO.File.Exists(FlagPath)) return;
        _started = true;
        try { System.IO.File.Delete(FlagPath); System.IO.File.WriteAllText(LogPath, ""); } catch { }
        TaskHelper.RunSafely(Run());
    }

    private static async Task Frames(int n)
    {
        var game = NGame.Instance!;
        for (int i = 0; i < n; i++)
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static void Shot(string name)
    {
        try { NGame.Instance!.GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(LogDir, name + ".png")); }
        catch (Exception ex) { Write($"  screenshot {name} failed: {ex.Message}"); }
    }

    private static async Task Run()
    {
        bool pass = false;
        try
        {
            await Frames(60);
            Write("SELFTEST: starting an unsaved Ironclad run");
            var runState = await NGame.Instance!.StartNewSingleplayerRun(
                ModelDb.Character<Ironclad>(), shouldSave: false, ActModel.GetDefaultList(),
                Array.Empty<ModifierModel>(), "ALLELITES", GameMode.Standard);
            await Frames(30);

            var points = runState.Map.GetAllMapPoints().ToList();
            int monsters = points.Count(p => p.PointType == MapPointType.Monster);
            int elites = points.Count(p => p.PointType == MapPointType.Elite);
            Write($"  map: {points.Count} points, monster={monsters}, elite={elites}");

            NMapScreen.Instance?.Open();
            await Frames(120);
            Shot("AllElites_map");

            // A "?" node that rolls a fight goes straight through CreateRoom(Monster).
            var createRoom = AccessTools.Method(typeof(RunManager), "CreateRoom");
            var unknownRoom = (AbstractRoom)createRoom.Invoke(RunManager.Instance,
                new object?[] { RoomType.Monster, MapPointType.Unknown, null })!;
            string unknownEnc = (unknownRoom as CombatRoom)?.Encounter.Id.Entry ?? "-";
            Write($"  CreateRoom(Monster): {unknownRoom.RoomType} {unknownEnc}");

            // Walk into a real row-1 node (vanilla: always a monster).
            var target = points.Where(p => p.coord.row == 1).OrderBy(p => p.coord.col).First();
            Write($"  entering {target.coord.col},{target.coord.row} ({target.PointType})");
            await RunManager.Instance.EnterMapCoord(target.coord);
            await Frames(60);
            var room = runState.CurrentRoom;
            string enc = (room as CombatRoom)?.Encounter.Id.Entry ?? "-";
            Write($"  current room: {room?.RoomType} {enc}");
            await Frames(120);
            Shot("AllElites_fight");

            pass = monsters == 0 && elites > 0
                && unknownRoom.RoomType == RoomType.Elite
                && room is CombatRoom && room.RoomType == RoomType.Elite;
        }
        catch (Exception ex)
        {
            Write($"SELFTEST EXCEPTION: {ex}");
        }
        Write(pass ? "SELFTEST RESULT: PASS" : "SELFTEST RESULT: FAIL");
        await Frames(30);
        NGame.Instance?.GetTree().Quit();
    }
}

[HarmonyPatch(typeof(NMainMenu), "_Ready")]
internal static class Patch_NMainMenu_Ready
{
    [HarmonyPostfix]
    public static void Postfix() => SelfTest.MaybeStart();
}
