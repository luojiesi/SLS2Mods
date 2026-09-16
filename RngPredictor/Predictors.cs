using System.Collections.Generic;
using System.Linq;
using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace RngPredictor;

/// <summary>A card to display in the overlay: which card, a small label under it, and the upgrade level to show.</summary>
internal sealed record PredCard(CardModel Card, string Label, int UpgradeLevel);

/// <summary>Result of a prediction: cards to show, text lines, and per-creature markers.</summary>
internal sealed class Prediction
{
    public string Title = "";
    public readonly List<PredCard> Cards = new();
    public readonly List<string> Lines = new();
    public readonly List<(Creature creature, string label)> Targets = new();
    /// <summary>Scale used for the card row (small rows for shuffle order).</summary>
    public float CardScale = 0.42f;
    public int CardsPerRow = 6;

    public bool IsEmpty => Cards.Count == 0 && Lines.Count == 0 && Targets.Count == 0;

    public string Signature()
    {
        var sb = new StringBuilder();
        sb.Append(Title).Append('|').Append(CardScale).Append('|').Append(CardsPerRow).Append('|');
        foreach (var c in Cards) sb.Append(c.Card.Id.Entry).Append('+').Append(c.UpgradeLevel).Append(':').Append(c.Label).Append(',');
        sb.Append('|');
        foreach (var l in Lines) sb.Append(l).Append('\n');
        sb.Append('|');
        foreach (var t in Targets) sb.Append(t.creature.GetHashCode()).Append(':').Append(t.label).Append(',');
        return sb.ToString();
    }
}

/// <summary>Localised UI strings (Chinese when the game language is zhs/zht, English otherwise).</summary>
internal static class L
{
    public static bool Zh
    {
        get
        {
            try { return (LocManager.Instance?.Language ?? "").StartsWith("zh"); }
            catch { return false; }
        }
    }

    public static string T(string zh, string en) => Zh ? zh : en;

    public static string Ordinal(int k) => Zh ? $"第{k}张" : (k switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{k}th" });
}

/// <summary>
/// One predictor per random effect. Each predictor rebuilds the card's random logic on a cloned RNG stream
/// (see <see cref="Sim"/>) and returns what the game will actually produce.
/// </summary>
internal static class Predictors
{
    private static string Name(CardModel c) { try { return c.Title; } catch { return c.Id.Entry; } }
    private static string Name(PotionModel p) { try { return p.Title.GetFormattedText(); } catch { return p.Id.Entry; } }
    private static string Name(OrbModel o) { try { return o.Title.GetFormattedText(); } catch { return o.Id.Entry; } }
    private static string Name(Creature c) { try { return c.Name; } catch { return "?"; } }

    private static void AddCards(Prediction pr, IEnumerable<CardModel> cards, string label = "", int upgrade = -1)
    {
        foreach (var c in cards)
            pr.Cards.Add(new PredCard(c, label, upgrade >= 0 ? upgrade : SafeUpgradeLevel(c)));
    }

    private static int SafeUpgradeLevel(CardModel c)
    {
        try { return c.CurrentUpgradeLevel; } catch { return 0; }
    }

    private static void AddTargets(Prediction pr, List<Creature> targets, string what)
    {
        if (targets.Count == 0) return;
        var counts = new Dictionary<Creature, int>();
        var order = new List<Creature>();
        foreach (var t in targets)
        {
            if (!counts.ContainsKey(t)) { counts[t] = 0; order.Add(t); }
            counts[t]++;
        }
        foreach (var t in order)
            pr.Targets.Add((t, $"{what} ×{counts[t]}"));
        pr.Lines.Add(L.T("目标顺序: ", "Target order: ") + string.Join(" → ", targets.Select(Name)));
    }

    /// <summary>
    /// Mirror of CardPileCmd.AutoPlayFromDrawPile(count, Top): the top cards of the draw pile (reshuffling the
    /// discard pile when it runs out), each auto-played with a random target if it needs one.
    /// </summary>
    private static void AddAutoPlayFromDraw(Prediction pr, Player p, int count, string label)
    {
        if (count <= 0) { pr.Lines.Add(L.T("不会打出任何牌", "Plays nothing")); return; }
        var rng = p.RunState.Rng;
        var cards = Sim.SimulateDraw(p, count, Sim.Clone(rng.Shuffle), applyHandLimit: false);
        var cs = p.Creature.CombatState;
        var targets = Sim.Clone(rng.CombatTargets);
        var hits = new List<Creature>();
        int idx = 0;
        foreach (var c in cards)
        {
            idx++;
            string lab = count > 1 ? L.T($"第{idx}张·", $"#{idx} ") + label : label;
            if (c.TargetType == TargetType.AnyEnemy && cs != null)
            {
                var t = targets.NextItem(cs.HittableEnemies);
                if (t != null) { hits.Add(t); lab += " → " + Name(t); }
            }
            pr.Cards.Add(new PredCard(c, lab, SafeUpgradeLevel(c)));
        }
        if (cards.Count == 0) pr.Lines.Add(L.T("抽牌堆和弃牌堆都是空的", "Draw and discard piles are empty"));
        AddTargets(pr, hits, L.T("被打", "Hit"));
    }

    private static int HitCountFromX(CardModel card, Player p, bool stars)
    {
        try
        {
            var pcs = p.PlayerCombatState;
            return stars ? pcs.Stars : pcs.Energy;
        }
        catch { return 0; }
    }

    // ───────────────────────────── hand cards (combat) ─────────────────────────────

    public static Prediction? ForHandCard(CardModel card)
    {
        var p = card.Owner;
        if (p == null || p.Creature?.CombatState == null) return null;
        var rng = p.RunState.Rng;
        // When a card resolves it has already left the hand, so exclude it from "random card in hand" pools.
        var hand = PileType.Hand.GetPile(p).Cards.Where(c => c != card).ToList();
        var draw = PileType.Draw.GetPile(p).Cards.ToList();
        var discard = PileType.Discard.GetPile(p).Cards.ToList();
        bool up = card.IsUpgraded;
        var pr = new Prediction { Title = Name(card) };
        string typeName = card.GetType().Name;

        switch (typeName)
        {
            // ── generated cards (CombatCardGeneration) ──
            case "Discovery":
                AddCards(pr, Sim.DistinctForCombat(p, Sim.CharacterPool(p), 3, Sim.Clone(rng.CombatCardGeneration)));
                pr.Lines.Add(L.T("三选一", "Choose 1 of 3"));
                break;
            case "InfernalBlade":
                AddCards(pr, Sim.DistinctForCombat(p, Sim.CharacterPool(p).Where(c => c.Type == CardType.Attack), 1, Sim.Clone(rng.CombatCardGeneration)));
                break;
            case "Distraction":
                AddCards(pr, Sim.DistinctForCombat(p, Sim.CharacterPool(p).Where(c => c.Type == CardType.Skill), 1, Sim.Clone(rng.CombatCardGeneration)));
                break;
            case "WhiteNoise":
                AddCards(pr, Sim.DistinctForCombat(p, Sim.CharacterPool(p).Where(c => c.Type == CardType.Power), 1, Sim.Clone(rng.CombatCardGeneration)));
                break;
            case "BundleOfJoy":
                AddCards(pr, Sim.DistinctForCombat(p, Sim.ColorlessPool(p), Sim.IntVar(card, "Cards", 1), Sim.Clone(rng.CombatCardGeneration)));
                break;
            case "JackOfAllTrades":
                AddCards(pr, Sim.DistinctForCombat(p, Sim.ColorlessPool(p).Where(c => c.GetType().Name != "JackOfAllTrades"), Sim.IntVar(card, "Cards", 1), Sim.Clone(rng.CombatCardGeneration)));
                break;
            case "Quasar":
                AddCards(pr, Sim.DistinctForCombat(p, Sim.ColorlessPool(p), 3, Sim.Clone(rng.CombatCardGeneration)), "", up ? 1 : 0);
                pr.Lines.Add(L.T("三选一", "Choose 1 of 3"));
                break;
            case "ManifestAuthority":
                AddCards(pr, Sim.DistinctForCombat(p, Sim.ColorlessPool(p), 1, Sim.Clone(rng.CombatCardGeneration)), "", up ? 1 : 0);
                break;
            case "Metamorphosis":
                AddCards(pr, Sim.ForCombat(p, Sim.CharacterPool(p).Where(c => c.Type == CardType.Attack), Sim.IntVar(card, "Cards", 3), Sim.Clone(rng.CombatCardGeneration)));
                pr.Lines.Add(L.T("放入抽牌堆随机位置", "Shuffled into the draw pile"));
                break;
            case "Jackpot":
                AddCards(pr, Sim.ForCombat(p, Sim.CharacterPool(p).Where(c =>
                {
                    try { var e = c.EnergyCost; return e != null && e.Canonical == 0 && !e.CostsX; } catch { return false; }
                }), Sim.IntVar(card, "Cards", 3), Sim.Clone(rng.CombatCardGeneration)), "", up ? 1 : 0);
                break;
            case "Stoke":
                AddCards(pr, Sim.ForCombat(p, Sim.CharacterPool(p), hand.Count, Sim.Clone(rng.CombatCardGeneration)), "", up ? 1 : 0);
                break;
            case "Splash":
            {
                var pools = p.UnlockState.CharacterCardPools.ToList();
                if (pools.Count > 1) pools.Remove(p.Character.CardPool);
                var cards = pools.SelectMany(pool => pool.GetUnlockedCards(p.UnlockState, p.RunState.CardMultiplayerConstraint)).Where(c => c.Type == CardType.Attack);
                AddCards(pr, Sim.DistinctForCombat(p, cards, 3, Sim.Clone(rng.CombatCardGeneration)), "", up ? 1 : 0);
                pr.Lines.Add(L.T("三选一", "Choose 1 of 3"));
                break;
            }

            // ── orbs / potions ──
            case "Chaos":
            {
                var orbRng = Sim.Clone(rng.CombatOrbGeneration);
                int n = Sim.IntVar(card, "Repeat", 1);
                var names = new List<string>();
                for (int i = 0; i < n; i++) names.Add(Name(OrbModel.GetRandomOrb(orbRng)));
                pr.Lines.Add(L.T("充能: ", "Channel: ") + string.Join(", ", names));
                break;
            }
            case "Alchemize":
            {
                var pots = Sim.RandomPotions(p, 1, Sim.Clone(rng.CombatPotionGeneration), inCombatPool: true);
                if (pots.Count > 0) pr.Lines.Add(L.T("获得药水: ", "Potion: ") + Name(pots[0]));
                break;
            }

            // ── random card selection (CombatCardSelection) ──
            case "TrueGrit":
            {
                if (up) return null;
                var c = Sim.Clone(rng.CombatCardSelection).NextItem(hand);
                if (c != null) { AddCards(pr, new[] { c }, L.T("消耗", "Exhaust")); }
                break;
            }
            case "Cinder":
            {
                var c = Sim.Clone(rng.CombatCardSelection).NextItem(hand);
                if (c != null) { AddCards(pr, new[] { c }, L.T("消耗", "Exhaust")); }
                break;
            }
            case "Thrash":
            {
                var c = Sim.Clone(rng.CombatCardSelection).NextItem(hand.Where(x => x.Type == CardType.Attack));
                if (c != null) { AddCards(pr, new[] { c }, L.T("加伤来源", "Bonus from")); }
                break;
            }
            case "Anointed":
            {
                int count = CardPile.MaxCardsInHand - hand.Count;
                var r = Sim.Clone(rng.CombatCardSelection);
                var picked = draw.Where(c => c.Rarity == CardRarity.Rare).ToList();
                picked = MegaCrit.Sts2.Core.Extensions.ListExtensions.UnstableShuffle(picked, r).Take(count).ToList();
                AddCards(pr, picked, L.T("入手", "To hand"));
                break;
            }
            case "DrainPower":
            {
                var r = Sim.Clone(rng.CombatCardSelection);
                var picked = discard.Where(c => c.IsUpgradable).ToList();
                picked = MegaCrit.Sts2.Core.Extensions.ListExtensions.UnstableShuffle(picked, r).Take(Sim.IntVar(card, "Cards", 1)).ToList();
                AddCards(pr, picked, L.T("升级", "Upgrade"));
                break;
            }
            case "SeekerStrike":
            {
                var r = Sim.Clone(rng.CombatCardSelection);
                var picked = Sim.ShuffleOrder(draw, r).Take(Sim.IntVar(card, "Cards", 3)).ToList();
                AddCards(pr, picked, L.T("可选", "Option"));
                break;
            }
            case "HiddenGem":
            {
                if (draw.Count == 0) break;
                var list2 = draw.Where(c =>
                {
                    bool playable = !c.Keywords.Contains(CardKeyword.Unplayable);
                    bool notStatusCurse = c.Type != CardType.Status && c.Type != CardType.Curse;
                    int replay = 0;
                    try { replay = c.GetEnchantedReplayCount(); } catch { }
                    return playable && notStatusCurse && replay < 1;
                }).ToList();
                var list3 = list2.Where(c => c.Type == CardType.Attack || c.Type == CardType.Skill || c.Type == CardType.Power).ToList();
                var items = list3.Count == 0 ? list2 : list3;
                var c2 = Sim.Clone(rng.CombatCardSelection).NextItem(items);
                if (c2 != null) AddCards(pr, new[] { c2 }, L.T("重现", "Replay"));
                break;
            }

            // ── random plays from piles (Shuffle stream) + random targets ──
            case "BeatDown":
            {
                var shuffle = Sim.Clone(rng.Shuffle);
                var targets = Sim.Clone(rng.CombatTargets);
                var picked = Sim.ShuffleOrder(discard.Where(c => c.Type == CardType.Attack && !c.Keywords.Contains(CardKeyword.Unplayable)), shuffle)
                    .Take(Sim.IntVar(card, "Cards", 3)).ToList();
                var cs = p.Creature.CombatState;
                var hits = new List<Creature>();
                int idx = 0;
                foreach (var c in picked)
                {
                    idx++;
                    string label = L.T($"第{idx}张", $"#{idx}");
                    if (c.TargetType == TargetType.AnyEnemy && cs != null)
                    {
                        var t = targets.NextItem(cs.HittableEnemies);
                        if (t != null) { hits.Add(t); label += " → " + Name(t); }
                    }
                    pr.Cards.Add(new PredCard(c, label, SafeUpgradeLevel(c)));
                }
                AddTargets(pr, hits, L.T("被打", "Hit"));
                break;
            }
            case "Catastrophe":
            {
                var shuffle = Sim.Clone(rng.Shuffle);
                var targets = Sim.Clone(rng.CombatTargets);
                var pool = draw.ToList();
                var cs = p.Creature.CombatState;
                var hits = new List<Creature>();
                int n = Sim.IntVar(card, "Cards", 1);
                for (int i = 0; i < n; i++)
                {
                    var c = Sim.ShuffleOrder(pool.Where(x => !x.Keywords.Contains(CardKeyword.Unplayable)), shuffle).FirstOrDefault();
                    if (c == null) c = Sim.ShuffleOrder(pool, shuffle).FirstOrDefault();
                    if (c == null) break;
                    pool.Remove(c);
                    string label = L.T($"第{i + 1}张", $"#{i + 1}");
                    if (c.TargetType == TargetType.AnyEnemy && cs != null)
                    {
                        var t = targets.NextItem(cs.HittableEnemies);
                        if (t != null) { hits.Add(t); label += " → " + Name(t); }
                    }
                    pr.Cards.Add(new PredCard(c, label, SafeUpgradeLevel(c)));
                }
                AddTargets(pr, hits, L.T("被打", "Hit"));
                break;
            }
            case "Uproar":
            {
                var shuffle = Sim.Clone(rng.Shuffle);
                var c = Sim.ShuffleOrder(draw.Where(x => x.Type == CardType.Attack && !x.Keywords.Contains(CardKeyword.Unplayable)), shuffle).FirstOrDefault();
                if (c == null) c = Sim.ShuffleOrder(draw.Where(x => x.Type == CardType.Attack), shuffle).FirstOrDefault();
                if (c != null)
                {
                    string label = L.T("打出", "Play");
                    var cs = p.Creature.CombatState;
                    if (c.TargetType == TargetType.AnyEnemy && cs != null)
                    {
                        var t = Sim.Clone(rng.CombatTargets).NextItem(cs.HittableEnemies);
                        if (t != null) { label += " → " + Name(t); pr.Targets.Add((t, L.T("被打", "Hit") + " ×1")); }
                    }
                    pr.Cards.Add(new PredCard(c, label, SafeUpgradeLevel(c)));
                }
                break;
            }

            // ── play the top card(s) of the draw pile (CardPileCmd.AutoPlayFromDrawPile) ──
            case "Havoc":
                AddAutoPlayFromDraw(pr, p, 1, L.T("打出并消耗", "Play, exhaust"));
                break;
            case "Cascade":
                AddAutoPlayFromDraw(pr, p, HitCountFromX(card, p, stars: false) + (up ? 1 : 0), L.T("打出", "Play"));
                break;

            // ── random targets (CombatTargets) ──
            case "SwordBoomerang":
            case "Ricochet":
                AddTargets(pr, Sim.RandomAttackTargets(p, Sim.IntVar(card, "Repeat", 1), Sim.Clone(rng.CombatTargets)), L.T("被打", "Hit"));
                break;
            case "RipAndTear":
                AddTargets(pr, Sim.RandomAttackTargets(p, 2, Sim.Clone(rng.CombatTargets)), L.T("被打", "Hit"));
                break;
            case "SweepingGaze":
                AddTargets(pr, Sim.RandomAttackTargets(p, 1, Sim.Clone(rng.CombatTargets)), L.T("被打", "Hit"));
                break;
            case "Stardust":
                AddTargets(pr, Sim.RandomAttackTargets(p, HitCountFromX(card, p, stars: true), Sim.Clone(rng.CombatTargets)), L.T("被打", "Hit"));
                break;
            case "Volley":
                AddTargets(pr, Sim.RandomAttackTargets(p, HitCountFromX(card, p, stars: false), Sim.Clone(rng.CombatTargets)), L.T("被打", "Hit"));
                break;
            case "FlakCannon":
            {
                int statuses = 0;
                try { statuses = p.PlayerCombatState.AllCards.Count(c => c.Type == CardType.Status && c.Pile != null && c.Pile.Type != PileType.Exhaust); } catch { }
                AddTargets(pr, Sim.RandomAttackTargets(p, statuses, Sim.Clone(rng.CombatTargets)), L.T("被打", "Hit"));
                break;
            }
            case "BouncingFlask":
                AddTargets(pr, Sim.RandomHittableTargets(p, Sim.IntVar(card, "Repeat", 3), Sim.Clone(rng.CombatTargets)), L.T("中毒", "Poison"));
                break;

            default:
                return null;
        }
        return pr.IsEmpty ? null : pr;
    }

    // ───────────────────────────── potions ─────────────────────────────

    public static Prediction? ForPotion(PotionModel potion)
    {
        var p = potion.Owner;
        if (p == null) return null;
        var rng = p.RunState.Rng;
        bool inCombat = CombatManager.Instance.IsInProgress && p.Creature?.CombatState != null;
        var pr = new Prediction { Title = Name(potion) };
        string typeName = potion.GetType().Name;

        switch (typeName)
        {
            case "AttackPotion":
                if (!inCombat) return null;
                AddCards(pr, Sim.DistinctForCombat(p, Sim.CharacterPool(p).Where(c => c.Type == CardType.Attack), 3, Sim.Clone(rng.CombatCardGeneration)));
                pr.Lines.Add(L.T("三选一", "Choose 1 of 3"));
                break;
            case "SkillPotion":
                if (!inCombat) return null;
                AddCards(pr, Sim.DistinctForCombat(p, Sim.CharacterPool(p).Where(c => c.Type == CardType.Skill), 3, Sim.Clone(rng.CombatCardGeneration)));
                pr.Lines.Add(L.T("三选一", "Choose 1 of 3"));
                break;
            case "PowerPotion":
                if (!inCombat) return null;
                AddCards(pr, Sim.DistinctForCombat(p, Sim.CharacterPool(p).Where(c => c.Type == CardType.Power), 3, Sim.Clone(rng.CombatCardGeneration)));
                pr.Lines.Add(L.T("三选一", "Choose 1 of 3"));
                break;
            case "ColorlessPotion":
                if (!inCombat) return null;
                AddCards(pr, Sim.DistinctForCombat(p, Sim.ColorlessPool(p), 3, Sim.Clone(rng.CombatCardGeneration)));
                pr.Lines.Add(L.T("三选一", "Choose 1 of 3"));
                break;
            case "CosmicConcoction":
                if (!inCombat) return null;
                AddCards(pr, Sim.DistinctForCombat(p, Sim.ColorlessPool(p), Sim.IntVar(potion, "Cards", 3), Sim.Clone(rng.CombatCardGeneration)), "", 1);
                break;
            case "OrobicAcid":
            {
                if (!inCombat) return null;
                var r = Sim.Clone(rng.CombatCardGeneration);
                AddCards(pr, Sim.DistinctForCombat(p, Sim.CharacterPool(p).Where(c => c.Type == CardType.Attack), 1, r));
                AddCards(pr, Sim.DistinctForCombat(p, Sim.CharacterPool(p).Where(c => c.Type == CardType.Skill), 1, r));
                AddCards(pr, Sim.DistinctForCombat(p, Sim.CharacterPool(p).Where(c => c.Type == CardType.Power), 1, r));
                break;
            }
            case "EntropicBrew":
            {
                int open = 0;
                try { open = p.PotionSlots.Count(s => s == null); } catch { }
                if (open == 0) { pr.Lines.Add(L.T("没有空的药水栏", "No open potion slots")); break; }
                var pots = Sim.RandomPotions(p, open, Sim.Clone(rng.CombatPotionGeneration), inCombatPool: false);
                pr.Lines.Add(L.T("获得药水: ", "Potions: ") + string.Join(", ", pots.Select(Name)));
                break;
            }
            case "DistilledChaos":
                if (!inCombat) return null;
                AddAutoPlayFromDraw(pr, p, Sim.IntVar(potion, "Repeat", 3), L.T("打出", "Play"));
                break;
            case "SneckoOil":
            {
                if (!inCombat) return null;
                var drawn = Sim.SimulateDraw(p, Sim.IntVar(potion, "Cards", 7), Sim.Clone(rng.Shuffle));
                var handAfter = PileType.Hand.GetPile(p).Cards.ToList();
                handAfter.AddRange(drawn);
                var costRng = Sim.Clone(rng.CombatEnergyCosts);
                var parts = new List<string>();
                foreach (var c in handAfter)
                {
                    bool eligible;
                    try { eligible = !c.EnergyCost.CostsX && c.EnergyCost.GetWithModifiers(CostModifiers.None) >= 0; }
                    catch { eligible = false; }
                    if (!eligible) continue;
                    int cost = costRng.NextInt(4);
                    parts.Add($"{Name(c)}→{cost}");
                }
                AddCards(pr, drawn, L.T("抽到", "Drawn"));
                pr.Lines.Add(L.T("费用: ", "Costs: ") + string.Join("  ", parts));
                break;
            }
            default:
                return null;
        }
        return pr.IsEmpty ? null : pr;
    }

    // ───────────────────────────── transformations ─────────────────────────────

    /// <summary>
    /// What <paramref name="card"/> becomes if it is the 1st, 2nd, ... <paramref name="maxSelect"/>-th card
    /// transformed with <paramref name="rng"/> (each transformation consumes exactly one draw).
    /// </summary>
    public static Prediction? ForTransform(CardModel card, Rng rng, int maxSelect, string sourceLabel, bool upgraded = false)
    {
        if (card.Owner == null) return null;
        try { if (!card.IsTransformable) return null; } catch { return null; }
        var pr = new Prediction { Title = L.T("变化: ", "Transform: ") + Name(card) + "  (" + sourceLabel + ")" };
        maxSelect = System.Math.Max(1, System.Math.Min(maxSelect, 6));
        for (int k = 1; k <= maxSelect; k++)
        {
            var r = Sim.Clone(rng);
            for (int skip = 0; skip < k - 1; skip++) r.NextInt();
            CardModel? result;
            try { result = Sim.TransformResult(card, inCombat: false, r); }
            catch (System.Exception ex) { PLog.Write($"Transform prediction failed: {ex.Message}"); return null; }
            if (result == null) break;
            pr.Cards.Add(new PredCard(result, maxSelect > 1 ? L.Ordinal(k) : "", upgraded ? 1 : 0));
        }
        if (maxSelect > 1)
            pr.Lines.Add(L.T("按选中顺序，第k张变化的牌会得到第k个结果", "The k-th card you transform gets the k-th result"));
        return pr.IsEmpty ? null : pr;
    }

    // ───────────────────────────── Neow / event relic options ─────────────────────────────

    private static readonly HashSet<string> NeowTransformRelics = new()
    {
        "NewLeaf", "Astrolabe", "PandorasBox", "LeafyPoultice",
        "ArcaneScroll", "HeftyTablet", "LeadPaperweight", "LavaRock", "PhialHolster", "CursedPearl",
    };

    private static void AddRewardCards(Prediction pr, Player p, int count, CardCreationOptions options, string label)
    {
        var cards = Sim.CreateForReward(p, count, options, Sim.Clone(p.PlayerRng.Rewards));
        foreach (var (card, upgraded) in cards)
            pr.Cards.Add(new PredCard(card, label, upgraded ? 1 : 0));
        if (cards.Count == 0) pr.Lines.Add(L.T("没有可生成的牌", "No card can be generated"));
    }

    public static bool IsPredictableNeowRelic(RelicModel relic) => NeowTransformRelics.Contains(relic.GetType().Name);

    /// <summary>
    /// Hovering an event option that grants a deck-transforming relic (New Leaf, Astrolabe, Pandora's Box,
    /// Leafy Poultice): what every eligible deck card would turn into, before the option is chosen.
    /// </summary>
    public static Prediction? ForNeowRelic(RelicModel relic, Player p)
    {
        string type = relic.GetType().Name;
        var deck = PileType.Deck.GetPile(p).Cards.ToList();
        var pr = new Prediction { CardScale = 0.3f, CardsPerRow = 8 };
        string relicName;
        try { relicName = relic.Title.GetFormattedText(); } catch { relicName = relic.Id.Entry; }

        switch (type)
        {
            case "NewLeaf":
            case "Astrolabe":
            {
                bool up = type == "Astrolabe";
                pr.Title = relicName + L.T("：选哪张牌 → 会变成什么（不用选也能看）", ": pick which card → becomes what (no need to choose to see this)");
                AddDeckTransformGroups(pr, deck, p.RunState.Rng.Niche, up);
                if (up)
                    pr.Lines.Add(L.T("星盘变化3张：以上是每张牌作为第1张被选中时的结果；第2、3张在选牌界面悬停查看", "Astrolabe transforms 3: results are for the 1st pick; hover on the selection screen for the 2nd/3rd"));
                break;
            }
            case "PandorasBox":
            {
                pr.Title = relicName + L.T("：打击/防御会变成", ": Strikes/Defends become");
                var r = Sim.Clone(p.RunState.Rng.Niche);
                foreach (var c in deck)
                {
                    bool ok;
                    try { ok = c.IsBasicStrikeOrDefend && c.IsRemovable; } catch { ok = false; }
                    if (!ok) continue;
                    var res = Sim.TransformResult(c, false, r);
                    if (res != null) pr.Cards.Add(new PredCard(res, Name(c) + " →", 0));
                }
                break;
            }
            case "ArcaneScroll":
            {
                pr.Title = relicName + L.T("：会获得的稀有牌", ": the rare card you get");
                pr.CardScale = 0.45f;
                var options = new CardCreationOptions(new[] { p.Character.CardPool }, CardCreationSource.Other, CardRarityOddsType.Uniform, c => c.Rarity == CardRarity.Rare).WithFlags(CardCreationFlags.NoUpgradeRoll);
                int n = 1;
                try { n = relic.DynamicVars.ContainsKey("Cards") ? relic.DynamicVars["Cards"].IntValue : 1; } catch { }
                AddRewardCards(pr, p, n, options, "");
                break;
            }
            case "HeftyTablet":
            {
                pr.Title = relicName + L.T("：可选的稀有牌", ": the rare cards offered");
                pr.CardScale = 0.45f;
                var options = new CardCreationOptions(new[] { p.Character.CardPool }, CardCreationSource.Other, CardRarityOddsType.Uniform, c => c.Rarity == CardRarity.Rare).WithFlags(CardCreationFlags.NoUpgradeRoll);
                int n = 3;
                try { n = relic.DynamicVars.ContainsKey("Cards") ? relic.DynamicVars["Cards"].IntValue : 3; } catch { }
                AddRewardCards(pr, p, n, options, "");
                pr.Lines.Add(L.T($"{n}选1", $"Choose 1 of {n}"));
                break;
            }
            case "LeadPaperweight":
            case "LavaRock":
            {
                pr.Title = relicName + L.T("：可选的无色牌", ": the colorless cards offered");
                pr.CardScale = 0.45f;
                var options = new CardCreationOptions(new[] { ModelDb.CardPool<ColorlessCardPool>() }, CardCreationSource.Other, CardRarityOddsType.RegularEncounter);
                AddRewardCards(pr, p, 2, options, "");
                pr.Lines.Add(L.T("2选1", "Choose 1 of 2"));
                break;
            }
            case "PhialHolster":
            {
                int n = 2;
                try { n = relic.DynamicVars.ContainsKey("Potions") ? relic.DynamicVars["Potions"].IntValue : 2; } catch { }
                var pots = Sim.RandomPotions(p, n, Sim.Clone(p.RunState.Rng.CombatPotionGeneration), inCombatPool: false);
                pr.Title = relicName + L.T("：会获得的药水", ": potions you get");
                pr.Lines.Add(string.Join(", ", pots.Select(Name)));
                break;
            }
            case "CursedPearl":
            {
                var pots = Sim.RandomPotions(p, 1, Sim.Clone(p.RunState.Rng.CombatPotionGeneration), inCombatPool: false);
                pr.Title = relicName + L.T("：会获得的药水", ": potion you get");
                pr.Lines.Add(string.Join(", ", pots.Select(Name)));
                break;
            }
            case "LeafyPoultice":
            {
                pr.Title = relicName + L.T("：一张打击和一张防御会变成", ": one Strike and one Defend become");
                var basics = deck.Where(c => c.Rarity == CardRarity.Basic).ToList();
                var strike = basics.FirstOrDefault(c => c.Tags.Contains(CardTag.Strike));
                var defend = basics.FirstOrDefault(c => c.Tags.Contains(CardTag.Defend));
                var r = Sim.Clone(p.PlayerRng.Transformations);
                foreach (var c in new[] { strike, defend })
                {
                    if (c == null) continue;
                    var res = Sim.TransformResult(c, false, r);
                    if (res != null) pr.Cards.Add(new PredCard(res, Name(c) + " →", 0));
                }
                break;
            }
            default:
                return null;
        }
        return pr.IsEmpty ? null : pr;
    }

    /// <summary>
    /// "Pick which card → becomes what" for a whole deck. Cards with the same option pool (e.g. all basic cards)
    /// roll the same result from the same draw, so the results are grouped.
    /// </summary>
    private static void AddDeckTransformGroups(Prediction pr, List<CardModel> deck, Rng rng, bool upgraded)
    {
        pr.CardScale = 0.45f;
        pr.CardsPerRow = 6;
        var groups = new List<(CardModel result, List<string> originals)>();
        foreach (var c in deck)
        {
            bool ok;
            try { ok = c.Type != CardType.Quest && c.IsTransformable; } catch { ok = false; }
            if (!ok) continue;
            CardModel? r;
            try { r = Sim.TransformResult(c, false, Sim.Clone(rng)); } catch { r = null; }
            if (r == null) continue;
            int gi = groups.FindIndex(g => g.result.Id == r.Id);
            if (gi < 0) { groups.Add((r, new List<string>())); gi = groups.Count - 1; }
            groups[gi].originals.Add(Name(c));
        }
        foreach (var (result, originals) in groups)
        {
            var counted = originals.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key}×{g.Count()}" : g.Key);
            pr.Cards.Add(new PredCard(result, Name(result), upgraded ? 1 : 0));
            pr.Lines.Add(string.Join(", ", counted) + "  →  " + Name(result));
        }
    }

    // ───────────────────────────── event options with random effects ─────────────────────────────

    private static string OptionSuffix(string textKey)
    {
        int i = textKey.LastIndexOf('.');
        return i >= 0 ? textKey.Substring(i + 1) : textKey;
    }

    /// <summary>Event options whose outcome is random and predictable from the event's own RNG (or the player's).</summary>
    public static bool HasEventOptionPredictor(EventModel ev, string textKey)
    {
        string suffix = OptionSuffix(textKey);
        return ev.GetType().Name switch
        {
            "EndlessConveyor" => suffix is "OBSERVE_CHEF" or "SPICY_SNAPPY" or "JELLY_LIVER" or "SUSPICIOUS_CONDIMENT",
            "AromaOfChaos" => suffix == "LET_GO",
            "WhisperingHollow" => suffix == "HUG",
            "Symbiote" => suffix == "KILL_WITH_FIRE",
            "MorphicGrove" => suffix == "GROUP",
            "Trial" => suffix == "NONDESCRIPT_INNOCENT",
            _ => false,
        };
    }

    public static Prediction? ForEventOption(EventModel ev, string textKey, Player p)
    {
        string suffix = OptionSuffix(textKey);
        string evName = ev.GetType().Name;
        var deck = PileType.Deck.GetPile(p).Cards.ToList();
        var pr = new Prediction();
        Rng evRng;
        try { evRng = ev.Rng; } catch { return null; }
        if (evRng == null) return null;

        // Random upgrade of a deck card (Endless Conveyor: Observe Chef / Spicy Snappy).
        if (evName == "EndlessConveyor" && suffix is "OBSERVE_CHEF" or "SPICY_SNAPPY")
        {
            pr.Title = L.T("随机升级一张牌 → 会升级的是", "Upgrade a random card → it will be");
            var upgradable = deck.Where(c => { try { return c.IsUpgradable; } catch { return false; } }).ToList();
            if (upgradable.Count == 0)
            {
                pr.Lines.Add(L.T("牌组里没有可升级的牌，这个选项不会升级任何东西", "No upgradable card in the deck: this option upgrades nothing"));
                return pr;
            }
            var card = Sim.Clone(evRng).NextItem(upgradable);
            if (card == null) return null;
            pr.CardScale = 0.45f;
            pr.Cards.Add(new PredCard(card, Name(card) + " → " + Name(card) + "+", SafeUpgradeLevel(card) + 1));
            pr.Lines.Add(L.T($"升级：{Name(card)}（共{upgradable.Count}张可升级）", $"Upgrades {Name(card)} ({upgradable.Count} upgradable cards)"));
            return pr;
        }

        // Random potion from the character + shared pools (Suspicious Condiment; uses the Rewards stream).
        if (evName == "EndlessConveyor" && suffix == "SUSPICIOUS_CONDIMENT")
        {
            pr.Title = L.T("可疑调味品 → 得到的药水", "Suspicious Condiment → potion");
            try
            {
                IEnumerable<PotionModel> items = p.Character.PotionPool.GetUnlockedPotions(p.UnlockState)
                    .Concat(ModelDb.PotionPool<SharedPotionPool>().GetUnlockedPotions(p.UnlockState));
                var potion = Sim.Clone(p.PlayerRng.Rewards).NextItem(items);
                if (potion == null) return null;
                pr.Lines.Add(L.T("药水: ", "Potion: ") + Name(potion));
            }
            catch (System.Exception ex) { PLog.Write($"Condiment prediction failed: {ex.Message}"); return null; }
            return pr;
        }

        // Transform options: one draw per transformed card from the event's RNG.
        int count = 1;
        if (evName == "MorphicGrove" || evName == "Trial") count = 2;
        else if (evName == "Symbiote")
        {
            try { count = ev.DynamicVars.ContainsKey("Cards") ? ev.DynamicVars["Cards"].IntValue : 1; } catch { count = 1; }
        }
        pr.Title = L.T("变化：选哪张牌 → 会变成什么（不用选也能看）", "Transform: pick which card → becomes what (no need to choose to see this)");
        AddDeckTransformGroups(pr, deck, evRng, upgraded: false);
        if (count > 1)
            pr.Lines.Add(L.T($"要变{count}张：以上是第1张选中的牌的结果；其余的在选牌界面悬停查看", $"{count} cards transform: results are for the 1st pick; hover on the selection screen for the rest"));
        return pr.IsEmpty ? null : pr;
    }

    // ───────────────────────────── shuffle order ─────────────────────────────

    /// <summary>Order of the draw pile after the next reshuffle (top first).</summary>
    public static Prediction? ForShuffle(Player p)
    {
        if (p.Creature?.CombatState == null) return null;
        var rng = p.RunState.Rng;
        var draw = PileType.Draw.GetPile(p).Cards.ToList();
        var discard = PileType.Discard.GetPile(p).Cards.ToList();
        var hand = PileType.Hand.GetPile(p).Cards.ToList();
        var pr = new Prediction
        {
            Title = L.T("下次洗牌后的抽牌堆顺序（从上到下）", "Draw pile order after the next reshuffle (top first)"),
            CardScale = 0.3f,
            CardsPerRow = 10,
        };
        // (a) reshuffle right now: discard + whatever is still in the draw pile (that is what CardPileCmd.Shuffle does)
        var now = Sim.ShuffleOrder(discard.Concat(draw), Sim.Clone(rng.Shuffle));
        // (b) reshuffle after this turn ends: the hand gets discarded first
        var afterTurn = Sim.ShuffleOrder(discard.Concat(draw).Concat(hand), Sim.Clone(rng.Shuffle));
        if (now.Count == 0 && afterTurn.Count == 0) return null;
        int i = 0;
        foreach (var c in now)
            pr.Cards.Add(new PredCard(c, (++i).ToString(), SafeUpgradeLevel(c)));
        pr.Lines.Add(L.T($"上排: 现在就洗牌（弃牌堆+抽牌堆, {now.Count}张）", $"Row above: reshuffle now (discard + draw pile, {now.Count} cards)"));
        pr.Lines.Add(L.T($"回合结束弃掉手牌后再洗牌（{afterTurn.Count}张）: ", $"Reshuffle after the hand is discarded at end of turn ({afterTurn.Count} cards): ")
                     + string.Join(", ", afterTurn.Take(12).Select(Name)) + (afterTurn.Count > 12 ? " …" : ""));
        pr.Lines.Add(L.T("不包含改变洗牌顺序的遗物/能力效果", "Relic/power hooks that reorder the shuffle are not included"));
        return pr;
    }
}
