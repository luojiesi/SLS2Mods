using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace RngPredictor;

/// <summary>
/// Side-effect-free copy of the player's <see cref="RelicGrabBag"/>: mirrors <c>RelicGrabBag.PullFromFront</c>
/// (allowed-only deques, rarity fall-through common → uncommon → rare → multiplayer fallback → Circlet) on
/// private copies, so several pulls in a row (This or That, Ranwid, Punch-Off, Wongo's bargain bin, ...) can be
/// predicted without touching the real bag. <see cref="PullRolled"/> is <c>RelicFactory.PullNextRelicFromFront(player)</c>
/// (one Rewards draw for the rarity), <see cref="Pull"/> the fixed-rarity + filter overload (no draw).
/// </summary>
internal sealed class RelicPeek
{
    private readonly Dictionary<RelicRarity, List<RelicModel>> _copies = new();
    private readonly List<RelicModel> _fallback;
    private readonly bool _refreshAllowed;

    public RelicPeek(Player p)
    {
        var bag = p.RelicGrabBag;
        var deques = HarmonyLib.AccessTools.FieldRefAccess<RelicGrabBag, Dictionary<RelicRarity, List<RelicModel>>>("_deques")(bag);
        var fallback = HarmonyLib.AccessTools.FieldRefAccess<RelicGrabBag, List<RelicModel>>("_mpFallbackDequeue")(bag);
        _refreshAllowed = HarmonyLib.AccessTools.FieldRefAccess<RelicGrabBag, bool>("_refreshAllowed")(bag);
        var runState = p.RunState;
        foreach (var (rarity, list) in deques)
            _copies[rarity] = list.Where(r => r.IsAllowed(runState)).ToList();
        _fallback = fallback.Where(r => r.IsAllowed(runState)).ToList();
    }

    /// <summary>Mirror of the rarity roll in RelicFactory.PullNextRelicFromFront(player): one Rewards draw.</summary>
    public static RelicRarity RollRarity(Rng rewards)
    {
        float f = rewards.NextFloat();
        return f < 0.5f ? RelicRarity.Common : (f < 0.83f ? RelicRarity.Uncommon : RelicRarity.Rare);
    }

    public (RelicModel? relic, string note) PullRolled(Rng rewards) => Pull(RollRarity(rewards), _ => true);

    /// <summary>Mirror of RelicGrabBag.PullFromFront(rarity, filter, runState) on the copies. A null relic means the game would refill the pool (unpredictable).</summary>
    public (RelicModel? relic, string note) Pull(RelicRarity rarity, Func<RelicModel, bool> filter)
    {
        List<RelicModel>? list = _copies.TryGetValue(rarity, out var l) ? l : new List<RelicModel>();
        if (list.Count == 0 && _refreshAllowed) return (null, "pool refills");
        while (list != null && !list.Any(filter))
        {
            rarity = rarity switch
            {
                RelicRarity.Shop => RelicRarity.Common,
                RelicRarity.Common => RelicRarity.Uncommon,
                RelicRarity.Uncommon => RelicRarity.Rare,
                _ => RelicRarity.None,
            };
            list = rarity == RelicRarity.None ? null : (_copies.TryGetValue(rarity, out var l2) ? l2 : new List<RelicModel>());
        }
        if (list == null && _fallback.Any(filter)) list = _fallback;
        if (list == null) return (RelicFactory.FallbackRelic, "");
        var relic = list.First(filter);
        list.Remove(relic);
        return (relic, "");
    }
}
