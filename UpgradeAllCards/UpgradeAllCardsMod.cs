using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace UpgradeAllCards;

/// <summary>
/// Grants all 3 egg relics, upgrades the starting deck, and removes
/// the eggs from the relic pool so they won't appear again mid-run.
/// </summary>
[ModInitializer("Initialize")]
public static class UpgradeAllCardsMod
{
    public static void Initialize()
    {
        var harmony = new Harmony("com.upgradeallcards.sts2");
        harmony.PatchAll(typeof(UpgradeAllCardsMod).Assembly);
    }

    /// <summary>
    /// The egg model IDs to remove from the relic grab bag.
    /// </summary>
    internal static readonly HashSet<ModelId> EggIds = new()
    {
        ModelDb.GetId<FrozenEgg>(),
        ModelDb.GetId<MoltenEgg>(),
        ModelDb.GetId<ToxicEgg>(),
    };
}

/// <summary>
/// After starting relics are populated, add the three egg relics.
/// </summary>
[HarmonyPatch(typeof(Player), "PopulateStartingRelics")]
public static class PatchAddEggs
{
    [HarmonyPostfix]
    public static void Postfix(Player __instance)
    {
        AddEggIfMissing<FrozenEgg>(__instance);
        AddEggIfMissing<MoltenEgg>(__instance);
        AddEggIfMissing<ToxicEgg>(__instance);
    }

    private static void AddEggIfMissing<T>(Player player) where T : RelicModel
    {
        if (player.GetRelic<T>() != null)
            return;

        var relic = ModelDb.Relic<T>().ToMutable();
        relic.FloorAddedToDeck = 1;
        player.AddRelicInternal(relic);
    }
}

/// <summary>
/// After the starting deck is populated, upgrade every card in it.
/// The eggs handle mid-run cards, but the starting deck needs this patch.
/// </summary>
[HarmonyPatch(typeof(Player), "PopulateStartingDeck")]
public static class PatchUpgradeStartingDeck
{
    [HarmonyPostfix]
    public static void Postfix(Player __instance)
    {
        foreach (var card in __instance.Deck.Cards)
        {
            while (card.IsUpgradable)
            {
                card.UpgradeInternal();
                card.FinalizeUpgradeInternal();
            }
        }
    }
}

/// <summary>
/// Removes the three eggs from a relic grab bag: from every rarity deque (Remove&lt;T&gt;) and from the
/// private "_originalRelics" list that RefreshRarity copies back into an exhausted deque.
/// There are two bags per run: the player's own (rewards, shops, events) and the run's shared bag
/// (treasure chests), which is populated through the IEnumerable overload.
/// </summary>
internal static class EggPool
{
    internal static void RemoveEggs(RelicGrabBag bag)
    {
        bag.Remove<FrozenEgg>();
        bag.Remove<MoltenEgg>();
        bag.Remove<ToxicEgg>();
        try
        {
            if (AccessTools.Field(typeof(RelicGrabBag), "_originalRelics")?.GetValue(bag) is List<RelicModel> originals)
                originals.RemoveAll(r => UpgradeAllCardsMod.EggIds.Contains(r.Id));
        }
        catch { }
    }
}

/// <summary>Player grab bag (card rewards, shops, events).</summary>
[HarmonyPatch(typeof(RelicGrabBag), "Populate", new[] { typeof(Player), typeof(Rng) })]
public static class PatchRemoveEggsFromPool
{
    [HarmonyPostfix]
    public static void Postfix(RelicGrabBag __instance) => EggPool.RemoveEggs(__instance);
}

/// <summary>Shared grab bag (treasure chests), populated from the shared relic pool at run start.</summary>
[HarmonyPatch(typeof(RelicGrabBag), "Populate", new[] { typeof(IEnumerable<RelicModel>), typeof(Rng) })]
public static class PatchRemoveEggsFromSharedPool
{
    [HarmonyPostfix]
    public static void Postfix(RelicGrabBag __instance) => EggPool.RemoveEggs(__instance);
}

/// <summary>Runs saved before this version: clean both bags when the save is loaded.</summary>
[HarmonyPatch(typeof(RelicGrabBag), "LoadFromSerializable")]
public static class PatchRemoveEggsOnLoad
{
    [HarmonyPostfix]
    public static void Postfix(RelicGrabBag __instance) => EggPool.RemoveEggs(__instance);
}

/// <summary>An exhausted rarity deque is refilled from the original list; keep the eggs out of the refill too.</summary>
[HarmonyPatch(typeof(RelicGrabBag), "RefreshRarity")]
public static class PatchRemoveEggsOnRefresh
{
    [HarmonyPostfix]
    public static void Postfix(RelicGrabBag __instance) => EggPool.RemoveEggs(__instance);
}
