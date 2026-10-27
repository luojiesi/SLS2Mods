using System;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace EmptyPileSelectFix;

internal static class Log
{
    private static readonly string LogPath = System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "EmptyPileSelectFix.log");
    private static bool _cleared;

    internal static void Write(string msg)
    {
        try
        {
            if (!_cleared)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                System.IO.File.WriteAllText(LogPath, "");
                _cleared = true;
            }
            System.IO.File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{System.Environment.NewLine}");
            GD.Print($"[EmptyPileSelectFix] {msg}");
        }
        catch { }
    }
}

/// <summary>
/// Base-game soft-lock (observed on v0.107.1 with Stratagem): a combat hook asks for "choose a card from the draw
/// pile" right after a shuffle. CardSelectCmd.FromCombatPile checks that the pile is not empty, then the hook is
/// paused and its prompt is queued as a separate game action. Meanwhile the draw that caused the shuffle takes the
/// last card. When the queued prompt finally opens, NCombatPileCardSelectScreen._Ready calls
/// ConnectSignalsAndInitGrid, which does <c>_pile.Cards.First().Owner</c> on the now-empty pile and throws
/// "Sequence contains no elements". The rest of _Ready never runs, so the screen stays on its default "Info text."
/// prompt with no cards and no confirm, and the awaited selection never completes.
///
/// The fix replaces _Ready for an empty pile: the screen is initialised without touching a card, and the selection
/// is completed with no card one frame later. "One frame later" matters: _Ready runs inside NOverlayStack.Push
/// before the screen is registered in the stack, so completing (and thereby removing the screen) synchronously
/// would leave a freed screen in the stack and crash the next overlay.
/// </summary>
[ModInitializer("Initialize")]
public static class EmptyPileSelectFixMod
{
    public const string Version = "1.0.0";

    public static void Initialize()
    {
        Log.Write($"EmptyPileSelectFix {Version} initializing");
        var harmony = new Harmony("com.emptypileselectfix.sts2");
        harmony.PatchAll(typeof(EmptyPileSelectFixMod).Assembly);
        Log.Write("Harmony patches applied");
    }
}

[HarmonyPatch(typeof(NCombatPileCardSelectScreen), "_Ready")]
public static class PatchEmptyPileReady
{
    private static readonly AccessTools.FieldRef<NCombatPileCardSelectScreen, CardPile> PileRef =
        AccessTools.FieldRefAccess<NCombatPileCardSelectScreen, CardPile>("_pile");
    private static readonly System.Reflection.MethodInfo BaseInit =
        AccessTools.Method(typeof(NCardGridSelectionScreen), "ConnectSignalsAndInitGrid");
    private static readonly System.Reflection.MethodInfo CompleteSelection =
        AccessTools.Method(typeof(NCombatPileCardSelectScreen), "CompleteSelection");

    public static int EmptyPilesHandled;

    [HarmonyPrefix]
    public static bool Prefix(NCombatPileCardSelectScreen __instance)
    {
        CardPile pile;
        try { pile = PileRef(__instance); } catch { return true; }
        if (pile == null || pile.Cards.Count > 0) return true; // normal case: let the game do its thing

        try
        {
            // base.ConnectSignalsAndInitGrid(): empty grid + peek button. Non-virtual call, otherwise the override
            // with the crashing First() would run.
            AccessTools.MethodDelegate<Action>(BaseInit, __instance, virtualCall: false)();

            var combatPiles = __instance.GetNode<NCombatPilesContainer>("%CombatPiles");
            AccessTools.Field(typeof(NCombatPileCardSelectScreen), "_combatPiles").SetValue(__instance, combatPiles);
            Player? owner = OwnerOf(pile);
            if (CombatManager.Instance.IsInProgress && owner != null) combatPiles.Initialize(owner);
            combatPiles.Disable();
            combatPiles.SetVisible(visible: false);

            // The rest of what _Ready sets up, so nothing later dereferences a null node.
            var confirm = __instance.GetNode("%Confirm");
            var bottom = __instance.GetNode<Control>("%BottomText");
            var label = bottom.GetNode("%BottomLabel");
            AccessTools.Field(typeof(NCombatPileCardSelectScreen), "_confirmButton").SetValue(__instance, confirm);
            AccessTools.Field(typeof(NCombatPileCardSelectScreen), "_bottomTextContainer").SetValue(__instance, bottom);
            AccessTools.Field(typeof(NCombatPileCardSelectScreen), "_infoLabel").SetValue(__instance, label);
            try { Traverse.Create(confirm).Method("Disable").GetValue(); } catch { }

            EmptyPilesHandled++;
            Log.Write($"Empty {pile.Type} pile on a combat card-select screen: initialised without a card (owner {(owner == null ? "unknown" : "found")}); completing the prompt with no selection next frame");
            Callable.From(() =>
            {
                try
                {
                    if (!GodotObject.IsInstanceValid(__instance)) return;
                    CompleteSelection.Invoke(__instance, null);
                }
                catch (Exception ex) { Log.Write($"deferred completion failed: {ex}"); }
            }).CallDeferred();
            return false;
        }
        catch (Exception ex)
        {
            Log.Write($"empty-pile init failed, falling back to the game's code: {ex}");
            return true;
        }
    }

    /// <summary>The pile's player: CardPile does not know its owner, so find the player whose pile of that type is this instance.</summary>
    private static Player? OwnerOf(CardPile pile)
    {
        try
        {
            var players = MegaCrit.Sts2.Core.Runs.RunManager.Instance.DebugOnlyGetState()?.Players;
            if (players == null) return null;
            foreach (var p in players)
            {
                CardPile? candidate = null;
                try { candidate = CardPile.Get(pile.Type, p); } catch { }
                if (ReferenceEquals(candidate, pile)) return p;
            }
            return players.FirstOrDefault();
        }
        catch { return null; }
    }
}
