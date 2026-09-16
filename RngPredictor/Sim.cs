using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace RngPredictor;

/// <summary>
/// Side-effect-free re-implementations of the game's random generation helpers, run on a *copy* of the
/// relevant RNG stream. The game's <see cref="Rng"/> is fully described by (Seed, Counter), so
/// <c>new Rng(seed, counter)</c> is an exact clone; every helper here consumes the clone exactly the way
/// the real code consumes the live stream (same list construction order, same number of draws).
/// </summary>
internal static class Sim
{
    public static Rng Clone(Rng rng) => new Rng(rng.Seed, rng.Counter);

    public static IEnumerable<CardModel> CharacterPool(Player p) =>
        p.Character.CardPool.GetUnlockedCards(p.UnlockState, p.RunState.CardMultiplayerConstraint);

    public static IEnumerable<CardModel> ColorlessPool(Player p) =>
        ModelDb.CardPool<ColorlessCardPool>().GetUnlockedCards(p.UnlockState, p.RunState.CardMultiplayerConstraint);

    /// <summary>Mirror of the private CardFactory.FilterForPlayerCount.</summary>
    public static IEnumerable<CardModel> FilterForPlayerCount(IRunState runState, IEnumerable<CardModel> options)
    {
        if (runState.Players.Count > 1)
            return options.Where(c => c.MultiplayerConstraint != CardMultiplayerConstraint.SingleplayerOnly);
        return options.Where(c => c.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly);
    }

    /// <summary>Mirror of CardFactory.GetDistinctForCombat (Discovery, Attack Potion, ...) without creating cards.</summary>
    public static List<CardModel> DistinctForCombat(Player p, IEnumerable<CardModel> cards, int count, Rng rng)
    {
        var list = CardFactory.FilterForCombat(FilterForPlayerCount(p.RunState, cards)).ToList();
        list.UnstableShuffle(rng);
        return list.Take(count).ToList();
    }

    /// <summary>Mirror of CardFactory.GetForCombat (Metamorphosis, Jackpot, Stoke): duplicates allowed.</summary>
    public static List<CardModel> ForCombat(Player p, IEnumerable<CardModel> cards, int count, Rng rng)
    {
        var options = CardFactory.FilterForCombat(cards).ToList();
        options = FilterForPlayerCount(p.RunState, options).ToList();
        var result = new List<CardModel>();
        for (int i = 0; i < count; i++)
        {
            var c = rng.NextItem(options);
            if (c == null) break;
            result.Add(c);
        }
        return result;
    }

    /// <summary>Mirror of PotionFactory.CreateRandomPotion(s)(In|OutOf)Combat.</summary>
    public static List<PotionModel> RandomPotions(Player p, int count, Rng rng, bool inCombatPool)
    {
        IEnumerable<PotionModel> options = PotionFactory.GetPotionOptions(p, System.Array.Empty<PotionModel>());
        if (inCombatPool)
            options = options.Where(x => x.CanBeGeneratedInCombat);
        var list = options.ToList();
        var result = new List<PotionModel>();
        for (int i = 0; i < count; i++)
        {
            float f = rng.NextFloat();
            var rarity = f <= 0.1f ? PotionRarity.Rare : (f <= 0.35f ? PotionRarity.Uncommon : PotionRarity.Common);
            var item = rng.NextItem(list.Where(x => x.Rarity == rarity));
            if (item == null) break;
            result.Add(item);
            list.Remove(item);
        }
        return result;
    }

    /// <summary>Mirror of AttackCommand.TargetingRandomOpponents: one draw per hit over living opponents.</summary>
    public static List<Creature> RandomAttackTargets(Player p, int hits, Rng rng)
    {
        var result = new List<Creature>();
        var cs = p.Creature.CombatState;
        if (cs == null) return result;
        for (int i = 0; i < hits; i++)
        {
            var valid = cs.GetOpponentsOf(p.Creature).Where(c => c.IsAlive).ToList();
            if (valid.Count == 0) break;
            var t = rng.NextItem(valid);
            if (t == null) break;
            result.Add(t);
        }
        return result;
    }

    /// <summary>Mirror of <c>Rng.CombatTargets.NextItem(CombatState.HittableEnemies)</c> repeated.</summary>
    public static List<Creature> RandomHittableTargets(Player p, int hits, Rng rng)
    {
        var result = new List<Creature>();
        var cs = p.Creature.CombatState;
        if (cs == null) return result;
        for (int i = 0; i < hits; i++)
        {
            var valid = cs.HittableEnemies;
            if (valid.Count == 0) break;
            var t = rng.NextItem(valid);
            if (t == null) break;
            result.Add(t);
        }
        return result;
    }

    /// <summary>Order the game would produce when shuffling this set of cards (StableShuffle: order-independent).</summary>
    public static List<CardModel> ShuffleOrder(IEnumerable<CardModel> cards, Rng shuffleRng)
    {
        var list = cards.ToList();
        list.StableShuffle(shuffleRng);
        return list;
    }

    /// <summary>
    /// Simulate drawing <paramref name="count"/> cards: from the top of the draw pile, reshuffling the discard pile
    /// (with the Shuffle stream) when it runs out, honouring the hand limit.
    /// </summary>
    public static List<CardModel> SimulateDraw(Player p, int count, Rng shuffleRng, bool applyHandLimit = true)
    {
        var draw = PileType.Draw.GetPile(p).Cards.ToList();
        var discard = PileType.Discard.GetPile(p).Cards.ToList();
        int handCount = PileType.Hand.GetPile(p).Cards.Count;
        if (applyHandLimit)
            count = System.Math.Min(count, System.Math.Max(0, CardPile.MaxCardsInHand - handCount));
        var result = new List<CardModel>();
        for (int i = 0; i < count; i++)
        {
            if (draw.Count == 0)
            {
                if (discard.Count == 0) break;
                draw = ShuffleOrder(discard, shuffleRng);
                discard.Clear();
            }
            result.Add(draw[0]);
            draw.RemoveAt(0);
        }
        return result;
    }

    /// <summary>
    /// Mirror of CardFactory.CreateForReward(player, count, options) without creating cards or touching the
    /// rarity pity counter. Everything draws from the Rewards stream: rarity roll (unless Uniform), card pick,
    /// upgrade roll (unless NoUpgradeRoll), in that order per card.
    /// </summary>
    public static List<(CardModel card, bool upgraded)> CreateForReward(Player p, int count, CardCreationOptions options, Rng rewards)
    {
        var results = new List<(CardModel, bool)>();
        var blacklist = new List<CardModel>();
        var oddsState = p.PlayerOdds.CardRarity;
        float pity = oddsState.CurrentValue;
        decimal upgradeScaling = 0m;
        try { upgradeScaling = (decimal)(HarmonyLib.AccessTools.Property(typeof(CardFactory), "UpgradedCardOddScaling")?.GetValue(null) ?? 0m); } catch { }
        for (int i = 0; i < count; i++)
        {
            var opts = MegaCrit.Sts2.Core.Hooks.Hook.ModifyCardRewardCreationOptions(p.RunState, p, options);
            var pool = FilterForPlayerCount(p.RunState, opts.GetPossibleCards(p).Except(blacklist).ToList()).ToArray();
            IEnumerable<CardModel> items;
            if (opts.RarityOdds == CardRarityOddsType.Uniform)
            {
                items = pool.Where(c => c.Rarity != CardRarity.Basic && c.Rarity != CardRarity.Ancient);
            }
            else
            {
                var allowed = pool.Select(c => c.Rarity).ToHashSet();
                bool force = opts.Flags.HasFlag(CardCreationFlags.ForceRarityOddsChange)
                             || (opts.Source == CardCreationSource.Encounter && (uint)(opts.RarityOdds - 1) <= 2u);
                float roll = rewards.NextFloat();
                CardRarity rarity;
                if (force)
                {
                    float offset = opts.RarityOdds == CardRarityOddsType.BossEncounter ? 0f : pity;
                    float rareOdds = BaseOdds(opts.RarityOdds, CardRarity.Rare) + offset;
                    rarity = roll < rareOdds ? CardRarity.Rare
                        : roll < BaseOdds(opts.RarityOdds, CardRarity.Uncommon) + rareOdds ? CardRarity.Uncommon : CardRarity.Common;
                    pity = rarity == CardRarity.Rare ? -0.05f : System.Math.Min(pity + oddsState.RarityGrowth, 0.4f);
                }
                else
                {
                    rarity = roll < BaseOdds(opts.RarityOdds, CardRarity.Rare) ? CardRarity.Rare
                        : roll < BaseOdds(opts.RarityOdds, CardRarity.Uncommon) ? CardRarity.Uncommon : CardRarity.Common;
                }
                var original = rarity;
                while (!allowed.Contains(rarity) && rarity != CardRarity.None)
                {
                    rarity = rarity.GetNextHighestRarityWithWrapping();
                    if (rarity == original) { rarity = CardRarity.None; break; }
                }
                if (rarity == CardRarity.None) break;
                var selected = rarity;
                items = pool.Where(c => c.Rarity == selected);
            }
            var card = rewards.NextItem(items);
            if (card == null) break;
            blacklist.Add(card.CanonicalInstance ?? card);
            bool upgraded = false;
            if (!opts.Flags.HasFlag(CardCreationFlags.NoUpgradeRoll))
            {
                decimal num = (decimal)rewards.NextFloat();
                if (card.IsUpgradable)
                {
                    decimal odds = 0m;
                    if (card.Rarity != CardRarity.Rare) odds += (decimal)p.RunState.CurrentActIndex * upgradeScaling;
                    odds = MegaCrit.Sts2.Core.Hooks.Hook.ModifyCardRewardUpgradeOdds(p.RunState, p, card, odds);
                    upgraded = num <= odds;
                }
            }
            results.Add((card, upgraded));
        }
        return results;
    }

    private static float BaseOdds(CardRarityOddsType type, CardRarity rarity)
    {
        return type switch
        {
            CardRarityOddsType.EliteEncounter => rarity switch { CardRarity.Common => CardRarityOdds.EliteCommonOdds, CardRarity.Uncommon => 0.4f, _ => CardRarityOdds.EliteRareOdds },
            CardRarityOddsType.BossEncounter => rarity switch { CardRarity.Rare => 1f, _ => 0f },
            CardRarityOddsType.Shop => rarity switch { CardRarity.Common => CardRarityOdds.ShopCommonOdds, CardRarity.Uncommon => 0.37f, _ => CardRarityOdds.ShopRareOdds },
            CardRarityOddsType.RegularEncounter => rarity switch { CardRarity.Common => CardRarityOdds.regularCommonOdds, CardRarity.Uncommon => 0.37f, _ => CardRarityOdds.RegularRareOdds },
            _ => 0.33f,
        };
    }

    /// <summary>
    /// Mirror of RelicFactory.PullNextRelicFromFront(player) repeated <paramref name="count"/> times, without
    /// touching the grab bag: one Rewards draw for the rarity (&lt;0.5 common, &lt;0.83 uncommon, else rare), then the
    /// first allowed relic of that rarity's pre-shuffled deque (falling through common → uncommon → rare → the
    /// multiplayer fallback deque → Circlet). A deque that is empty and would be refilled cannot be predicted.
    /// </summary>
    public static List<(RelicModel? relic, string note)> PeekRelicsFromFront(Player p, int count)
    {
        var results = new List<(RelicModel?, string)>();
        var bag = p.RelicGrabBag;
        var deques = HarmonyLib.AccessTools.FieldRefAccess<RelicGrabBag, System.Collections.Generic.Dictionary<RelicRarity, List<RelicModel>>>("_deques")(bag);
        var fallback = HarmonyLib.AccessTools.FieldRefAccess<RelicGrabBag, List<RelicModel>>("_mpFallbackDequeue")(bag);
        bool refreshAllowed = HarmonyLib.AccessTools.FieldRefAccess<RelicGrabBag, bool>("_refreshAllowed")(bag);
        var runState = p.RunState;
        // Work on allowed-only copies so nothing in the real bag is touched.
        var copies = new System.Collections.Generic.Dictionary<RelicRarity, List<RelicModel>>();
        foreach (var (rarity, list) in deques)
            copies[rarity] = list.Where(r => r.IsAllowed(runState)).ToList();
        var fallbackCopy = fallback.Where(r => r.IsAllowed(runState)).ToList();
        var rewards = Clone(p.PlayerRng.Rewards);
        for (int i = 0; i < count; i++)
        {
            float f = rewards.NextFloat();
            var rarity = f < 0.5f ? RelicRarity.Common : (f < 0.83f ? RelicRarity.Uncommon : RelicRarity.Rare);
            List<RelicModel>? list = copies.TryGetValue(rarity, out var l) ? l : new List<RelicModel>();
            if (list.Count == 0 && refreshAllowed)
            {
                results.Add((null, "pool refills"));
                break;
            }
            while (list != null && list.Count == 0)
            {
                rarity = rarity switch
                {
                    RelicRarity.Shop => RelicRarity.Common,
                    RelicRarity.Common => RelicRarity.Uncommon,
                    RelicRarity.Uncommon => RelicRarity.Rare,
                    _ => RelicRarity.None,
                };
                list = rarity == RelicRarity.None ? null : (copies.TryGetValue(rarity, out var l2) ? l2 : new List<RelicModel>());
            }
            if (list == null && fallbackCopy.Count > 0) list = fallbackCopy;
            if (list == null || list.Count == 0)
            {
                results.Add((RelicFactory.FallbackRelic, ""));
                continue;
            }
            var relic = list[0];
            list.RemoveAt(0);
            results.Add((relic, ""));
        }
        return results;
    }

    /// <summary>Mirror of CardFactory.CreateRandomCardForTransform without creating the card.</summary>
    public static CardModel? TransformResult(CardModel original, bool inCombat, Rng rng)
    {
        var options = CardFactory.GetDefaultTransformationOptions(original, inCombat);
        return rng.NextItem(options);
    }

    public static int IntVar(CardModel card, string key, int fallback)
    {
        try
        {
            if (card.DynamicVars.ContainsKey(key))
                return card.DynamicVars[key].IntValue;
        }
        catch { }
        return fallback;
    }

    public static int IntVar(PotionModel potion, string key, int fallback)
    {
        try
        {
            if (potion.DynamicVars.ContainsKey(key))
                return potion.DynamicVars[key].IntValue;
        }
        catch { }
        return fallback;
    }
}
