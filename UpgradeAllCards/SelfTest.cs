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

    private static List<string> BagIds(RelicGrabBag bag)
    {
        var ids = new List<string>();
        try
        {
            var deques = AccessTools.Field(typeof(RelicGrabBag), "_deques")?.GetValue(bag) as IEnumerable;
            if (deques != null)
                foreach (var entry in deques)
                {
                    var value = entry.GetType().GetProperty("Value")?.GetValue(entry) as IEnumerable;
                    if (value == null) continue;
                    foreach (var relic in value)
                        if (relic is RelicModel rm) ids.Add(rm.Id.Entry);
                }
        }
        catch (Exception ex) { Log.Write($"  grab bag inspection failed: {ex.Message}"); }
        return ids;
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

            var eggIds = UpgradeAllCardsMod.EggIds.Select(e => e.Entry).ToHashSet();
            var bagIds = BagIds(player.RelicGrabBag);
            var eggsInBag = bagIds.Where(eggIds.Contains).ToList();
            Log.Write($"  grab bag: {bagIds.Count} relics, eggs in bag: {(eggsInBag.Count == 0 ? "none" : string.Join(",", eggsInBag))}");

            // Shared bag = treasure chests (populated through the IEnumerable overload).
            var sharedIds = BagIds(runState.SharedRelicGrabBag);
            var eggsInShared = sharedIds.Where(eggIds.Contains).ToList();
            Log.Write($"  shared grab bag (chests): {sharedIds.Count} relics, eggs: {(eggsInShared.Count == 0 ? "none" : string.Join(",", eggsInShared))}");

            // Refill of an exhausted deque must not bring the eggs back.
            var eggsAfterRefresh = new List<string>();
            try
            {
                foreach (var rarity in new[] { MegaCrit.Sts2.Core.Entities.Relics.RelicRarity.Common, MegaCrit.Sts2.Core.Entities.Relics.RelicRarity.Uncommon, MegaCrit.Sts2.Core.Entities.Relics.RelicRarity.Rare })
                    AccessTools.Method(typeof(RelicGrabBag), "RefreshRarity")?.Invoke(player.RelicGrabBag, new object[] { rarity });
                eggsAfterRefresh = BagIds(player.RelicGrabBag).Where(eggIds.Contains).ToList();
                Log.Write($"  after refreshing every rarity: {BagIds(player.RelicGrabBag).Count} relics, eggs: {(eggsAfterRefresh.Count == 0 ? "none" : string.Join(",", eggsAfterRefresh))}");
            }
            catch (Exception ex) { Log.Write($"  refresh check failed: {ex.Message}"); eggsAfterRefresh.Add("ERROR"); }

            // A save written before the fix still lists the eggs: loading it must drop them.
            var eggsAfterLoad = new List<string>();
            try
            {
                var save = new MegaCrit.Sts2.Core.Saves.Runs.SerializableRelicGrabBag();
                save.RelicIdLists[MegaCrit.Sts2.Core.Entities.Relics.RelicRarity.Rare] = new List<ModelId> { ModelDb.GetId<FrozenEgg>(), ModelDb.GetId<BurningBlood>() };
                var loaded = RelicGrabBag.FromSerializable(save);
                var loadedIds = BagIds(loaded);
                eggsAfterLoad = loadedIds.Where(eggIds.Contains).ToList();
                Log.Write($"  loaded from a save containing FROZEN_EGG: {string.Join(",", loadedIds)}");
            }
            catch (Exception ex) { Log.Write($"  load check failed: {ex.Message}"); eggsAfterLoad.Add("ERROR"); }

            Log.Write($"SELFTEST: deck {upgraded}/{total} upgraded ({notUpgradable} not upgradable) -> {(deckOk ? "ok" : "FAIL")}; eggs owned frozen={frozen} molten={molten} toxic={toxic}; eggs in pool={eggsInBag.Count}");
            Log.Write($"SELFTEST: eggs in shared bag={eggsInShared.Count}, after refresh={eggsAfterRefresh.Count}, after load={eggsAfterLoad.Count}");
            pass = deckOk && frozen && molten && toxic && eggsInBag.Count == 0 && bagIds.Count > 0
                && eggsInShared.Count == 0 && sharedIds.Count > 0 && eggsAfterRefresh.Count == 0 && eggsAfterLoad.Count == 0;
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
