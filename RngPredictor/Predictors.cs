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
using MegaCrit.Sts2.Core.Models.Orbs;
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
    private static string Name(RelicModel r) { try { return r.Title.GetFormattedText(); } catch { return r.Id.Entry; } }

    private static void AddCards(Prediction pr, IEnumerable<CardModel> cards, string label = "", int upgrade = -1)
    {
        foreach (var c in cards)
            pr.Cards.Add(new PredCard(c, label, upgrade >= 0 ? upgrade : SafeUpgradeLevel(c)));
    }

    private static bool SafeIsUpgradable(CardModel c) { try { return c.IsUpgradable; } catch { return false; } }

    private static int SafeUpgradeLevel(CardModel c)
    {
        try { return c.CurrentUpgradeLevel; } catch { return 0; }
    }

    /// <summary>Markers "Hit ×N (damage absorbed)" plus the hit sequence, with kills flagged (see <see cref="DamageSim"/>).</summary>
    private static void AddAttackHits(Prediction pr, List<DamageSim.Hit> hits, string what)
    {
        if (hits.Count == 0) return;
        var order = new List<Creature>();
        var count = new Dictionary<Creature, int>();
        var total = new Dictionary<Creature, decimal>();
        var killed = new HashSet<Creature>();
        foreach (var h in hits)
        {
            if (!count.ContainsKey(h.Target)) { count[h.Target] = 0; total[h.Target] = 0m; order.Add(h.Target); }
            count[h.Target]++;
            total[h.Target] += h.Dealt;
            if (h.Kill) killed.Add(h.Target);
        }
        foreach (var c in order)
            pr.Targets.Add((c, $"{what} ×{count[c]} ({total[c]})" + (killed.Contains(c) ? L.T(" 击杀", " kill") : "")));
        pr.Lines.Add(L.T("目标顺序: ", "Target order: ") + string.Join(" → ", hits.Select(h => Name(h.Target) + " " + h.Damage + (h.Kill ? L.T("（击杀）", " (kill)") : ""))));
    }

    private static decimal DamageBase(CardModel card) { try { return card.DynamicVars.Damage.BaseValue; } catch { return 0m; } }
    private static MegaCrit.Sts2.Core.ValueProps.ValueProp DamageProps(CardModel card) { try { return card.DynamicVars.Damage.Props; } catch { return MegaCrit.Sts2.Core.ValueProps.ValueProp.Move; } }

    private static List<DamageSim.Hit> RandomAttack(CardModel card, Player p, int hits, Rng targets) =>
        new DamageSim(p).RandomAttack(card, DamageBase(card), DamageProps(card), hits, targets);

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

    // ───────────────────────────── orbs ─────────────────────────────

    /// <summary>Replays the orb operations of a Defect card on the simulator. Returns false for unknown cards.</summary>
    private static bool RunOrbCard(CardModel card, Player p, OrbSim sim)
    {
        bool up = card.IsUpgraded;
        int Repeat(int d) => Sim.IntVar(card, "Repeat", d);
        int Cards(int d) => Sim.IntVar(card, "Cards", d);
        switch (card.GetType().Name)
        {
            case "Zap": sim.Channel(sim.NewOrb<LightningOrb>()); return true;
            case "BallLightning": sim.Channel(sim.NewOrb<LightningOrb>()); return true;
            case "Tempest":
            {
                int n = HitCountFromX(card, p, stars: false) + (up ? 1 : 0);
                for (int i = 0; i < n; i++) sim.Channel(sim.NewOrb<LightningOrb>());
                return true;
            }
            case "Voltaic":
            {
                int n = 0;
                try
                {
                    var v = card.DynamicVars["CalculatedChannels"];
                    n = (int)(decimal)HarmonyLib.Traverse.Create(v).Method("Calculate", new System.Type[] { typeof(Creature) }).GetValue(new object?[] { null });
                }
                catch { }
                for (int i = 0; i < n; i++) sim.Channel(sim.NewOrb<LightningOrb>());
                return true;
            }
            case "Rainbow":
                sim.Channel(sim.NewOrb<LightningOrb>()); sim.Channel(sim.NewOrb<FrostOrb>()); sim.Channel(sim.NewOrb<DarkOrb>());
                return true;
            case "Dualcast":
                if (sim.Queue.Count > 0) { sim.EvokeNext(false); sim.EvokeNext(true); }
                return true;
            case "MultiCast":
            {
                int n = HitCountFromX(card, p, stars: false);
                for (int i = 0; i < n; i++) sim.EvokeNext(i == n - 1);
                return true;
            }
            case "Quadcast":
            {
                if (sim.Queue.Count <= 0) return true;
                int n = Repeat(4);
                for (int i = 0; i < n; i++) sim.EvokeNext(i == n - 1);
                return true;
            }
            case "Shatter":
            {
                int n = sim.Queue.Count;
                for (int i = 0; i < n; i++) { sim.EvokeNext(false); sim.EvokeNext(true); }
                return true;
            }
            case "Darkness": case "Null": case "ShadowShield": sim.Channel(sim.NewOrb<DarkOrb>()); return true;
            case "ConsumingShadow": { int n = Repeat(2); for (int i = 0; i < n; i++) sim.Channel(sim.NewOrb<DarkOrb>()); return true; }
            case "Coolheaded": case "ColdSnap": case "Chill": sim.Channel(sim.NewOrb<FrostOrb>()); return true;
            case "Glacier": for (int i = 0; i < 2; i++) sim.Channel(sim.NewOrb<FrostOrb>()); return true;
            case "IceLance": { int n = Repeat(3); for (int i = 0; i < n; i++) sim.Channel(sim.NewOrb<FrostOrb>()); return true; }
            case "Refract": { int n = Repeat(2); for (int i = 0; i < n; i++) sim.Channel(sim.NewOrb<GlassOrb>()); return true; }
            case "Glasswork": case "Spinner": sim.Channel(sim.NewOrb<GlassOrb>()); return true;
            case "Fusion": case "Ignition": sim.Channel(sim.NewOrb<PlasmaOrb>()); return true;
            case "MeteorStrike": for (int i = 0; i < 3; i++) sim.Channel(sim.NewOrb<PlasmaOrb>()); return true;
            default: return false;
        }
    }

    /// <summary>After the card's own orb operations, add what the resulting orbs will do at end of turn.</summary>
    private static void FinishOrbSim(Prediction pr, OrbSim sim)
    {
        int before = sim.Lines.Count;
        sim.EndTurnPassives();
        if (sim.LightningEvents == 0) return; // nothing random happened: no prediction needed
        var cardLines = sim.Lines.Take(before).Where(l => l.Contains("→")).ToList();
        var turnLines = sim.Lines.Skip(before).ToList();
        if (cardLines.Count > 0) pr.Lines.Add(L.T("打出后: ", "On play: ") + string.Join("; ", cardLines));
        if (turnLines.Count > 0) pr.Lines.Add(L.T("然后回合结束: ", "Then at end of turn: ") + string.Join("; ", turnLines.Select(l => l.Replace(L.T("回合结束 ", "End of turn "), ""))));
        sim.AddTargetsTo(pr);
    }

    /// <summary>Hovering the End Turn button: what the orbs' passives will do.</summary>
    public static Prediction? ForEndTurn(Player p)
    {
        if (p.Creature?.CombatState == null) return null;
        var sim = new OrbSim(p, Sim.Clone(p.RunState.Rng.CombatTargets));
        sim.EndTurnPassives();
        if (sim.LightningEvents == 0) return null;
        var pr = new Prediction { Title = L.T("结束回合 → 充能球被动", "End turn → orb passives") };
        pr.Lines.AddRange(sim.Lines);
        sim.AddTargetsTo(pr);
        return pr;
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
                var sim = new OrbSim(p, Sim.Clone(rng.CombatTargets));
                var names = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    var canonical = OrbModel.GetRandomOrb(orbRng);
                    names.Add(Name(canonical));
                    sim.Channel(sim.NewOrb(canonical));
                }
                pr.Lines.Add(L.T("充能: ", "Channel: ") + string.Join(", ", names));
                FinishOrbSim(pr, sim);
                break;
            }
            case "Zap": case "BallLightning": case "Tempest": case "Voltaic": case "Rainbow":
            case "Dualcast": case "MultiCast": case "Quadcast": case "Shatter":
            case "Darkness": case "Coolheaded": case "Glacier": case "Refract": case "IceLance": case "ColdSnap":
            case "Chill": case "Null": case "ShadowShield": case "ConsumingShadow": case "Glasswork": case "Fusion":
            case "MeteorStrike": case "Spinner": case "Ignition":
            {
                var sim = new OrbSim(p, Sim.Clone(rng.CombatTargets));
                if (!RunOrbCard(card, p, sim)) return null;
                FinishOrbSim(pr, sim);
                if (pr.IsEmpty) return null;
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
                AddAttackHits(pr, RandomAttack(card, p, Sim.IntVar(card, "Repeat", 1), Sim.Clone(rng.CombatTargets)), L.T("被打", "Hit"));
                break;
            case "RipAndTear":
                AddAttackHits(pr, RandomAttack(card, p, 2, Sim.Clone(rng.CombatTargets)), L.T("被打", "Hit"));
                break;
            case "SweepingGaze":
            {
                var osty = p.Osty;
                if (osty == null) { pr.Lines.Add(L.T("奥斯提不在场", "Osty is not in this fight")); break; }
                decimal dmg = 0m;
                try { dmg = card.DynamicVars["OstyDamage"].BaseValue; } catch { }
                AddAttackHits(pr, new DamageSim(p).RandomAttack(card, dmg, MegaCrit.Sts2.Core.ValueProps.ValueProp.Move, 1, Sim.Clone(rng.CombatTargets), osty), L.T("被打", "Hit"));
                break;
            }
            case "Stardust":
                AddAttackHits(pr, RandomAttack(card, p, HitCountFromX(card, p, stars: true), Sim.Clone(rng.CombatTargets)), L.T("被打", "Hit"));
                break;
            case "Volley":
                AddAttackHits(pr, RandomAttack(card, p, HitCountFromX(card, p, stars: false), Sim.Clone(rng.CombatTargets)), L.T("被打", "Hit"));
                break;
            case "FlakCannon":
            {
                int statuses = 0;
                try { statuses = p.PlayerCombatState.AllCards.Count(c => c.Type == CardType.Status && c.Pile != null && c.Pile.Type != PileType.Exhaust); } catch { }
                AddAttackHits(pr, RandomAttack(card, p, statuses, Sim.Clone(rng.CombatTargets)), L.T("被打", "Hit"));
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
        "ArcaneScroll", "HeftyTablet", "LeadPaperweight", "LavaRock", "PhialHolster", "CursedPearl", "LargeCapsule",
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
    /// <summary>Relics whose AfterObtained draws from RunState.Rng.Niche (they shift a Niche draw that happens after them).</summary>
    private static readonly HashSet<string> NicheOnPickup = new()
    {
        "NewLeaf", "Astrolabe", "PandorasBox", "SereTalon", "NeowsBones", "FragrantMushroom", "SandCastle", "WarPaint", "Whetstone",
    };

    /// <summary>Relics with a random pickup effect that this mod predicts when they are hovered directly.</summary>
    private static readonly HashSet<string> RandomOnPickup = new()
    {
        "NewLeaf", "Astrolabe", "PandorasBox", "LeafyPoultice", "ArcaneScroll", "HeftyTablet", "LeadPaperweight", "PhialHolster",
        "LargeCapsule", "SmallCapsule", "ToyBox", "LostCoffer", "CallingBell", "Cauldron", "AlchemicalCoffer", "SereTalon", "FragrantMushroom", "SandCastle",
        "WarPaint", "Whetstone",
    };

    /// <summary>
    /// Mirror of the "random curses" loop of Neow's Bones / Sere Talon: Niche.NextItem over the unlocked curses that
    /// modifiers may generate, ordered by id, without repeats.
    /// </summary>
    /// <summary>
    /// Number of RunState.Rng.Niche draws a relic makes in AfterObtained (null = unknown). One NextItem per random
    /// transformation, one per curse, and whatever a StableShuffle of the candidate list takes (measured on a clone).
    /// </summary>
    private static int? NicheDrawsOnPickup(RelicModel r, Player p, List<CardModel> deck)
    {
        string t = r.GetType().Name;
        if (!NicheOnPickup.Contains(t)) return 0;
        try
        {
            switch (t)
            {
                case "NewLeaf":
                case "Astrolabe":
                    return r.DynamicVars["Cards"].IntValue;
                case "PandorasBox":
                    return deck.Count(c => { try { return c.IsBasicStrikeOrDefend && c.IsRemovable; } catch { return false; } });
                case "SereTalon":
                    return r.DynamicVars["Curses"].IntValue;
                case "FragrantMushroom":
                case "SandCastle":
                case "WarPaint":
                case "Whetstone":
                {
                    var pool = deck.Where(c => c != null && SafeIsUpgradable(c)
                        && (t != "WarPaint" || c.Type == CardType.Skill)
                        && (t != "Whetstone" || c.Type == CardType.Attack)).ToList();
                    var clone = Sim.Clone(p.RunState.Rng.Niche);
                    int before = clone.Counter;
                    Sim.ShuffleOrder(pool, clone);
                    return clone.Counter - before;
                }
            }
        }
        catch { }
        return null;
    }

    private static void AddRandomCurses(Prediction pr, Player p, int count, bool uncertain, int nicheAhead = 0)
    {
        try
        {
            var curses = ModelDb.CardPool<CurseCardPool>().GetUnlockedCards(p.UnlockState, p.RunState.CardMultiplayerConstraint)
                .Where(c => c.CanBeGeneratedByModifiers).OrderBy(c => c.Id).ToList();
            var niche = p.RunState.Rng.Niche;
            var r = new Rng(niche.Seed, niche.Counter + nicheAhead);
            pr.CardScale = 0.45f;
            for (int i = 0; i < count; i++)
            {
                var c = r.NextItem(curses);
                if (c == null) break;
                curses.Remove(c);
                pr.Cards.Add(new PredCard(c, L.T("诅咒", "Curse") + (uncertain ? " ?" : ""), 0));
            }
            if (uncertain)
                pr.Lines.Add(L.T("诅咒在领取上面的遗物之后才抽；其中有遗物会先消耗同一条随机数，实际诅咒可能不同", "The curse is drawn after you claim the relics above; one of them uses the same random stream first, so the curse may differ"));
        }
        catch (System.Exception ex) { PLog.Write($"curse prediction failed: {ex.Message}"); }
    }

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
            case "LargeCapsule":
            {
                int n = 2;
                try { n = relic.DynamicVars.ContainsKey("Relics") ? relic.DynamicVars["Relics"].IntValue : 2; } catch { }
                pr.Title = relicName + L.T("：会获得的遗物", ": relics you get");
                AddRelicPulls(pr, p, n);
                break;
            }
            case "LavaRock":
            {
                // TryModifyRewards adds two RelicReward(player) to the Act 1 boss rewards; they are drawn from the
                // Rewards stream at that moment, after everything the act consumes, so nothing is predictable now.
                int n = 2;
                try { n = relic.DynamicVars["Relics"].IntValue; } catch { }
                pr.Title = relicName + L.T("：第一幕Boss额外掉落的遗物", ": extra relics from the Act 1 boss");
                pr.Lines.Add(L.T($"{n} 件遗物在打完第一幕Boss时才抽取，现在无法预测", $"The {n} relics are drawn when the Act 1 boss rewards appear; nothing to predict yet"));
                break;
            }
            case "NeowsBones":
            {
                // AfterObtained: Rewards.Shuffle(valid Neow relics).Take(N) are offered (skipping disallowed), and only
                // after they are claimed the curse is drawn from Niche.
                int nRelics = 2, nCurses = 1;
                try { nRelics = relic.DynamicVars["Relics"].IntValue; } catch { }
                try { nCurses = relic.DynamicVars["Curses"].IntValue; } catch { }
                var valid = (HarmonyLib.AccessTools.Method(relic.GetType(), "GetValidRelics")?.Invoke(null, new object[] { p }) as IEnumerable<RelicModel>)?.ToList();
                if (valid == null) return null;
                Sim.Clone(p.PlayerRng.Rewards).Shuffle(valid);
                var picked = valid.Take(nRelics).ToList();
                pr.Title = relicName + L.T("：会获得的遗物和诅咒", ": the relics and the curse you get");
                pr.Lines.Add(L.T("获得遗物: ", "Relics: ") + string.Join(", ", picked.Select(Name)));
                // The curse is drawn after both relics are claimed; every Rng draw is exactly one generator step
                // (Rng.FastForwardCounter relies on that), so relics that use Niche first just shift the stream.
                int nicheAhead = 0;
                bool nicheUnknown = false;
                foreach (var r in picked)
                {
                    int? k = NicheDrawsOnPickup(r, p, deck);
                    if (k == null) nicheUnknown = true; else nicheAhead += k.Value;
                }
                AddRandomCurses(pr, p, nCurses, nicheUnknown, nicheAhead);
                if (nicheAhead > 0 && !nicheUnknown)
                    pr.Lines.Add(L.T($"诅咒已按「先领取上面的遗物（它们先用掉 {nicheAhead} 次随机数）」推算", $"Curse computed for after the relics above are claimed (they use {nicheAhead} draws of the same stream first)"));
                if (picked.Any(r => RandomOnPickup.Contains(r.GetType().Name)))
                    pr.Lines.Add(L.T("其中带随机效果的遗物，要到领取时才结算它自己的随机结果", "A relic above with its own random effect rolls it when you claim it"));
                break;
            }
            case "SereTalon":
            {
                int nCurses = 2, nWishes = 0;
                try { nCurses = relic.DynamicVars["Curses"].IntValue; } catch { }
                try { nWishes = relic.DynamicVars["Wishes"].IntValue; } catch { }
                pr.Title = relicName + L.T("：会加入牌组的诅咒", ": the curses added to your deck");
                AddRandomCurses(pr, p, nCurses, false);
                if (nWishes > 0) pr.Lines.Add(L.T($"另外获得 {nWishes} 张愿望", $"Plus {nWishes} Wish"));
                break;
            }
            case "AlchemicalCoffer":
            {
                int n = 4;
                try { n = relic.DynamicVars["PotionSlots"].IntValue; } catch { }
                var pots = Sim.RandomPotions(p, n, Sim.Clone(p.RunState.Rng.CombatPotionGeneration), inCombatPool: false);
                pr.Title = relicName + L.T("：会获得的药水", ": potions you get");
                pr.Lines.Add(string.Join(", ", pots.Select(Name)));
                break;
            }
            case "FragrantMushroom":
            case "SandCastle":
            case "WarPaint":
            case "Whetstone":
            {
                // StableShuffle(upgradable cards, Niche).Take(Cards)
                int n = 1;
                try { n = relic.DynamicVars["Cards"].IntValue; } catch { }
                var pool = deck.Where(c => c != null && SafeIsUpgradable(c)
                    && (type != "WarPaint" || c.Type == CardType.Skill)
                    && (type != "Whetstone" || c.Type == CardType.Attack)).ToList();
                var picked = Sim.ShuffleOrder(pool, Sim.Clone(p.RunState.Rng.Niche)).Take(n).ToList();
                pr.Title = relicName + L.T($"：会升级的{n}张牌", $": the {n} cards that get upgraded");
                pr.CardScale = 0.42f;
                pr.CardsPerRow = 6;
                foreach (var c in picked) pr.Cards.Add(new PredCard(c, L.T("升级", "Upgraded"), SafeUpgradeLevel(c) + 1));
                if (picked.Count == 0) pr.Lines.Add(L.T("牌组里没有符合条件的可升级牌", "No matching upgradable card in the deck"));
                break;
            }
            case "SmallCapsule":
            {
                pr.Title = relicName + L.T("：会获得的遗物", ": the relic you get");
                AddRelicPulls(pr, p, 1);
                break;
            }
            case "ToyBox":
            {
                int n = 3;
                try { n = relic.DynamicVars["Relics"].IntValue; } catch { }
                pr.Title = relicName + L.T("：会获得的蜡制遗物", ": the wax relics you get");
                AddRelicPulls(pr, p, n);
                break;
            }
            case "LostCoffer":
            {
                // Rewards are populated in order with one Rewards stream: the 3-card reward, then the random potion.
                var rewards = Sim.Clone(p.PlayerRng.Rewards);
                var options = new CardCreationOptions(new[] { p.Character.CardPool }, CardCreationSource.Other, CardRarityOddsType.RegularEncounter);
                var cards = Sim.CreateForReward(p, 3, options, rewards);
                pr.Title = relicName + L.T("：三选一的牌和会获得的药水", ": the 3 cards offered and the potion you get");
                pr.CardScale = 0.42f;
                pr.CardsPerRow = 6;
                foreach (var (card, upgraded) in cards) pr.Cards.Add(new PredCard(card, L.T("三选一", "Pick 1 of 3"), upgraded ? 1 : 0));
                var pots = Sim.RandomPotions(p, 1, rewards, inCombatPool: false);
                pr.Lines.Add(L.T("药水: ", "Potion: ") + string.Join(", ", pots.Select(Name)));
                break;
            }
            case "CallingBell":
            {
                // GenerateRewards(): three RelicRewards with fixed rarities (common, uncommon, rare) = three
                // PullNextRelicFromFront(player, rarity) calls on the same grab bag, no rarity roll.
                pr.Title = relicName + L.T("：会获得的三件遗物（另加一张铃铛的诅咒）", ": the three relics you get (plus Curse of the Bell)");
                var peek = new RelicPeek(p);
                var names = new List<string>();
                foreach (var rarity in new[] { MegaCrit.Sts2.Core.Entities.Relics.RelicRarity.Common, MegaCrit.Sts2.Core.Entities.Relics.RelicRarity.Uncommon, MegaCrit.Sts2.Core.Entities.Relics.RelicRarity.Rare })
                {
                    var (r, _) = peek.Pull(rarity, _ => true);
                    names.Add(r == null ? L.T("（遗物池将重新填充，无法预测）", "(relic pool refills, cannot predict)") : Name(r));
                }
                pr.Lines.Add(L.T("获得遗物: ", "Relics: ") + string.Join(", ", names));
                break;
            }
            case "Cauldron":
            {
                // N PotionReward(player): each is one CreateRandomPotionOutOfCombat on the Rewards stream, in order.
                int n = 5;
                try { n = relic.DynamicVars["Potions"].IntValue; } catch { }
                var rewards = Sim.Clone(p.PlayerRng.Rewards);
                var pots = new List<PotionModel>();
                for (int i = 0; i < n; i++) pots.AddRange(Sim.RandomPotions(p, 1, rewards, inCombatPool: false));
                pr.Title = relicName + L.T("：会提供的药水", ": the potions offered");
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
    /// <summary>Event options that grant "a random relic" = the next relic pulled from the front of the grab bag.</summary>
    private static int RelicPullCount(EventModel ev, string textKey)
    {
        string suffix = OptionSuffix(textKey);
        return ev.GetType().Name switch
        {
            "ThisOrThat" when suffix == "ORNATE" => 1,
            "PunchOff" when suffix == "NAB" => 1,
            "RoundTeaParty" when suffix is "PICK_FIGHT" or "CONTINUE_FIGHT" => 1,
            "RanwidTheElder" when suffix is "GOLD" or "POTION" => 1,
            "RanwidTheElder" when suffix == "RELIC" => 2, // trade one relic, get two

            "UnrestSite" when suffix == "KILL" => 1,
            "LuminousChoir" when suffix == "OFFER_TRIBUTE" => 1,
            "Trial" when textKey.EndsWith("MERCHANT.options.GUILTY") => 1,
            _ => 0,
        };
    }

    private static void AddRelicPulls(Prediction pr, Player p, int count)
    {
        var pulls = Sim.PeekRelicsFromFront(p, count);
        var names = new List<string>();
        foreach (var (relic, note) in pulls)
        {
            if (relic == null) { names.Add(L.T("（遗物池将重新填充，无法预测）", "(relic pool refills, cannot predict)")); continue; }
            string n;
            try { n = relic.Title.GetFormattedText(); } catch { n = relic.Id.Entry; }
            names.Add(n);
        }
        pr.Lines.Add(L.T("获得遗物: ", "Relic: ") + string.Join(", ", names));
    }

    // ───────────────────────────── rest site ─────────────────────────────

    public static bool IsPredictableRestSiteOption(MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption option)
        => option.GetType().Name == "DigRestSiteOption";

    /// <summary>Dig (Shovel): the next relic from the front of the grab bag.</summary>
    public static Prediction? ForRestSiteOption(MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption option, Player p)
    {
        if (!IsPredictableRestSiteOption(option)) return null;
        var pr = new Prediction { Title = L.T("挖掘 → 会挖到的遗物", "Dig → the relic you get") };
        AddRelicPulls(pr, p, 1);
        return pr;
    }

    public static bool HasEventOptionPredictor(EventModel ev, string textKey)
    {
        if (RelicPullCount(ev, textKey) > 0) return true;
        string suffix = OptionSuffix(textKey);
        return ev.GetType().Name switch
        {
            "EndlessConveyor" => suffix is "OBSERVE_CHEF" or "SPICY_SNAPPY" or "JELLY_LIVER" or "SUSPICIOUS_CONDIMENT" or "FRIED_EEL",
            "TheLegendsWereTrue" => suffix == "SLOWLY_FIND_AN_EXIT",
            "PotionCourier" => suffix == "RANSACK",
            "Wellspring" => suffix == "BOTTLE",
            "InfestedAutomaton" => suffix is "STUDY" or "TOUCH_CORE",
            "WelcomeToWongos" => suffix is "BARGAIN_BIN" or "LEAVE",
            "TrashHeap" => suffix is "DIVE_IN" or "GRAB",
            "DollRoom" => suffix == "RANDOM",
            "Reflections" => suffix == "TOUCH_A_MIRROR",
            "TabletOfTruth" => suffix.StartsWith("DECIPHER"),
            "DoorsOfLightAndDark" => suffix == "LIGHT",
            "SlipperyBridge" => suffix.StartsWith("HOLD_ON"),
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
        // "Obtain a random relic": the next relic from the front of the grab bag.
        int relicPulls = RelicPullCount(ev, textKey);
        if (relicPulls > 0)
        {
            pr.Title = L.T("随机遗物 → 会拿到", "Random relic → you get");
            AddRelicPulls(pr, p, relicPulls);
            return pr;
        }

        Rng? EvRng() { try { return ev.Rng; } catch { return null; } }

        // ── Random potion drawn with one Rewards NextItem over the unlocked character + shared potion pools ──
        // (Endless Conveyor's Suspicious Condiment, The Legends Were True's exit, Potion Courier's Ransack
        // (uncommon only), Wellspring's Bottle). HP loss before the draw does not touch the stream.
        string? potionTitle = (evName, suffix) switch
        {
            ("EndlessConveyor", "SUSPICIOUS_CONDIMENT") => L.T("可疑调味品 → 得到的药水", "Suspicious Condiment → potion"),
            ("TheLegendsWereTrue", "SLOWLY_FIND_AN_EXIT") => L.T("耐心寻找出口 → 得到的药水", "Slowly find an exit → potion"),
            ("PotionCourier", "RANSACK") => L.T("洗劫 → 得到的药水", "Ransack → potion"),
            ("Wellspring", "BOTTLE") => L.T("装瓶 → 得到的药水", "Bottle → potion"),
            _ => null,
        };
        if (potionTitle != null)
        {
            pr.Title = potionTitle;
            try
            {
                IEnumerable<PotionModel> items = p.Character.PotionPool.GetUnlockedPotions(p.UnlockState)
                    .Concat(ModelDb.PotionPool<SharedPotionPool>().GetUnlockedPotions(p.UnlockState));
                if (evName == "PotionCourier") items = items.Where(x => x.Rarity == PotionRarity.Uncommon);
                var potion = Sim.Clone(p.PlayerRng.Rewards).NextItem(items);
                if (potion == null) return null;
                pr.Lines.Add(L.T("药水: ", "Potion: ") + Name(potion));
            }
            catch (System.Exception ex) { PLog.Write($"Potion option prediction failed: {ex.Message}"); return null; }
            return pr;
        }

        // ── One reward card added straight to the deck (CardFactory.CreateForReward with the event's options) ──
        if (evName == "InfestedAutomaton" && suffix is "STUDY" or "TOUCH_CORE")
        {
            try
            {
                CardCreationOptions options = suffix == "STUDY"
                    ? CardCreationOptions.ForNonCombatWithDefaultOdds(new[] { p.Character.CardPool }, c => c.Type == CardType.Power)
                    : CardCreationOptions.ForNonCombatWithDefaultOdds(new[] { p.Character.CardPool }, c =>
                        {
                            var e = c.EnergyCost;
                            return e != null && e.Canonical == 0 && !e.CostsX;
                        }).WithFlags(CardCreationFlags.NoCardPoolModifications);
                pr.Title = suffix == "STUDY"
                    ? L.T("研究 → 加入牌组的能力牌", "Study → the Power added to your deck")
                    : L.T("触碰核心 → 加入牌组的0费牌", "Touch the core → the 0-cost card added to your deck");
                AddRewardCards(pr, p, 1, options, L.T("加入牌组", "Added to deck"));
            }
            catch (System.Exception ex) { PLog.Write($"Infested Automaton prediction failed: {ex.Message}"); return null; }
            return pr;
        }
        if (evName == "EndlessConveyor" && suffix == "FRIED_EEL")
        {
            try
            {
                var options = CardCreationOptions.ForNonCombatWithDefaultOdds(new CardPoolModel[] { ModelDb.CardPool<ColorlessCardPool>() });
                pr.Title = L.T("炸鳗鱼 → 加入牌组的无色牌", "Fried Eel → the colorless card added to your deck");
                AddRewardCards(pr, p, 1, options, L.T("加入牌组", "Added to deck"));
            }
            catch (System.Exception ex) { PLog.Write($"Fried Eel prediction failed: {ex.Message}"); return null; }
            return pr;
        }

        // ── Wongo's: bargain bin = next common shop-allowed relic (fixed rarity, no roll); leave = random downgrade ──
        if (evName == "WelcomeToWongos" && suffix == "BARGAIN_BIN")
        {
            try
            {
                var (relic, note) = new RelicPeek(p).Pull(MegaCrit.Sts2.Core.Entities.Relics.RelicRarity.Common, r => r.IsAllowedInShops);
                pr.Title = L.T("特价箱 → 会拿到的遗物", "Bargain bin → the relic you get");
                pr.Lines.Add(L.T("获得遗物: ", "Relic: ") + (relic == null ? L.T("（遗物池将重新填充，无法预测）", "(relic pool refills, cannot predict)") : Name(relic)));
            }
            catch (System.Exception ex) { PLog.Write($"Bargain bin prediction failed: {ex.Message}"); return null; }
            return pr;
        }
        if (evName == "WelcomeToWongos" && suffix == "LEAVE")
        {
            var r = EvRng(); if (r == null) return null;
            var upgraded = deck.Where(c => { try { return c.IsUpgraded; } catch { return false; } }).ToList();
            pr.Title = L.T("离开 → 会被降级的牌", "Leave → the card that gets downgraded");
            if (upgraded.Count == 0) { pr.Lines.Add(L.T("牌组里没有已升级的牌，不会降级任何牌", "No upgraded card in the deck: nothing is downgraded")); return pr; }
            var card = Sim.Clone(r).NextItem(upgraded);
            if (card == null) return null;
            pr.CardScale = 0.45f;
            pr.Cards.Add(new PredCard(card, L.T("降级", "Downgraded"), SafeUpgradeLevel(card)));
            return pr;
        }

        // ── Trash Heap: one of five fixed relics / one of ten fixed cards, chosen with the event's Rng ──
        if (evName == "TrashHeap" && suffix is "DIVE_IN" or "GRAB")
        {
            var r = EvRng(); if (r == null) return null;
            try
            {
                if (suffix == "DIVE_IN")
                {
                    var relics = HarmonyLib.AccessTools.Property(ev.GetType(), "Relics")?.GetValue(null) as RelicModel[];
                    var relic = relics == null ? null : Sim.Clone(r).NextItem(relics);
                    if (relic == null) return null;
                    pr.Title = L.T("潜入 → 会拿到的遗物", "Dive in → the relic you get");
                    pr.Lines.Add(L.T("获得遗物: ", "Relic: ") + Name(relic));
                }
                else
                {
                    var cards = HarmonyLib.AccessTools.Property(ev.GetType(), "Cards")?.GetValue(null) as CardModel[];
                    var card = cards == null ? null : Sim.Clone(r).NextItem(cards);
                    if (card == null) return null;
                    pr.Title = L.T("翻找 → 加入牌组的牌", "Grab → the card added to your deck");
                    pr.CardScale = 0.45f;
                    pr.Cards.Add(new PredCard(card, L.T("加入牌组", "Added to deck"), 0));
                }
            }
            catch (System.Exception ex) { PLog.Write($"Trash Heap prediction failed: {ex.Message}"); return null; }
            return pr;
        }

        // ── Doll Room "choose at random": one of the three dolls' relics ──
        if (evName == "DollRoom" && suffix == "RANDOM")
        {
            var r = EvRng(); if (r == null) return null;
            try
            {
                var dolls = HarmonyLib.AccessTools.Field(ev.GetType(), "_dolls")?.GetValue(null) as System.Array;
                if (dolls == null) return null;
                var pick = Sim.Clone(r).NextItem(dolls.Cast<object>().ToList());
                var relic = pick == null ? null : HarmonyLib.AccessTools.Field(pick.GetType(), "relic")?.GetValue(pick) as RelicModel;
                if (relic == null) return null;
                pr.Title = L.T("随机拿一个 → 会拿到的遗物", "Pick at random → the relic you get");
                pr.Lines.Add(L.T("获得遗物: ", "Relic: ") + Name(relic));
            }
            catch (System.Exception ex) { PLog.Write($"Doll Room prediction failed: {ex.Message}"); return null; }
            return pr;
        }

        // ── Reflections "touch a mirror": 2 random upgraded cards are downgraded, then 4 random upgradable cards upgraded ──
        if (evName == "Reflections" && suffix == "TOUCH_A_MIRROR")
        {
            var r0 = EvRng(); if (r0 == null) return null;
            var r = Sim.Clone(r0);
            var upgradedList = deck.Where(c => { try { return c.IsUpgraded; } catch { return false; } }).ToList();
            var down = new List<CardModel>();
            for (int i = 0; i < 2 && upgradedList.Count > 0; i++)
            {
                var c = r.NextItem(upgradedList);
                if (c == null) break;
                upgradedList.Remove(c);
                down.Add(c);
            }
            // The upgradable list is built after the downgrades, so the downgraded cards are candidates again.
            var upgradableList = deck.Where(c => down.Contains(c) || (SafeIsUpgradable(c))).ToList();
            var up = new List<CardModel>();
            for (int i = 0; i < 4 && upgradableList.Count > 0; i++)
            {
                var c = r.NextItem(upgradableList);
                if (c == null) break;
                upgradableList.Remove(c);
                up.Add(c);
            }
            pr.Title = L.T("触摸镜子 → 降级2张，再升级4张", "Touch a mirror → 2 downgrades, then 4 upgrades");
            pr.CardScale = 0.42f;
            foreach (var c in down) pr.Cards.Add(new PredCard(c, L.T("降级", "Downgraded"), SafeUpgradeLevel(c)));
            foreach (var c in up) pr.Cards.Add(new PredCard(c, L.T("升级", "Upgraded"), (down.Contains(c) ? SafeUpgradeLevel(c) - 1 : SafeUpgradeLevel(c)) + 1));
            if (down.Count == 0) pr.Lines.Add(L.T("没有已升级的牌可降级", "No upgraded card to downgrade"));
            if (up.Count == 0) pr.Lines.Add(L.T("没有可升级的牌", "No upgradable card"));
            return pr;
        }

        // ── Tablet of Truth "decipher": one random upgradable card (the 5th decipher upgrades everything) ──
        if (evName == "TabletOfTruth" && suffix.StartsWith("DECIPHER"))
        {
            var r = EvRng(); if (r == null) return null;
            int decipherCount = 0;
            try { decipherCount = (int)(HarmonyLib.AccessTools.Field(ev.GetType(), "_decipherCount")?.GetValue(ev) ?? 0); } catch { }
            var upgradable = deck.Where(SafeIsUpgradable).ToList();
            pr.Title = L.T("破译 → 会升级的牌", "Decipher → the card that gets upgraded");
            if (decipherCount == 4) { pr.Lines.Add(L.T($"第5次破译：牌组里所有可升级的牌都会升级（{upgradable.Count}张）", $"5th decipher: every upgradable card in the deck is upgraded ({upgradable.Count})")); return pr; }
            if (upgradable.Count == 0) { pr.Lines.Add(L.T("牌组里没有可升级的牌，不会升级任何牌", "No upgradable card in the deck: nothing is upgraded")); return pr; }
            var card = Sim.Clone(r).NextItem(upgradable);
            if (card == null) return null;
            pr.CardScale = 0.45f;
            pr.Cards.Add(new PredCard(card, Name(card) + " → " + Name(card) + "+", SafeUpgradeLevel(card) + 1));
            return pr;
        }

        // ── Doors of Light and Dark "light": N random upgradable cards (StableShuffle, then Take) ──
        if (evName == "DoorsOfLightAndDark" && suffix == "LIGHT")
        {
            var r = EvRng(); if (r == null) return null;
            int n = 0;
            try { n = ev.DynamicVars["Cards"].IntValue; } catch { }
            var upgradable = deck.Where(SafeIsUpgradable).ToList();
            var picked = Sim.ShuffleOrder(upgradable, Sim.Clone(r)).Take(n).ToList();
            pr.Title = L.T($"光之门 → 会升级的{n}张牌", $"Light → the {n} cards that get upgraded");
            if (picked.Count == 0) { pr.Lines.Add(L.T("牌组里没有可升级的牌", "No upgradable card in the deck")); return pr; }
            pr.CardScale = 0.42f;
            foreach (var c in picked) pr.Cards.Add(new PredCard(c, L.T("升级", "Upgraded"), SafeUpgradeLevel(c) + 1));
            return pr;
        }

        // ── Slippery Bridge "hold on": the next random card the bridge will ask you to give up ──
        if (evName == "SlipperyBridge" && suffix.StartsWith("HOLD_ON"))
        {
            var r = EvRng(); if (r == null) return null;
            try
            {
                var current = HarmonyLib.AccessTools.Property(ev.GetType(), "RandomCardToLose")?.GetValue(ev) as CardModel;
                var skipped = HarmonyLib.AccessTools.Property(ev.GetType(), "SkippedRemovals")?.GetValue(ev) as HashSet<CardModel>;
                var list = current == null
                    ? deck.Where(c => c.Rarity != CardRarity.Basic).ToList()
                    : deck.Where(c => c.GetType() != current.GetType()).ToList();
                list.RemoveAll(c => !c.IsRemovable || c == current || (skipped?.Contains(c) ?? false));
                if (list.Count == 0) list = deck.Where(c => c.IsRemovable).ToList();
                var next = Sim.Clone(r).NextItem(list);
                if (next == null) return null;
                pr.Title = L.T("坚持 → 下一张会被要求放弃的牌", "Hold on → the next card the bridge asks for");
                pr.CardScale = 0.45f;
                pr.Cards.Add(new PredCard(next, L.T("下一张", "Next"), SafeUpgradeLevel(next)));
            }
            catch (System.Exception ex) { PLog.Write($"Slippery Bridge prediction failed: {ex.Message}"); return null; }
            return pr;
        }

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
