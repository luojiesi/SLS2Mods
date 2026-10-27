using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.CardSelection;

namespace EmptyPileSelectFix;

/// <summary>
/// Automated check, only when &lt;user data&gt;/logs/EmptyPileSelectFix.selftest exists at the main menu: starts an
/// unsaved Ironclad run, jumps into a fight, then opens a combat-pile card-select screen directly on the (empty)
/// exhaust pile, which is exactly the state the race leaves behind. Without the fix _Ready throws and the screen
/// never completes; with it the selection completes with no card and the overlay closes. A second screen on the
/// (non-empty) draw pile checks that the normal path is untouched. Logs PASS/FAIL to logs/EmptyPileSelectFix.log.
/// </summary>
internal static class SelfTest
{
    private static string FlagPath => System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "EmptyPileSelectFix.selftest");
    private static bool _started;

    public static void MaybeStart()
    {
        if (_started || !System.IO.File.Exists(FlagPath)) return;
        _started = true;
        try { System.IO.File.Delete(FlagPath); } catch { }
        TaskHelper.RunSafely(Run());
    }

    private static async Task NextFrame()
    {
        var game = NGame.Instance!;
        await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static async Task<bool> WaitUntil(Func<bool> cond, int seconds, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            bool ok = false;
            try { ok = cond(); } catch { }
            if (ok) return true;
            await NextFrame();
        }
        Log.Write($"SELFTEST: timed out waiting for {what}");
        return false;
    }

    private static async Task Run()
    {
        bool pass = false;
        try
        {
            for (int i = 0; i < 60; i++) await NextFrame();
            Log.Write("SELFTEST: starting an unsaved Ironclad run");
            var rs = await NGame.Instance!.StartNewSingleplayerRun(
                ModelDb.Character<Ironclad>(), shouldSave: false, ActModel.GetDefaultList(),
                Array.Empty<ModifierModel>(), "FIXTEST", GameMode.Standard);
            for (int i = 0; i < 30; i++) await NextFrame();
            var rm = RunManager.Instance;
            var modelId = new ModelId(ModelId.SlugifyCategory<EncounterModel>(), "EXOSKELETONS_WEAK");
            var encounter = ModelDb.GetById<EncounterModel>(modelId).ToMutable();
            encounter.DebugRandomizeRng();
            await rm.EnterRoomDebug(RoomType.Monster, MapPointType.Unassigned, encounter);
            if (!await WaitUntil(() => CombatManager.Instance.IsInProgress, 30, "combat")) { Finish(false); return; }
            for (int i = 0; i < 90; i++) await NextFrame();
            var me = rm.DebugOnlyGetState()!.Players[0];

            // 1. Empty pile: this is the state the race leaves behind (prompt opens after the last card was drawn).
            var exhaust = PileType.Exhaust.GetPile(me);
            Log.Write($"SELFTEST: exhaust pile has {exhaust.Cards.Count} cards; opening a select screen on it");
            int handledBefore = PatchEmptyPileReady.EmptyPilesHandled;
            var screen = NCombatPileCardSelectScreen.Create(exhaust, new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, 1), _ => true);
            Task<System.Collections.Generic.IEnumerable<CardModel>> selected;
            NOverlayStack.Instance.Push(screen);
            selected = screen.CardsSelected();
            bool completed = await WaitUntil(() => selected.IsCompleted, 10, "empty-pile selection to complete");
            int count = completed && selected.IsCompletedSuccessfully ? selected.Result.Count() : -1;
            bool closed = await WaitUntil(() => !IsInstanceValidAndInTree(screen), 5, "empty-pile screen to close");
            bool emptyOk = completed && count == 0 && closed && PatchEmptyPileReady.EmptyPilesHandled == handledBefore + 1;
            Log.Write($"SELFTEST empty pile: completed={completed} selected={count} closed={closed} handled={PatchEmptyPileReady.EmptyPilesHandled - handledBefore} -> {(emptyOk ? "ok" : "FAIL")}");

            // 2. Normal pile: the draw pile has cards, the screen must show them and stay open until confirmed.
            // (Give the overlay stack time to finish the previous screen's fade-out and free it first.)
            for (int i = 0; i < 60; i++) await NextFrame();
            var draw = PileType.Draw.GetPile(me);
            var screen2 = NCombatPileCardSelectScreen.Create(draw, new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, 1), _ => true);
            NOverlayStack.Instance.Push(screen2);
            var selected2 = screen2.CardsSelected();
            for (int i = 0; i < 30; i++) await NextFrame();
            var grid = AccessTools.Field(typeof(NCardGridSelectionScreen), "_grid")?.GetValue(screen2) as MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid;
            int shown = 0;
            try { shown = grid?.CurrentlyDisplayedCards.Count() ?? 0; } catch { }
            bool normalOk = !selected2.IsCompleted && shown == draw.Cards.Count && draw.Cards.Count > 0;
            Log.Write($"SELFTEST normal pile: draw pile {draw.Cards.Count} cards, screen shows {shown}, still open={!selected2.IsCompleted} -> {(normalOk ? "ok" : "FAIL")}");
            try { AccessTools.Method(typeof(NCombatPileCardSelectScreen), "CompleteSelection")?.Invoke(screen2, null); } catch (Exception ex) { Log.Write($"SELFTEST: closing normal screen: {ex.Message}"); }
            for (int i = 0; i < 20; i++) await NextFrame();

            pass = emptyOk && normalOk;
        }
        catch (Exception ex)
        {
            Log.Write($"SELFTEST EXCEPTION: {ex}");
        }
        Finish(pass);
    }

    private static bool IsInstanceValidAndInTree(Node n)
    {
        try { return GodotObject.IsInstanceValid(n) && n.IsInsideTree(); } catch { return false; }
    }

    private static void Finish(bool pass)
    {
        Log.Write(pass ? "SELFTEST RESULT: PASS" : "SELFTEST RESULT: FAIL");
        TaskHelper.RunSafely(QuitSoon());
    }

    private static async Task QuitSoon()
    {
        for (int i = 0; i < 30; i++) await NextFrame();
        NGame.Instance?.GetTree().Quit();
    }
}

[HarmonyPatch(typeof(NMainMenu), "_Ready")]
internal static class SelfTestStart
{
    [HarmonyPostfix]
    public static void Postfix() => SelfTest.MaybeStart();
}
