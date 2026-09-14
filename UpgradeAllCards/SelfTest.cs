using System.Collections;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;

namespace UpgradeAllCards;

internal static class Log
{
    private static readonly string LogPath = System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "UpgradeAllCards.log");
    private static bool _cleared;

    internal static void Write(string msg)
    {
        try
        {
            if (!_cleared)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                System.IO.File.WriteAllText(LogPath, "");
                _cleared = true;
            }
            System.IO.File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{System.Environment.NewLine}");
            GD.Print($"[UpgradeAllCards] {msg}");
        }
        catch { }
    }
}

/// <summary>
/// Automated check, only when &lt;user data&gt;/logs/UpgradeAllCards.selftest exists at the main menu:
/// starts an unsaved Ironclad run, verifies the starting deck is upgraded, the three eggs are owned and
/// absent from the relic pool, logs PASS/FAIL to logs/UpgradeAllCards.log and quits.
/// </summary>
internal static class SelfTest
{
    private static string FlagPath => System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "UpgradeAllCards.selftest");
    private static bool _started;

    public static void MaybeStart()
    {
        if (_started || !System.IO.File.Exists(FlagPath)) return;
        _started = true;
        try { System.IO.File.Delete(FlagPath); } catch { }
        TaskHelper.RunSafely(Run());
    }

    private static async Task NextFrame()
    {
        var game = NGame.Instance!;
        await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static async Task Run()
    {
        bool pass = false;
        try
        {
            for (int i = 0; i < 60; i++) await NextFrame();
            Log.Write("SELFTEST: starting an unsaved Ironclad run");
            var runState = await NGame.Instance!.StartNewSingleplayerRun(
                ModelDb.Character<Ironclad>(), shouldSave: false, ActModel.GetDefaultList(),
                Array.Empty<ModifierModel>(), "UPGRADETEST", GameMode.Standard);
            for (int i = 0; i < 30; i++) await NextFrame();

            var player = runState.Players[0];
            int total = 0, upgraded = 0, notUpgradable = 0;
            foreach (var card in player.Deck.Cards)
            {
                total++;
                if (card.IsUpgraded) upgraded++;
                else if (!card.IsUpgradable) notUpgradable++;
                Log.Write($"  deck: {card.Id.Entry} upgraded={card.IsUpgraded} upgradable={card.IsUpgradable}");
            }
            bool deckOk = upgraded + notUpgradable == total && upgraded > 0;

            bool frozen = player.GetRelic<FrozenEgg>() != null;
            bool molten = player.GetRelic<MoltenEgg>() != null;
            bool toxic = player.GetRelic<ToxicEgg>() != null;
            Log.Write($"  relics: {string.Join(",", player.Relics.Select(r => r.Id.Entry))}");

            var bagIds = new List<string>();
            try
            {
                var deques = AccessTools.Field(typeof(RelicGrabBag), "_deques")?.GetValue(player.RelicGrabBag) as IEnumerable;
                if (deques != null)
                    foreach (var entry in deques)
                    {
                        var value = entry.GetType().GetProperty("Value")?.GetValue(entry) as IEnumerable;
                        if (value == null) continue;
                        foreach (var relic in value)
                            if (relic is RelicModel rm) bagIds.Add(rm.Id.Entry);
                    }
            }
            catch (Exception ex) { Log.Write($"  grab bag inspection failed: {ex.Message}"); }
            var eggIds = UpgradeAllCardsMod.EggIds.Select(e => e.Entry).ToHashSet();
            var eggsInBag = bagIds.Where(eggIds.Contains).ToList();
            Log.Write($"  grab bag: {bagIds.Count} relics, eggs in bag: {(eggsInBag.Count == 0 ? "none" : string.Join(",", eggsInBag))}");

            Log.Write($"SELFTEST: deck {upgraded}/{total} upgraded ({notUpgradable} not upgradable) -> {(deckOk ? "ok" : "FAIL")}; eggs owned frozen={frozen} molten={molten} toxic={toxic}; eggs in pool={eggsInBag.Count}");
            pass = deckOk && frozen && molten && toxic && eggsInBag.Count == 0 && bagIds.Count > 0;
        }
        catch (Exception ex)
        {
            Log.Write($"SELFTEST EXCEPTION: {ex}");
        }
        Log.Write(pass ? "SELFTEST RESULT: PASS" : "SELFTEST RESULT: FAIL");
        for (int i = 0; i < 30; i++) await NextFrame();
        NGame.Instance?.GetTree().Quit();
    }
}

[HarmonyPatch(typeof(NMainMenu), "_Ready")]
internal static class Patch_NMainMenu_Ready
{
    [HarmonyPostfix]
    public static void Postfix() => SelfTest.MaybeStart();
}
