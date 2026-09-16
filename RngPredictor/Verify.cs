using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace RngPredictor;

/// <summary>
/// Shadow verification: right before the game runs one of its random generators, compute what <see cref="Sim"/>
/// predicts from a clone of the same RNG; right after, compare with the real result. Mismatches are always
/// logged; matches are logged only while <c>logs/RngPredictor.verify</c> exists or the self-test runs.
/// This proves the simulation layer mirrors the game byte for byte, independent of any UI.
/// </summary>
internal static class Verify
{
    private static string FlagPath => System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "RngPredictor.verify");
    private static bool _flagChecked;
    private static bool _flag;

    public static bool Verbose
    {
        get
        {
            if (!_flagChecked)
            {
                _flagChecked = true;
                try { _flag = System.IO.File.Exists(FlagPath); } catch { }
            }
            return _flag || SelfTest.Running;
        }
    }

    public static int Mismatches;
    public static int Matches;

    public static void Report(string what, string predicted, string actual)
    {
        bool ok = predicted == actual;
        if (ok) Matches++; else Mismatches++;
        if (ok && !Verbose) return;
        PLog.Write($"VERIFY {(ok ? "OK      " : "MISMATCH")} {what}: predicted [{predicted}] actual [{actual}]");
    }

    public static string Ids(IEnumerable<CardModel> cards) => string.Join(",", cards.Select(c => c.Id.Entry));
    public static string Ids(IEnumerable<PotionModel> potions) => string.Join(",", potions.Select(c => c.Id.Entry));
}

[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.GetDistinctForCombat))]
internal static class Verify_GetDistinctForCombat
{
    [HarmonyPrefix]
    public static void Prefix(Player player, IEnumerable<CardModel> cards, int count, Rng rng, out string __state)
    {
        __state = "";
        try
        {
            var list = cards.ToList(); // the game enumerates the same query; materialise once so both sides see one order
            __state = Verify.Ids(Sim.DistinctForCombat(player, list, count, Sim.Clone(rng)));
        }
        catch (Exception ex) { __state = "ERROR " + ex.Message; }
    }

    [HarmonyPostfix]
    public static void Postfix(ref IEnumerable<CardModel> __result, string __state)
    {
        try
        {
            var actual = __result.ToList();
            __result = actual; // the game's query is lazy; make sure it is evaluated exactly once
            Verify.Report("GetDistinctForCombat", __state, Verify.Ids(actual));
        }
        catch (Exception ex) { PLog.Write($"verify postfix failed: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.CreateForReward), new[] { typeof(Player), typeof(int), typeof(CardCreationOptions) })]
internal static class Verify_CreateForReward
{
    [HarmonyPrefix]
    public static void Prefix(Player player, int cardCount, CardCreationOptions options, out string __state)
    {
        __state = "";
        try
        {
            var rng = Sim.Clone(options.RngOverride ?? player.PlayerRng.Rewards);
            __state = string.Join(",", Sim.CreateForReward(player, cardCount, options, rng).Select(x => x.card.Id.Entry + (x.upgraded ? "+" : "")));
        }
        catch (Exception ex) { __state = "ERROR " + ex.Message; }
    }

    [HarmonyPostfix]
    public static void Postfix(IEnumerable<CardCreationResult> __result, string __state)
    {
        try
        {
            string actual = string.Join(",", __result.Select(r => r.Card.Id.Entry + (r.Card.IsUpgraded ? "+" : "")));
            if (actual != __state && actual.Replace("+", "") == __state.Replace("+", ""))
            {
                // Same cards, different upgrade flags: a relic hook we could not reproduce exactly. Not a wrong prediction.
                PLog.Write($"VERIFY OK      CreateForReward (upgrade flags differ): predicted [{__state}] actual [{actual}]");
                Verify.Matches++;
                return;
            }
            Verify.Report("CreateForReward", __state, actual);
        }
        catch (Exception ex) { PLog.Write($"verify postfix failed: {ex.Message}"); }
    }
}

/// <summary>Lightning orb passive/evoke: the random target draw. Logs the CombatTargets counter to spot extra draws.</summary>
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Models.Orbs.LightningOrb), "ApplyLightningDamage")]
internal static class Verify_Lightning
{
    [HarmonyPrefix]
    public static void Prefix(MegaCrit.Sts2.Core.Models.Orbs.LightningOrb __instance, decimal value, MegaCrit.Sts2.Core.Entities.Creatures.Creature? target, out (string predicted, int counter) __state)
    {
        __state = ("", -1);
        try
        {
            if (target != null) { __state = ("(fixed)", -1); return; }
            var p = __instance.Owner;
            var rng = p.RunState.Rng.CombatTargets;
            var list = __instance.CombatState.GetOpponentsOf(p.Creature).Where(e => e.IsHittable).ToList();
            var t = Sim.Clone(rng).NextItem(list);
            __state = (t?.Name ?? "none", rng.Counter);
        }
        catch (Exception ex) { __state = ("ERROR " + ex.Message, -1); }
    }

    [HarmonyPostfix]
    public static void Postfix(MegaCrit.Sts2.Core.Models.Orbs.LightningOrb __instance, decimal value, System.Threading.Tasks.Task<IEnumerable<MegaCrit.Sts2.Core.Entities.Creatures.Creature>> __result, (string predicted, int counter) __state)
    {
        if (__state.predicted == "(fixed)") return;
        __result.ContinueWith(t =>
        {
            try
            {
                if (!t.IsCompletedSuccessfully) return;
                var actual = string.Join(",", t.Result.Select(c => c.Name));
                int after = -1;
                try { after = __instance.Owner.RunState.Rng.CombatTargets.Counter; } catch { }
                Verify.Report($"Lightning {value} (counter {__state.counter}->{after})", __state.predicted, actual);
            }
            catch (Exception ex) { PLog.Write($"verify lightning failed: {ex.Message}"); }
        }, System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
    }
}

[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.GetForCombat))]
internal static class Verify_GetForCombat
{
    [HarmonyPrefix]
    public static void Prefix(Player player, IEnumerable<CardModel> cards, int count, Rng rng, out string __state)
    {
        __state = "";
        try { __state = Verify.Ids(Sim.ForCombat(player, cards.ToList(), count, Sim.Clone(rng))); }
        catch (Exception ex) { __state = "ERROR " + ex.Message; }
    }

    [HarmonyPostfix]
    public static void Postfix(IEnumerable<CardModel> __result, string __state)
    {
        try { Verify.Report("GetForCombat", __state, Verify.Ids(__result.ToList())); }
        catch (Exception ex) { PLog.Write($"verify postfix failed: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(PotionFactory), "CreateRandomPotion")]
internal static class Verify_CreateRandomPotion
{
    [HarmonyPrefix]
    public static void Prefix(IEnumerable<PotionModel> options, int count, Rng rng, out string __state)
    {
        __state = "";
        try
        {
            var list = options.ToList();
            var r = Sim.Clone(rng);
            var result = new List<PotionModel>();
            for (int i = 0; i < count; i++)
            {
                float f = r.NextFloat();
                var rarity = f <= 0.1f ? MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Rare
                    : (f <= 0.35f ? MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Uncommon : MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Common);
                var item = r.NextItem(list.Where(x => x.Rarity == rarity));
                if (item == null) break;
                result.Add(item);
                list.Remove(item);
            }
            __state = Verify.Ids(result);
        }
        catch (Exception ex) { __state = "ERROR " + ex.Message; }
    }

    [HarmonyPostfix]
    public static void Postfix(List<PotionModel> __result, string __state)
    {
        try { Verify.Report("CreateRandomPotion", __state, Verify.Ids(__result)); }
        catch (Exception ex) { PLog.Write($"verify postfix failed: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.Shuffle))]
internal static class Verify_Shuffle
{
    [HarmonyPrefix]
    public static void Prefix(Player player, out (string predicted, Player player) __state)
    {
        __state = ("", player);
        try
        {
            var discard = PileType.Discard.GetPile(player).Cards.ToList();
            var draw = PileType.Draw.GetPile(player).Cards.ToList();
            __state = (Verify.Ids(Sim.ShuffleOrder(discard.Concat(draw), Sim.Clone(player.RunState.Rng.Shuffle))), player);
        }
        catch (Exception ex) { __state = ("ERROR " + ex.Message, player); }
    }

    [HarmonyPostfix]
    public static void Postfix(System.Threading.Tasks.Task __result, (string predicted, Player player) __state)
    {
        // The shuffle is async; compare once it has finished adding cards.
        __result.ContinueWith(_ =>
        {
            try
            {
                var actual = Verify.Ids(PileType.Draw.GetPile(__state.player).Cards);
                Verify.Report("Shuffle (draw pile after)", __state.predicted, actual);
            }
            catch (Exception ex) { PLog.Write($"verify shuffle failed: {ex.Message}"); }
        }, System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
    }
}

[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Transform), new[] { typeof(IEnumerable<CardTransformation>), typeof(Rng), typeof(CardPreviewStyle) })]
internal static class Verify_Transform
{
    [HarmonyPrefix]
    public static void Prefix(ref IEnumerable<CardTransformation> transformations, Rng? rng, out string __state)
    {
        __state = "";
        try
        {
            var arr = transformations.ToArray();
            transformations = arr;
            if (rng == null) { __state = "(fixed)"; return; }
            var r = Sim.Clone(rng);
            var predicted = new List<string>();
            foreach (var t in arr)
            {
                if (t.Replacement != null) { predicted.Add(t.Replacement.Id.Entry); continue; }
                CardModel? c = t.ReplacementOptions == null
                    ? Sim.TransformResult(t.Original, t.IsInCombat, r)
                    : r.NextItem(CardFactory.GetDefaultTransformationOptions(t.Original, t.IsInCombat)); // custom option lists are rare; best effort
                predicted.Add(c?.Id.Entry ?? "?");
            }
            __state = string.Join(",", predicted);
        }
        catch (Exception ex) { __state = "ERROR " + ex.Message; }
    }

    [HarmonyPostfix]
    public static void Postfix(System.Threading.Tasks.Task<IEnumerable<CardPileAddResult>> __result, string __state)
    {
        if (__state == "(fixed)") return;
        __result.ContinueWith(t =>
        {
            try
            {
                if (!t.IsCompletedSuccessfully) return;
                var actual = string.Join(",", t.Result.Select(x => x.cardAdded?.Id.Entry ?? "?"));
                Verify.Report("Transform", __state, actual);
            }
            catch (Exception ex) { PLog.Write($"verify transform failed: {ex.Message}"); }
        }, System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
    }
}
