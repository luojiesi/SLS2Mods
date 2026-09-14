using System.Reflection;
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
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace UndoAndRedo;

/// <summary>
/// Automated end-to-end test. Enabled only when the file &lt;user data&gt;/logs/UndoAndRedo.selftest exists
/// when the main menu appears. It continues the saved run, plays a few turns using the same action path
/// the UI uses, then undoes and redoes everything while checking state checksums, logs PASS/FAIL, and
/// quits the game. Never runs during normal play.
/// </summary>
internal static class SelfTest
{
    private static string FlagPath => System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "UndoAndRedo.selftest");
    private static bool _started;

    private sealed class RandomCardSelector : ICardSelector
    {
        private readonly Random _rng = new(1234);

        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var list = options.ToList();
            int count = Math.Min(list.Count, Math.Max(minSelect, Math.Min(maxSelect, 1)));
            var picked = list.OrderBy(_ => _rng.Next()).Take(count).ToList();
            Log.Write($"SelfTest selector: chose {string.Join(",", picked.Select(c => c.Id.Entry))} of {list.Count} (min {minSelect} max {maxSelect})");
            return Task.FromResult<IEnumerable<CardModel>>(picked);
        }

        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
        {
            return new CardRewardSelection { card = options.Count > 0 ? options[0].Card : null };
        }
    }

    public static void MaybeStart()
    {
        if (_started) return;
        if (!System.IO.File.Exists(FlagPath)) return;
        _started = true;
        try { System.IO.File.Delete(FlagPath); } catch { }
        Log.Write("SELFTEST: flag found, starting");
        TaskHelper.RunSafely(Run());
    }

    private static async Task Run()
    {
        bool pass = false;
        try
        {
            pass = await RunInner();
        }
        catch (Exception ex)
        {
            Log.Write($"SELFTEST EXCEPTION: {ex}");
        }
        Log.Write(pass ? "SELFTEST RESULT: PASS" : "SELFTEST RESULT: FAIL");
        for (int i = 0; i < 30; i++) await RewindEngine.NextFrame();
        Log.Write("SELFTEST: quitting game");
        NGame.Instance?.GetTree().Quit();
    }

    private static async Task<bool> RunInner()
    {
        var game = NGame.Instance!;
        // 1. Continue the saved run through the main menu's own code path.
        if (!await RewindEngine.WaitUntil(() => game.MainMenu != null, 30, "main menu")) return false;
        var menu = game.MainMenu!;
        var readField = AccessTools.Field(typeof(NMainMenu), "_readRunSaveResult");
        await RewindEngine.WaitUntil(() => readField?.GetValue(menu) != null, 5, "run save read");
        var saveResult = readField?.GetValue(menu);
        var saveData = saveResult?.GetType().GetProperty("SaveData")?.GetValue(saveResult);
        for (int i = 0; i < 30; i++) await RewindEngine.NextFrame();
        if (saveData != null)
        {
            var continueMethod = AccessTools.Method(typeof(NMainMenu), "OnContinueButtonPressedAsync");
            if (continueMethod == null) { Log.Write("SELFTEST: OnContinueButtonPressedAsync not found"); return false; }
            Log.Write("SELFTEST: continuing saved run");
            await (Task)continueMethod.Invoke(menu, null)!;
        }
        else
        {
            string seed = "UNDOTEST";
            try { var f = System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "UndoAndRedo.selftest.seed"); if (System.IO.File.Exists(f)) seed = System.IO.File.ReadAllText(f).Trim(); } catch { }
            Log.Write($"SELFTEST: no saved run; starting an unsaved Ironclad run (seed {seed})");
            await game.StartNewSingleplayerRun(ModelDb.Character<Ironclad>(), shouldSave: false, ActModel.GetDefaultList(),
                                               Array.Empty<ModifierModel>(), seed, GameMode.Standard);
        }

        var rm = RunManager.Instance;
        var cm = CombatManager.Instance;
        if (!await RewindEngine.WaitUntil(() => rm.IsInProgress && rm.DebugOnlyGetState()?.CurrentRoom != null, 60, "run in progress")) return false;
        var rs = rm.DebugOnlyGetState()!;
        Log.Write($"SELFTEST: run loaded, room = {rs.CurrentRoom?.GetType().Name} ({rs.CurrentRoom?.RoomType})");
        if (rs.CurrentRoom is not CombatRoom)
        {
            // Travel to the next monster node through the same call the map screen uses.
            var current = rs.CurrentMapPoint;
            var children = current?.Children?.ToList() ?? new List<MegaCrit.Sts2.Core.Map.MapPoint>();
            var next = children.FirstOrDefault(c => c.PointType == MegaCrit.Sts2.Core.Map.MapPointType.Monster)
                       ?? children.FirstOrDefault(c => c.PointType == MegaCrit.Sts2.Core.Map.MapPointType.Elite)
                       ?? children.FirstOrDefault();
            if (next == null)
            {
                Log.Write("SELFTEST: no next map point to travel to; aborting");
                return false;
            }
            Log.Write($"SELFTEST: not in combat; traveling to ({next.coord.col},{next.coord.row}) type {next.PointType}");
            for (int i = 0; i < 30; i++) await RewindEngine.NextFrame();
            await rm.EnterMapCoord(next.coord);
            rs = rm.DebugOnlyGetState()!;
            Log.Write($"SELFTEST: arrived, room = {rs.CurrentRoom?.GetType().Name} ({rs.CurrentRoom?.RoomType})");
            if (rs.CurrentRoom is not CombatRoom)
            {
                Log.Write("SELFTEST: still not a combat; aborting");
                return false;
            }
        }
        if (!await RewindEngine.WaitUntil(() => cm.IsInProgress && rm.ActionQueueSynchronizer.CombatState == ActionSynchronizerCombatState.PlayPhase, 60, "combat play phase")) return false;
        await RewindEngine.WaitForIdlePlayPhase(30);
        for (int i = 0; i < 60; i++) await RewindEngine.NextFrame();

        using var selectorScope = CardSelectCmd.UseSelector(new RandomCardSelector());
        var me = rs.Players[0];
        var rec = ReplayRecorder.Current!;
        Log.Write($"SELFTEST: combat started, hand = {string.Join(",", me.PlayerCombatState!.Hand.Cards.Select(c => c.Id.Entry))}");

        // 2. Play a few turns via the same action path the UI uses. A Block Potion is given first and used as
        //    the very first decision, so potion undo (belt visuals) is covered.
        int actionsTaken = 0;
        try
        {
            var procure = await PotionCmd.TryToProcure<BlockPotion>(me);
            for (int f = 0; f < 30; f++) await RewindEngine.NextFrame();
            var potion = procure.success ? procure.potion : me.Potions.FirstOrDefault();
            if (potion != null && cm.IsInProgress)
            {
                Log.Write($"SELFTEST: using potion {potion.Id.Entry} (slot {me.GetPotionSlotIndex(potion)})");
                rm.ActionQueueSynchronizer.RequestEnqueue(new UsePotionAction(potion, null, isCombatInProgress: true));
                actionsTaken++;
                for (int f = 0; f < 5; f++) await RewindEngine.NextFrame();
                if (!await RewindEngine.WaitForIdlePlayPhase(30)) { Log.Write("SELFTEST: potion use did not settle"); return false; }
                for (int f = 0; f < 30; f++) await RewindEngine.NextFrame();
                Log.Write($"SELFTEST: after potion: block {me.Creature.Block}, potions {me.Potions.Count()}");
            }
            else Log.Write($"SELFTEST: no potion to use (procure success={procure.success})");
        }
        catch (Exception ex) { Log.Write($"SELFTEST: potion step failed: {ex.Message}"); }
        int turnsToPlay = 8;
        try { var f = System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "UndoAndRedo.selftest.turns"); if (System.IO.File.Exists(f)) turnsToPlay = int.Parse(System.IO.File.ReadAllText(f).Trim()); } catch { }
        for (int turn = 0; turn < turnsToPlay && cm.IsInProgress; turn++)
        {
            for (int i = 0; i < 3 && cm.IsInProgress; i++)
            {
                var hand = me.PlayerCombatState!.Hand.Cards.ToList();
                var playable = hand.Where(c => c.CanPlay(out UnplayableReason _, out AbstractModel? _))
                    .OrderBy(c => c.TargetType == TargetType.AnyEnemy ? 1 : 0) // prefer non-attacks so long fights do not end early
                    .ToList();
                if (playable.Count == 0) break;
                var card = playable[i % playable.Count];
                Creature? target = null;
                if (card.TargetType == TargetType.AnyEnemy)
                    target = me.Creature.CombatState?.HittableEnemies.FirstOrDefault();
                if (card.TargetType == TargetType.AnyEnemy && target == null) continue;
                Log.Write($"SELFTEST: turn {turn + 1} playing {card.Id.Entry} -> {target?.Name ?? "none"} (energy {me.PlayerCombatState.Energy})");
                rm.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));
                actionsTaken++;
                for (int f = 0; f < 5; f++) await RewindEngine.NextFrame();
                if (!await RewindEngine.WaitForIdlePlayPhase(30)) { Log.Write("SELFTEST: card play did not settle"); return false; }
            }
            if (!cm.IsInProgress) break;
            int turnBefore = me.PlayerCombatState!.TurnNumber;
            Log.Write($"SELFTEST: ending turn {turnBefore} (round {cm.DebugOnlyGetState()?.RoundNumber})");
            rm.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(me, turnBefore));
            actionsTaken++;
            if (!await RewindEngine.WaitUntil(() => !cm.IsInProgress || (me.PlayerCombatState!.TurnNumber > turnBefore
                                                    && rm.ActionQueueSynchronizer.CombatState == ActionSynchronizerCombatState.PlayPhase
                                                    && rm.ActionQueueSet.IsEmpty && !rm.ActionExecutor.IsRunning), 90, "next turn"))
            { Log.Write("SELFTEST: end turn did not complete"); return false; }
            for (int f = 0; f < 30; f++) await RewindEngine.NextFrame();
        }
        if (!cm.IsInProgress)
        {
            Log.Write("SELFTEST: combat ended before the undo phase; nothing to test");
            return false;
        }

        var replay = rec.Replay!;
        int depth = ReplayRecorder.Boundaries(replay.events).Count;
        uint? before = rec.CurrentChecksum();
        Log.Write($"SELFTEST: {actionsTaken} actions taken, {replay.events.Count} events recorded, undo depth {depth}, checksum {before}, hp {me.Creature.CurrentHp}, round {cm.DebugOnlyGetState()?.RoundNumber}");

        // 3. Undo everything, one step at a time (the first undo is the most expensive: it replays every earlier turn).
        Shot("before_undo");
        bool visualsOk = VisualCheck("before undo");
        int undone = 0;
        for (int i = 0; i < depth; i++)
        {
            for (int f = 0; f < 10; f++) await RewindEngine.NextFrame();
            bool ok = await RewindEngine.UndoAsync();
            Log.Write($"SELFTEST: undo #{i + 1} -> {(ok ? "ok" : "FAILED")}; hp {me2()?.Creature.CurrentHp} round {cm.DebugOnlyGetState()?.RoundNumber} events {ReplayRecorder.Current?.Replay?.events.Count}");
            if (!ok) return false;
            undone++;
            for (int f = 0; f < 5; f++) await RewindEngine.NextFrame();
            Shot($"undo_{i + 1}");
            visualsOk &= VisualCheck($"after undo #{i + 1}");
        }

        // 4. Redo everything.
        int redone = 0;
        for (int i = 0; i < undone; i++)
        {
            for (int f = 0; f < 10; f++) await RewindEngine.NextFrame();
            bool ok = await RewindEngine.RedoAsync();
            Log.Write($"SELFTEST: redo #{i + 1} -> {(ok ? "ok" : "FAILED")}; hp {me2()?.Creature.CurrentHp} round {cm.DebugOnlyGetState()?.RoundNumber} events {ReplayRecorder.Current?.Replay?.events.Count}");
            if (!ok) break;
            redone++;
            for (int f = 0; f < 5; f++) await RewindEngine.NextFrame();
            Shot($"redo_{i + 1}");
            visualsOk &= VisualCheck($"after redo #{i + 1}");
        }

        uint? after = ReplayRecorder.Current?.CurrentChecksum();
        Log.Write($"SELFTEST: undone {undone}, redone {redone}, checksum before {before} after {after}");

        // 5. One more undo after live play resumed (after a replay-based redo this exercises the snapshots
        //    taken while replaying), then play one card live and undo that too.
        bool finalOk = await RewindEngine.UndoAsync();
        Log.Write($"SELFTEST: final undo -> {(finalOk ? "ok" : "FAILED")}");
        for (int f = 0; f < 5; f++) await RewindEngine.NextFrame();
        Shot("final_undo");
        visualsOk &= VisualCheck("after final undo");
        bool liveOk = false;
        if (finalOk && await RewindEngine.WaitForIdlePlayPhase(30))
        {
            var meNow = me2()!;
            CardModel? Playable() => meNow.PlayerCombatState!.Hand.Cards.FirstOrDefault(c => c.CanPlay(out UnplayableReason _, out AbstractModel? _) && c.TargetType != TargetType.AnyEnemy)
                                     ?? meNow.PlayerCombatState.Hand.Cards.FirstOrDefault(c => c.CanPlay(out UnplayableReason _, out AbstractModel? _));
            var card = Playable();
            if (card == null)
            {
                Log.Write("SELFTEST: nothing playable here; undoing one more step for the live-play check");
                if (await RewindEngine.UndoAsync() && await RewindEngine.WaitForIdlePlayPhase(30)) { meNow = me2()!; card = Playable(); }
            }
            if (card != null)
            {
                Creature? target = card.TargetType == TargetType.AnyEnemy ? meNow.Creature.CombatState?.HittableEnemies.FirstOrDefault() : null;
                uint? sumBeforePlay = ReplayRecorder.Current?.CurrentChecksum();
                Log.Write($"SELFTEST: live play after undo: {card.Id.Entry} -> {target?.Name ?? "none"}");
                rm.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));
                for (int f = 0; f < 5; f++) await RewindEngine.NextFrame();
                if (await RewindEngine.WaitForIdlePlayPhase(30))
                {
                    for (int f = 0; f < 10; f++) await RewindEngine.NextFrame();
                    Shot("live_play");
                    bool ok2 = await RewindEngine.UndoAsync();
                    uint? sumAfterUndo = ReplayRecorder.Current?.CurrentChecksum();
                    liveOk = ok2 && sumBeforePlay.HasValue && sumBeforePlay == sumAfterUndo;
                    Log.Write($"SELFTEST: undo of live play -> {(ok2 ? "ok" : "FAILED")}; checksum {sumBeforePlay} -> {sumAfterUndo} ({(liveOk ? "match" : "MISMATCH")})");
                    for (int f = 0; f < 5; f++) await RewindEngine.NextFrame();
                    Shot("live_play_undone");
                    visualsOk &= VisualCheck("after undo of live play");
                }
            }
            else
            {
                Log.Write("SELFTEST: no playable card for the live-play check; skipping");
                liveOk = true;
            }
        }
        Log.Write($"SELFTEST: visuals {(visualsOk ? "consistent" : "INCONSISTENT")}");

        return undone == depth && redone == undone && before.HasValue && before == after && finalOk && liveOk && visualsOk;

        static Player? me2() => RunManager.Instance.DebugOnlyGetState()?.Players[0];
    }

    private static void Shot(string name)
    {
        try
        {
            var img = NGame.Instance!.GetViewport().GetTexture().GetImage();
            var dir = System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "selftest");
            System.IO.Directory.CreateDirectory(dir);
            img.SavePng(System.IO.Path.Combine(dir, name + ".png"));
        }
        catch (Exception ex) { Log.Write($"SELFTEST shot {name} failed: {ex.Message}"); }
    }

    /// <summary>Compares what is on screen with the model: hand cards (same models, same order), living creatures, potions.</summary>
    private static bool VisualCheck(string when)
    {
        try
        {
            var rs = RunManager.Instance.DebugOnlyGetState()!;
            var cs = CombatManager.Instance.DebugOnlyGetState()!;
            var me = rs.Players[0];
            var room = MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance;
            var hand = MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand.Instance;
            int modelHand = me.PlayerCombatState!.Hand.Cards.Count;
            int shownHand = hand?.ActiveHolders.Count ?? -1;
            var shownCards = hand?.ActiveHolders.Select(h => h.CardNode.Model).ToList() ?? new List<CardModel>();
            bool sameCards = shownCards.Count == modelHand && shownCards.Zip(me.PlayerCombatState.Hand.Cards).All(p => ReferenceEquals(p.First, p.Second));
            int modelAlive = cs.Creatures.Count(c => !c.IsDead);
            int shownAlive = room?.CreatureNodes.Count(n => !n.Entity.IsDead) ?? -1;
            var potions = NRun.Instance?.GlobalUi.TopBar.PotionContainer;
            var holdersField = AccessTools.Field(typeof(MegaCrit.Sts2.Core.Nodes.Potions.NPotionContainer), "_holders");
            var holders = holdersField?.GetValue(potions) as System.Collections.IList;
            int shownPotions = holders?.Cast<MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder>().Count(h => h.Potion != null) ?? -1;
            int modelPotions = me.Potions.Count();
            bool ok = sameCards && modelAlive == shownAlive && modelPotions == shownPotions;
            Log.Write($"SELFTEST VISUAL ({when}): hand model={modelHand} shown={shownHand} sameCards={sameCards}; alive model={modelAlive} shown={shownAlive}; potions model={modelPotions} shown={shownPotions}; energy {me.PlayerCombatState.Energy}/{me.PlayerCombatState.MaxEnergy} -> {(ok ? "OK" : "MISMATCH")}");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Write($"SELFTEST VISUAL ({when}) error: {ex.Message}");
            return false;
        }
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
