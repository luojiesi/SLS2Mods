using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace RngPredictor;

/// <summary>
/// Automated end-to-end test, enabled only when &lt;user data&gt;/logs/RngPredictor.selftest exists when the main
/// menu appears. Starts an unsaved run, enters a transform event and a fight, "hovers" cards/potions/piles
/// through the same code the real hover path uses (taking screenshots), then plays the cards and checks that
/// what happened is what was predicted. Logs PASS/FAIL and quits. Never touches saved runs.
/// </summary>
internal static class SelfTest
{
    private static string FlagPath => System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "RngPredictor.selftest");
    private static bool _started;
    public static bool Running;

    private sealed class RecordingSelector : MegaCrit.Sts2.Core.TestSupport.ICardSelector
    {
        public List<CardModel> LastOptions = new();

        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            LastOptions = options.ToList();
            PLog.Write($"SELFTEST selector: options {string.Join(",", LastOptions.Select(c => c.Id.Entry))} (min {minSelect} max {maxSelect})");
            var picked = LastOptions.Take(Math.Max(minSelect, Math.Min(maxSelect, 1))).ToList();
            return Task.FromResult<IEnumerable<CardModel>>(picked);
        }

        public MegaCrit.Sts2.Core.TestSupport.CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
        {
            return new MegaCrit.Sts2.Core.TestSupport.CardRewardSelection { card = options.Count > 0 ? options[0].Card : null };
        }
    }

    public static void MaybeStart()
    {
        if (_started) return;
        if (!System.IO.File.Exists(FlagPath)) return;
        _started = true;
        try { System.IO.File.Delete(FlagPath); } catch { }
        Running = true;
        PLog.Write("SELFTEST: flag found, starting");
        TaskHelper.RunSafely(Run());
    }

    private static async Task Run()
    {
        bool pass = false;
        try { pass = await RunInner(); }
        catch (Exception ex) { PLog.Write($"SELFTEST EXCEPTION: {ex}"); }
        PLog.Write($"SELFTEST: verify matches {Verify.Matches}, mismatches {Verify.Mismatches}");
        pass &= Verify.Mismatches == 0;
        PLog.Write(pass ? "SELFTEST RESULT: PASS" : "SELFTEST RESULT: FAIL");
        for (int i = 0; i < 30; i++) await NextFrame();
        PLog.Write("SELFTEST: quitting game");
        NGame.Instance?.GetTree().Quit();
    }

    private static readonly List<(string name, bool ok)> Checks = new();

    private static void Check(string name, bool ok, string detail)
    {
        Checks.Add((name, ok));
        PLog.Write($"SELFTEST CHECK {(ok ? "OK  " : "FAIL")} {name}: {detail}");
    }

    private static async Task<bool> RunInner()
    {
        var game = NGame.Instance!;
        if (!await WaitUntil(() => game.MainMenu != null, 30, "main menu")) return false;
        for (int i = 0; i < 30; i++) await NextFrame();
        string seed = "RNGTEST";
        try { var f = System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "RngPredictor.selftest.seed"); if (System.IO.File.Exists(f)) seed = System.IO.File.ReadAllText(f).Trim(); } catch { }
        PLog.Write($"SELFTEST: starting an unsaved Ironclad run (seed {seed})");
        await game.StartNewSingleplayerRun(ModelDb.Character<Ironclad>(), shouldSave: false, ActModel.GetDefaultList(),
                                           Array.Empty<ModifierModel>(), seed, GameMode.Standard);
        var rm = RunManager.Instance;
        var cm = CombatManager.Instance;
        if (!await WaitUntil(() => rm.IsInProgress && rm.DebugOnlyGetState()?.CurrentRoom != null, 60, "run in progress")) return false;
        var rs = rm.DebugOnlyGetState()!;
        PLog.Write($"SELFTEST: run loaded, room = {rs.CurrentRoom?.GetType().Name} ({rs.CurrentRoom?.RoomType})");
        for (int i = 0; i < 30; i++) await NextFrame();

        // ── 0. Neow: hover the relic options, then obtain New Leaf and transform through its screen ──
        try { await NeowTest(rm, rs); }
        catch (Exception ex) { PLog.Write($"SELFTEST: Neow test failed: {ex}"); Check("Neow test ran", false, ex.Message); }

        // ── 0b. Endless Conveyor: hover the options, then "observe the chef" (random upgrade) ──
        try { await ConveyorTest(rm, rs); }
        catch (Exception ex) { PLog.Write($"SELFTEST: conveyor test failed: {ex}"); Check("Conveyor test ran", false, ex.Message); }

        // ── 1. transform event: hover a deck card on the transform screen, then transform it ──
        try { await TransformTest(rm, rs); }
        catch (Exception ex) { PLog.Write($"SELFTEST: transform test failed: {ex}"); Check("transform test ran", false, ex.Message); }

        // ── 2. fight: a three-enemy encounter so random targeting is meaningful (override with
        //       logs/RngPredictor.selftest.encounter = <ENCOUNTER_ID>; "map" travels to the first monster node instead) ──
        rs = rm.DebugOnlyGetState()!;
        string encounterId = "EXOSKELETONS_WEAK";
        try { var f = System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "RngPredictor.selftest.encounter"); if (System.IO.File.Exists(f)) encounterId = System.IO.File.ReadAllText(f).Trim().ToUpperInvariant(); } catch { }
        if (encounterId != "MAP")
        {
            try
            {
                var modelId = new ModelId(ModelId.SlugifyCategory<EncounterModel>(), encounterId);
                var encounter = ModelDb.GetById<EncounterModel>(modelId).ToMutable();
                encounter.DebugRandomizeRng();
                PLog.Write($"SELFTEST: jumping to encounter {encounter.Id.Entry}");
                for (int i = 0; i < 30; i++) await NextFrame();
                await rm.EnterRoomDebug(MegaCrit.Sts2.Core.Rooms.RoomType.Monster, MegaCrit.Sts2.Core.Map.MapPointType.Unassigned, encounter);
                rs = rm.DebugOnlyGetState()!;
                PLog.Write($"SELFTEST: arrived, room = {rs.CurrentRoom?.GetType().Name} ({rs.CurrentRoom?.RoomType})");
            }
            catch (Exception ex) { PLog.Write($"SELFTEST: encounter jump failed: {ex.Message}"); }
        }
        if (rs.CurrentRoom is not CombatRoom)
        {
            var current = rs.CurrentMapPoint;
            var children = current?.Children?.ToList() ?? new List<MegaCrit.Sts2.Core.Map.MapPoint>();
            var next = children.FirstOrDefault(c => c.PointType == MegaCrit.Sts2.Core.Map.MapPointType.Monster)
                       ?? children.FirstOrDefault(c => c.PointType == MegaCrit.Sts2.Core.Map.MapPointType.Elite)
                       ?? children.FirstOrDefault();
            if (next == null) { PLog.Write("SELFTEST: no next map point"); return false; }
            PLog.Write($"SELFTEST: traveling to ({next.coord.col},{next.coord.row}) type {next.PointType}");
            for (int i = 0; i < 30; i++) await NextFrame();
            await rm.EnterMapCoord(next.coord);
            rs = rm.DebugOnlyGetState()!;
            PLog.Write($"SELFTEST: arrived, room = {rs.CurrentRoom?.GetType().Name} ({rs.CurrentRoom?.RoomType})");
            if (rs.CurrentRoom is not CombatRoom) { PLog.Write("SELFTEST: not a combat"); return false; }
        }
        if (!await WaitUntil(() => cm.IsInProgress && rm.ActionQueueSynchronizer.CombatState == ActionSynchronizerCombatState.PlayPhase, 60, "combat play phase")) return false;
        await WaitForIdlePlayPhase(30);
        for (int i = 0; i < 60; i++) await NextFrame();

        var me = rs.Players[0];
        PLog.Write($"SELFTEST: combat started, hand = {string.Join(",", me.PlayerCombatState!.Hand.Cards.Select(c => c.Id.Entry))}, enemies = {string.Join(",", me.Creature.CombatState!.HittableEnemies.Select(e => e.Name))}");

        await PlayerCmd.GainEnergy(20, me);
        // Havoc first (hand limit 10): predict the top card of the draw pile, play, and check it was the one exhausted.
        var havoc = await AddToHand<Havoc>(rs, me);
        if (havoc != null)
        {
            var pr = Predictors.ForHandCard(havoc);
            PLog.Write($"SELFTEST hover HAVOC: {(pr == null ? "(no prediction)" : Describe(pr))}");
            var predicted = pr?.Cards.FirstOrDefault()?.Card;
            var exhaustBefore = PileType.Exhaust.GetPile(me).Cards.ToHashSet();
            await Play(rm, havoc, null);
            var exhausted = PileType.Exhaust.GetPile(me).Cards.Where(c => !exhaustBefore.Contains(c) && c != havoc).ToList();
            Check("Havoc top card", predicted != null && exhausted.Count == 1 && ReferenceEquals(exhausted[0], predicted), $"predicted [{predicted?.Id.Entry}] actual [{string.Join(",", exhausted.Select(c => c.Id.Entry))}]");
        }

        // Test cards into the hand (hand limit 10: 5 starting + 5).
        var discovery = await AddToHand<Discovery>(rs, me);
        var infernal = await AddToHand<InfernalBlade>(rs, me);
        var boomerang = await AddToHand<SwordBoomerang>(rs, me);
        var trueGrit = await AddToHand<TrueGrit>(rs, me);
        var metamorphosis = await AddToHand<Metamorphosis>(rs, me);
        await PotionCmd.TryToProcure<AttackPotion>(me);
        await PotionCmd.TryToProcure<SneckoOil>(me);
        await PlayerCmd.GainEnergy(20, me);
        for (int i = 0; i < 30; i++) await NextFrame();
        await WaitForIdlePlayPhase(30);
        PLog.Write($"SELFTEST: hand now = {string.Join(",", me.PlayerCombatState.Hand.Cards.Select(c => c.Id.Entry))}, energy {me.PlayerCombatState.Energy}, potions {string.Join(",", me.Potions.Select(p => p.Id.Entry))}");

        // ── 3. hover everything through the real hover path and take screenshots ──
        var hand = NPlayerHand.Instance;
        int shotIdx = 0;
        if (hand != null)
        {
            bool focusPathOk = true;
            int predicted = 0;
            foreach (var holder in hand.ActiveHolders.ToList())
            {
                var card = holder.CardModel;
                if (card == null) continue;
                // Real focus path: Control focus -> FocusEntered -> NCardHolder.OnFocus -> our Harmony postfix.
                holder.GrabFocus();
                for (int i = 0; i < 8; i++) await NextFrame();
                var pr = Predictors.ForHandCard(card);
                bool showing = PredictionManager.OverlayShowing;
                PLog.Write($"SELFTEST hover {card.Id.Entry}: {(pr == null ? "(no prediction)" : Describe(pr))} overlayShowing={showing}");
                if (pr != null) { predicted++; Shot($"{++shotIdx:00}_hover_{card.Id.Entry}"); }
                if (showing != (pr != null)) focusPathOk = false;
                holder.ReleaseFocus();
                for (int i = 0; i < 4; i++) await NextFrame();
                if (PredictionManager.OverlayShowing) { focusPathOk = false; PLog.Write("SELFTEST: overlay still showing after unfocus"); }
            }
            Check("real focus path shows/hides overlay", focusPathOk && predicted > 0, $"{predicted} cards with predictions");
        }
        foreach (var potion in me.Potions.ToList())
        {
            PredictionManager.OnPotionHovered(potion);
            for (int i = 0; i < 8; i++) await NextFrame();
            var pr = Predictors.ForPotion(potion);
            PLog.Write($"SELFTEST hover potion {potion.Id.Entry}: {(pr == null ? "(no prediction)" : Describe(pr))}");
            if (pr != null) Shot($"{++shotIdx:00}_hover_{potion.Id.Entry}");
            PredictionManager.OnPotionUnhovered();
            for (int i = 0; i < 2; i++) await NextFrame();
        }
        var discardBtn = NCombatRoom.Instance?.Ui?.DiscardPile;
        if (discardBtn != null)
        {
            PredictionManager.OnPileFocused(discardBtn);
            for (int i = 0; i < 8; i++) await NextFrame();
            var pr = Predictors.ForShuffle(me);
            PLog.Write($"SELFTEST hover discard pile: {(pr == null ? "(no prediction)" : Describe(pr))}");
            Shot($"{++shotIdx:00}_hover_discard");
            PredictionManager.OnPileUnfocused(discardBtn);
        }

        // ── 4. play the cards and compare with the predictions made a moment before ──
        var selector = new RecordingSelector();
        using var selectorScope = CardSelectCmd.UseSelector(selector);

        // Sword Boomerang: predicted random targets vs HP lost.
        if (boomerang != null)
        {
            var pr = Predictors.ForHandCard(boomerang)!;
            var enemies = me.Creature.CombatState!.HittableEnemies.ToList();
            var hpBefore = enemies.ToDictionary(e => e, e => e.CurrentHp + e.Block);
            int dmg = (int)boomerang.DynamicVars.Damage.BaseValue;
            var predictedLoss = new Dictionary<Creature, int>();
            foreach (var (creature, label) in pr.Targets)
            {
                int hits = int.Parse(label.Substring(label.LastIndexOf('×') + 1));
                predictedLoss[creature] = hits * dmg;
            }
            await Play(rm, boomerang, null);
            var actual = enemies.ToDictionary(e => e, e => hpBefore[e] - (e.CurrentHp + e.Block));
            string p = string.Join(", ", enemies.Select(e => $"{e.Name}={predictedLoss.GetValueOrDefault(e, 0)}"));
            string a = string.Join(", ", enemies.Select(e => $"{e.Name}={actual[e]}"));
            Check("SwordBoomerang targets", p == a, $"predicted [{p}] actual [{a}]");
        }

        // Metamorphosis: predicted attacks vs the new cards in the draw pile.
        if (metamorphosis != null)
        {
            var pr = Predictors.ForHandCard(metamorphosis)!;
            var drawBefore = PileType.Draw.GetPile(me).Cards.ToHashSet();
            await Play(rm, metamorphosis, null);
            var added = PileType.Draw.GetPile(me).Cards.Where(c => !drawBefore.Contains(c)).Select(c => c.Id.Entry).OrderBy(x => x).ToList();
            var predicted = pr.Cards.Select(c => c.Card.Id.Entry).OrderBy(x => x).ToList();
            Check("Metamorphosis cards", string.Join(",", added) == string.Join(",", predicted), $"predicted [{string.Join(",", predicted)}] actual [{string.Join(",", added)}]");
        }

        // Infernal Blade: predicted attack vs the new card in hand.
        if (infernal != null)
        {
            var pr = Predictors.ForHandCard(infernal)!;
            var handBefore = PileType.Hand.GetPile(me).Cards.ToHashSet();
            await Play(rm, infernal, null);
            var added = PileType.Hand.GetPile(me).Cards.Where(c => !handBefore.Contains(c)).Select(c => c.Id.Entry).ToList();
            var predicted = pr.Cards.Select(c => c.Card.Id.Entry).ToList();
            Check("InfernalBlade card", string.Join(",", added) == string.Join(",", predicted), $"predicted [{string.Join(",", predicted)}] actual [{string.Join(",", added)}]");
        }

        // Discovery: predicted 3 options vs the options offered to the selector.
        if (discovery != null)
        {
            var pr = Predictors.ForHandCard(discovery)!;
            selector.LastOptions.Clear();
            await Play(rm, discovery, null);
            var offered = selector.LastOptions.Select(c => c.Id.Entry).ToList();
            var predicted = pr.Cards.Select(c => c.Card.Id.Entry).ToList();
            Check("Discovery options", string.Join(",", offered) == string.Join(",", predicted), $"predicted [{string.Join(",", predicted)}] actual [{string.Join(",", offered)}]");
        }

        // True Grit: predicted exhausted card vs the exhaust pile.
        if (trueGrit != null)
        {
            var pr = Predictors.ForHandCard(trueGrit)!;
            var predicted = pr.Cards.FirstOrDefault()?.Card;
            var exhaustBefore = PileType.Exhaust.GetPile(me).Cards.ToHashSet();
            await Play(rm, trueGrit, null);
            var exhausted = PileType.Exhaust.GetPile(me).Cards.Where(c => !exhaustBefore.Contains(c) && c != trueGrit).ToList();
            bool ok = predicted != null && exhausted.Count == 1 && ReferenceEquals(exhausted[0], predicted);
            Check("TrueGrit exhaust", ok, $"predicted [{predicted?.Id.Entry}] actual [{string.Join(",", exhausted.Select(c => c.Id.Entry))}]");
        }

        // Attack Potion: predicted 3 options vs the options offered.
        var attackPotion = me.Potions.FirstOrDefault(p => p is AttackPotion);
        if (attackPotion != null)
        {
            var pr = Predictors.ForPotion(attackPotion)!;
            selector.LastOptions.Clear();
            await UsePotion(rm, attackPotion, null);
            var offered = selector.LastOptions.Select(c => c.Id.Entry).ToList();
            var predicted = pr.Cards.Select(c => c.Card.Id.Entry).ToList();
            Check("AttackPotion options", string.Join(",", offered) == string.Join(",", predicted), $"predicted [{string.Join(",", predicted)}] actual [{string.Join(",", offered)}]");
        }

        // Snecko Oil: predicted drawn cards + costs vs actual.
        var snecko = me.Potions.FirstOrDefault(p => p is SneckoOil);
        if (snecko != null)
        {
            var pr = Predictors.ForPotion(snecko)!;
            var handBefore = PileType.Hand.GetPile(me).Cards.ToList();
            await UsePotion(rm, snecko, me.Creature);
            var handAfter = PileType.Hand.GetPile(me).Cards.ToList();
            var drawn = handAfter.Where(c => !handBefore.Contains(c)).Select(c => c.Id.Entry).ToList();
            var predictedDrawn = pr.Cards.Select(c => c.Card.Id.Entry).ToList();
            Check("SneckoOil drawn cards", string.Join(",", drawn) == string.Join(",", predictedDrawn), $"predicted [{string.Join(",", predictedDrawn)}] actual [{string.Join(",", drawn)}]");
            var actualCosts = new List<string>();
            foreach (var c in handAfter)
            {
                int cost;
                try { cost = c.EnergyCost.GetWithModifiers(CostModifiers.Local); } catch { cost = -99; }
                actualCosts.Add($"{c.Title}→{cost}");
            }
            string predictedCosts = pr.Lines.FirstOrDefault(l => l.Contains("→")) ?? "";
            string actualLine = string.Join("  ", actualCosts);
            // The predicted line only lists eligible cards; compare card-by-card on those.
            bool costsOk = true;
            foreach (var part in predictedCosts.Split("  ", StringSplitOptions.RemoveEmptyEntries).Skip(0))
            {
                var kv = part.Contains(": ") ? part.Substring(part.IndexOf(": ") + 2) : part;
                if (!kv.Contains('→')) continue;
                if (!actualLine.Contains(kv)) costsOk = false;
            }
            Check("SneckoOil costs", costsOk, $"predicted [{predictedCosts}] actual [{actualLine}]");
        }

        for (int i = 0; i < 30; i++) await NextFrame();
        Shot($"{++shotIdx:00}_end");
        bool all = Checks.Count > 0 && Checks.All(c => c.ok);
        PLog.Write($"SELFTEST: {Checks.Count(c => c.ok)}/{Checks.Count} checks passed");
        return all;
    }

    private static async Task TransformTest(RunManager rm, IRunState rs)
    {
        var eventModel = ModelDb.AllEvents.FirstOrDefault(e => e.Id.Entry == "AROMA_OF_CHAOS");
        if (eventModel == null) { PLog.Write("SELFTEST: AROMA_OF_CHAOS not found; skipping transform test"); return; }
        PLog.Write("SELFTEST: entering AROMA_OF_CHAOS");
        rs.AppendToMapPointHistory(MegaCrit.Sts2.Core.Map.MapPointType.Unknown, MegaCrit.Sts2.Core.Rooms.RoomType.Event, eventModel.Id);
        await rm.EnterRoom(new EventRoom(eventModel));
        if (!await WaitUntil(() => (NEventRoom.Instance?.Layout?.OptionButtons.Count() ?? 0) > 0, 30, "event options")) return;
        for (int i = 0; i < 30; i++) await NextFrame();
        PLog.Write("SELFTEST: choosing option 0 (transform)");
        rm.EventSynchronizer.ChooseLocalOption(0);
        if (!await WaitUntil(() => PredictionManager.CurrentTransformScreen != null && PredictionManager.CurrentTransformScreen.IsInsideTree(), 30, "transform screen")) return;
        var screen = PredictionManager.CurrentTransformScreen!;
        for (int i = 0; i < 30; i++) await NextFrame();
        var holders = new List<NGridCardHolder>();
        Collect(screen, holders);
        PLog.Write($"SELFTEST: transform screen has {holders.Count} grid holders");
        var holder = holders.FirstOrDefault(h => h.CardModel != null);
        if (holder == null) { Check("transform screen holders", false, "none"); return; }
        var card = holder.CardModel!;
        PredictionManager.OnHolderFocused(holder);
        for (int i = 0; i < 8; i++) await NextFrame();
        var ev = NEventRoom.Instance != null ? AccessTools.Field(typeof(NEventRoom), "_event")?.GetValue(NEventRoom.Instance) as EventModel : null;
        var pr = ev != null ? Predictors.ForTransform(card, ev.Rng, 1, "event") : null;
        PLog.Write($"SELFTEST hover transform {card.Id.Entry}: {(pr == null ? "(no prediction)" : Describe(pr))} overlayShowing={PredictionManager.OverlayShowing}");
        Shot("00_hover_transform");
        Check("transform hover shows overlay", pr != null && PredictionManager.OverlayShowing, $"prediction {(pr != null)}");
        PredictionManager.OnHolderUnfocused(holder);
        string predictedId = pr?.Cards.FirstOrDefault()?.Card.Id.Entry ?? "?";

        // Select the card and confirm through the screen's own handlers.
        var deckBefore = PileType.Deck.GetPile(card.Owner).Cards.ToHashSet();
        AccessTools.Method(typeof(NDeckTransformSelectScreen), "OnCardClicked")?.Invoke(screen, new object[] { card });
        for (int i = 0; i < 30; i++) await NextFrame();
        AccessTools.Method(typeof(NDeckTransformSelectScreen), "CompleteSelection")?.Invoke(screen, new object?[] { null });
        if (!await WaitUntil(() => PileType.Deck.GetPile(card.Owner).Cards.Any(c => !deckBefore.Contains(c)), 30, "transformed card")) { Check("transform happened", false, "timeout"); return; }
        for (int i = 0; i < 60; i++) await NextFrame();
        var added = PileType.Deck.GetPile(card.Owner).Cards.Where(c => !deckBefore.Contains(c)).Select(c => c.Id.Entry).ToList();
        Check("Transform result", added.Count == 1 && added[0] == predictedId, $"predicted [{predictedId}] actual [{string.Join(",", added)}]");
        // Leave the event.
        if (!await WaitUntil(() => NEventRoom.Instance == null || (NEventRoom.Instance.Layout?.OptionButtons.Count() ?? 0) > 0, 30, "event done")) return;
        for (int i = 0; i < 30; i++) await NextFrame();
        try { rm.EventSynchronizer.ChooseLocalOption(0); } catch (Exception ex) { PLog.Write($"SELFTEST: leaving event: {ex.Message}"); }
        for (int i = 0; i < 60; i++) await NextFrame();
    }

    private static async Task NeowTest(RunManager rm, IRunState rs)
    {
        var me = rs.Players[0];
        // Hover the real Neow option buttons (whatever this seed rolled).
        var layout = NEventRoom.Instance?.Layout;
        if (layout != null)
        {
            foreach (var btn in layout.OptionButtons.ToList())
            {
                PredictionManager.OnEventOptionFocused(btn);
                for (int i = 0; i < 6; i++) await NextFrame();
                PLog.Write($"SELFTEST Neow option {btn.Option?.Relic?.Id.Entry ?? btn.Option?.TextKey}: overlayShowing={PredictionManager.OverlayShowing}");
                if (PredictionManager.OverlayShowing) Shot($"00_neow_{btn.Option?.Relic?.Id.Entry}");
                PredictionManager.OnEventOptionUnfocused(btn);
            }
        }
        // Predict New Leaf for the whole deck, then actually obtain it (this opens the transform screen).
        var newLeaf = ModelDb.Relic<NewLeaf>().ToMutable();
        var deckPrediction = Predictors.ForNeowRelic(newLeaf, me);
        PLog.Write($"SELFTEST Neow New Leaf deck prediction: {(deckPrediction == null ? "(none)" : Describe(deckPrediction))}");
        if (deckPrediction != null)
        {
            // Render the whole-deck overlay at Neow exactly as an option hover would, and photograph it.
            PredictionManager.ShowForTest(deckPrediction);
            for (int i = 0; i < 10; i++) await NextFrame();
            Shot("00_neow_newleaf_deck");
            Check("Neow deck overlay rendered", PredictionManager.OverlayShowing, $"{deckPrediction.Cards.Count} cards");
            PredictionManager.Clear();
            for (int i = 0; i < 3; i++) await NextFrame();
        }
        var deckBefore = PileType.Deck.GetPile(me).Cards.ToList();
        var obtainTask = RelicCmd.Obtain(newLeaf, me);
        if (!await WaitUntil(() => PredictionManager.CurrentTransformScreen != null && PredictionManager.CurrentTransformScreen.IsInsideTree(), 30, "New Leaf transform screen")) return;
        var screen = PredictionManager.CurrentTransformScreen!;
        for (int i = 0; i < 30; i++) await NextFrame();
        var holders = new List<NGridCardHolder>();
        Collect(screen, holders);
        var holder = holders.FirstOrDefault(h => h.CardModel != null);
        if (holder == null) { Check("New Leaf screen holders", false, "none"); return; }
        var card = holder.CardModel!;
        LogPatches(typeof(NCardHolder), "OnFocus");
        LogPatches(typeof(NGridCardHolder), "OnFocus");
        LogPatches(typeof(NHandCardHolder), "OnFocus");
        // Real focus path on the grid (Control focus -> NCardHolder.OnFocus -> Harmony postfix).
        holder.GrabFocus();
        for (int i = 0; i < 8; i++) await NextFrame();
        var pr = PredictionManager.TransformPrediction(card);
        PLog.Write($"SELFTEST hover New Leaf screen {card.Id.Entry}: {(pr == null ? "(no prediction)" : Describe(pr))} overlayShowing={PredictionManager.OverlayShowing}");
        Shot("00_hover_newleaf");
        Check("New Leaf screen: real focus path shows overlay", PredictionManager.OverlayShowing, $"holder {holder.GetType().Name}, hasFocus={holder.HasFocus()}");
        holder.ReleaseFocus();
        for (int i = 0; i < 3; i++) await NextFrame();
        PredictionManager.OnHolderFocused(holder);
        for (int i = 0; i < 3; i++) await NextFrame();
        string screenPredicted = pr?.Cards.FirstOrDefault()?.Card.Id.Entry ?? "?";
        int deckIndex = deckBefore.IndexOf(card);
        string neowPredicted = deckPrediction != null && deckIndex >= 0 && deckIndex < deckPrediction.Cards.Count ? deckPrediction.Cards[deckIndex].Card.Id.Entry : "?";
        Check("New Leaf: Neow-hover == screen-hover", screenPredicted == neowPredicted && screenPredicted != "?", $"neow [{neowPredicted}] screen [{screenPredicted}] (uses {pr?.Title})");
        PredictionManager.OnHolderUnfocused(holder);
        AccessTools.Method(typeof(NDeckTransformSelectScreen), "OnCardClicked")?.Invoke(screen, new object[] { card });
        for (int i = 0; i < 30; i++) await NextFrame();
        AccessTools.Method(typeof(NDeckTransformSelectScreen), "CompleteSelection")?.Invoke(screen, new object?[] { null });
        if (!await WaitUntil(() => PileType.Deck.GetPile(me).Cards.Any(c => !deckBefore.Contains(c)), 30, "New Leaf transformed card")) { Check("New Leaf transform happened", false, "timeout"); return; }
        for (int i = 0; i < 60; i++) await NextFrame();
        try { await obtainTask; } catch (Exception ex) { PLog.Write($"SELFTEST: obtain task: {ex.Message}"); }
        var added = PileType.Deck.GetPile(me).Cards.Where(c => !deckBefore.Contains(c)).Select(c => c.Id.Entry).ToList();
        Check("New Leaf result", added.Count == 1 && added[0] == screenPredicted, $"predicted [{screenPredicted}] actual [{string.Join(",", added)}]");
    }

    private static async Task ConveyorTest(RunManager rm, IRunState rs)
    {
        var me = rs.Players[0];
        var eventModel = ModelDb.AllEvents.FirstOrDefault(e => e.Id.Entry == "ENDLESS_CONVEYOR");
        if (eventModel == null) { PLog.Write("SELFTEST: ENDLESS_CONVEYOR not found; skipping"); return; }
        // Make sure there are unupgraded cards to pick from even when an upgrade-everything mod is active
        // (AddInternal bypasses the add-to-deck hooks that the egg relics use).
        try
        {
            foreach (var c in new CardModel[] { rs.CreateCard<Whirlwind>(me), rs.CreateCard<Bludgeon>(me), rs.CreateCard<Armaments>(me) })
                me.Deck.AddInternal(c);
            PLog.Write($"SELFTEST: deck now {string.Join(",", me.Deck.Cards.Select(c => c.Id.Entry + (c.IsUpgraded ? "+" : "")))}");
        }
        catch (Exception ex) { PLog.Write($"SELFTEST: adding deck cards failed: {ex.Message}"); }
        PLog.Write("SELFTEST: entering ENDLESS_CONVEYOR");
        rs.AppendToMapPointHistory(MegaCrit.Sts2.Core.Map.MapPointType.Unknown, MegaCrit.Sts2.Core.Rooms.RoomType.Event, eventModel.Id);
        await rm.EnterRoom(new EventRoom(eventModel));
        if (!await WaitUntil(() => (NEventRoom.Instance?.Layout?.OptionButtons.Count() ?? 0) > 1, 30, "conveyor options")) return;
        for (int i = 0; i < 30; i++) await NextFrame();
        var buttons = NEventRoom.Instance!.Layout!.OptionButtons.ToList();
        int shot = 0;
        foreach (var btn in buttons)
        {
            PredictionManager.OnEventOptionFocused(btn);
            for (int i = 0; i < 6; i++) await NextFrame();
            PLog.Write($"SELFTEST conveyor option {btn.Option?.TextKey}: overlayShowing={PredictionManager.OverlayShowing}");
            if (PredictionManager.OverlayShowing) Shot($"00_conveyor_{shot++}");
            PredictionManager.OnEventOptionUnfocused(btn);
        }
        // Predict, then choose "Observe the chef" (random upgrade) and compare.
        var chefBtn = buttons.FirstOrDefault(b => (b.Option?.TextKey ?? "").EndsWith("OBSERVE_CHEF"));
        if (chefBtn == null) { Check("conveyor observe-chef option present", false, string.Join(",", buttons.Select(b => b.Option?.TextKey))); return; }
        var ev = AccessTools.Field(typeof(NEventRoom), "_event")?.GetValue(NEventRoom.Instance) as EventModel;
        var pr = ev != null ? Predictors.ForEventOption(ev, chefBtn.Option!.TextKey, me) : null;
        PLog.Write($"SELFTEST conveyor observe-chef prediction: {(pr == null ? "(none)" : Describe(pr))}");
        var levelsBefore = PileType.Deck.GetPile(me).Cards.ToDictionary(c => c, c => c.CurrentUpgradeLevel);
        int idx = buttons.IndexOf(chefBtn);
        rm.EventSynchronizer.ChooseLocalOption(idx);
        for (int i = 0; i < 60; i++) await NextFrame();
        var upgraded = PileType.Deck.GetPile(me).Cards.Where(c => levelsBefore.TryGetValue(c, out var lv) && c.CurrentUpgradeLevel > lv).ToList();
        var predictedCard = pr?.Cards.FirstOrDefault()?.Card;
        bool ok = predictedCard == null ? upgraded.Count == 0 : upgraded.Count == 1 && ReferenceEquals(upgraded[0], predictedCard);
        Check("Conveyor random upgrade", ok, $"predicted [{predictedCard?.Id.Entry ?? "none"}] actual [{string.Join(",", upgraded.Select(c => c.Id.Entry))}]");
        // Leave the (finished) event.
        for (int i = 0; i < 30; i++) await NextFrame();
        try { if ((NEventRoom.Instance?.Layout?.OptionButtons.Count() ?? 0) > 0) rm.EventSynchronizer.ChooseLocalOption(0); } catch (Exception ex) { PLog.Write($"SELFTEST: leaving conveyor: {ex.Message}"); }
        for (int i = 0; i < 60; i++) await NextFrame();
    }

    private static void LogPatches(Type type, string method)
    {
        try
        {
            var m = AccessTools.DeclaredMethod(type, method);
            if (m == null) { PLog.Write($"SELFTEST patches {type.Name}.{method}: not declared here"); return; }
            var info = Harmony.GetPatchInfo(m);
            if (info == null) { PLog.Write($"SELFTEST patches {type.Name}.{method}: none"); return; }
            PLog.Write($"SELFTEST patches {type.Name}.{method}: prefixes [{string.Join(",", info.Prefixes.Select(p => p.owner))}] postfixes [{string.Join(",", info.Postfixes.Select(p => p.owner))}] transpilers [{string.Join(",", info.Transpilers.Select(p => p.owner))}]");
        }
        catch (Exception ex) { PLog.Write($"SELFTEST patches {type.Name}.{method}: {ex.Message}"); }
    }

    private static void Collect(Node node, List<NGridCardHolder> into)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is NGridCardHolder h) into.Add(h);
            Collect(child, into);
        }
    }

    private static async Task<CardModel?> AddToHand<T>(IRunState rs, Player me) where T : CardModel
    {
        try
        {
            // Combat-scoped card, added the same way the game adds generated cards.
            var card = me.Creature.CombatState!.CreateCard<T>(me);
            await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Hand, me);
            for (int i = 0; i < 10; i++) await NextFrame();
            await WaitForIdlePlayPhase(10);
            bool canPlay = card.CanPlay(out UnplayableReason reason, out AbstractModel? preventer);
            PLog.Write($"SELFTEST: added {card.Id.Entry} to hand; pile={card.Pile?.Type}, canPlay={canPlay} ({reason}, {preventer?.Id.Entry})");
            return card;
        }
        catch (Exception ex)
        {
            PLog.Write($"SELFTEST: adding {typeof(T).Name} failed: {ex.Message}");
            return null;
        }
    }

    private static async Task Play(RunManager rm, CardModel card, Creature? target)
    {
        PLog.Write($"SELFTEST: playing {card.Id.Entry}");
        rm.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));
        for (int f = 0; f < 5; f++) await NextFrame();
        await WaitForIdlePlayPhase(30);
        for (int f = 0; f < 20; f++) await NextFrame();
    }

    private static async Task UsePotion(RunManager rm, PotionModel potion, Creature? target)
    {
        PLog.Write($"SELFTEST: using potion {potion.Id.Entry}");
        rm.ActionQueueSynchronizer.RequestEnqueue(new UsePotionAction(potion, target, isCombatInProgress: true));
        for (int f = 0; f < 5; f++) await NextFrame();
        await WaitForIdlePlayPhase(30);
        for (int f = 0; f < 20; f++) await NextFrame();
    }

    private static string Describe(Prediction pr)
    {
        return $"cards [{string.Join(",", pr.Cards.Select(c => c.Card.Id.Entry + (c.Label != "" ? "(" + c.Label + ")" : "")))}] lines [{string.Join(" | ", pr.Lines)}] targets [{string.Join(",", pr.Targets.Select(t => t.creature.Name + ":" + t.label))}]";
    }

    private static void Shot(string name)
    {
        try
        {
            var img = NGame.Instance!.GetViewport().GetTexture().GetImage();
            var dir = System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "rngpredictor_selftest");
            System.IO.Directory.CreateDirectory(dir);
            img.SavePng(System.IO.Path.Combine(dir, name + ".png"));
        }
        catch (Exception ex) { PLog.Write($"SELFTEST shot {name} failed: {ex.Message}"); }
    }

    internal static async Task<bool> WaitUntil(Func<bool> condition, double timeoutSec, string what)
    {
        var start = Time.GetTicksMsec();
        while (!condition())
        {
            if (Time.GetTicksMsec() - start > timeoutSec * 1000)
            {
                PLog.Write($"SELFTEST: timeout waiting for {what}");
                return false;
            }
            await NextFrame();
        }
        return true;
    }

    internal static async Task NextFrame()
    {
        var game = NGame.Instance!;
        await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    internal static async Task<bool> WaitForIdlePlayPhase(double timeoutSec)
    {
        var rm = RunManager.Instance;
        var cm = CombatManager.Instance;
        return await WaitUntil(() => !cm.IsInProgress
                                     || (rm.ActionQueueSet.IsEmpty && !rm.ActionExecutor.IsRunning
                                         && rm.ActionQueueSynchronizer.CombatState == ActionSynchronizerCombatState.PlayPhase
                                         && !cm.EndingPlayerTurnPhaseOne && !cm.EndingPlayerTurnPhaseTwo),
                               timeoutSec, "idle play phase");
    }
}

[HarmonyPatch(typeof(NMainMenu), "_Ready")]
internal static class Patch_NMainMenu_Ready
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        SelfTest.MaybeStart();
    }
}
