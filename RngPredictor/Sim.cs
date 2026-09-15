using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
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
