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
/// After the relic grab bag is populated, remove the three eggs so they
/// won't show up from chests, shops, or events during the run.
/// Uses the built-in Remove&lt;T&gt;() method which handles both _deques
/// and internal state correctly.
/// </summary>
[HarmonyPatch(typeof(RelicGrabBag), "Populate", new[] { typeof(Player), typeof(Rng) })]
public static class PatchRemoveEggsFromPool
{
    [HarmonyPostfix]
    public static void Postfix(RelicGrabBag __instance)
    {
        __instance.Remove<FrozenEgg>();
        __instance.Remove<MoltenEgg>();
        __instance.Remove<ToxicEgg>();
    }
}
