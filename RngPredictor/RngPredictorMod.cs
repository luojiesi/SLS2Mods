using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace RngPredictor;

/// <summary>File + console logger: &lt;Godot user data&gt;/logs/RngPredictor.log.</summary>
internal static class PLog
{
    private static readonly string LogPath = System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "RngPredictor.log");
    private static bool _cleared;

    internal static void Write(string msg)
    {
        try
        {
            if (!_cleared)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                System.IO.File.WriteAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] === new session ==={System.Environment.NewLine}");
                _cleared = true;
            }
            System.IO.File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{System.Environment.NewLine}");
            GD.Print($"[RngPredictor] {msg}");
        }
        catch { }
    }
}

/// <summary>
/// Random Number Predictor (随机数预测) for Slay the Spire 2: hover a card, potion, pile or event option and see
/// what its random effect will actually produce. Inspired by the STS1 mod 随机数预测大师 (RandomNumberPredictionMaster).
/// </summary>
[ModInitializer("Initialize")]
public static class RngPredictorMod
{
    public const string Version = "1.3.1";

    public static void Initialize()
    {
        PLog.Write($"RngPredictor {Version} initializing");
        var harmony = new Harmony("com.rngpredictor.sts2");
        harmony.PatchAll(typeof(RngPredictorMod).Assembly);
        PLog.Write("Harmony patches applied");
    }

    /// <summary>Small quiet toast at the top of the screen.</summary>
    public static void Toast(string text)
    {
        try
        {
            var game = NGame.Instance;
            if (game == null) return;
            var layer = new CanvasLayer { Layer = 110 };
            var label = new Label
            {
                Text = text,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            label.AddThemeFontSizeOverride("font_size", 28);
            label.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.95f));
            label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
            label.AddThemeConstantOverride("outline_size", 6);
            label.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
            label.OffsetTop = 40;
            label.OffsetLeft = -400;
            label.OffsetRight = 400;
            label.OffsetBottom = 90;
            layer.AddChild(label);
            game.AddChild(layer);
            var tween = label.CreateTween();
            tween.TweenInterval(1.0);
            tween.TweenProperty(label, "modulate:a", 0f, 0.5);
            tween.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(layer)) layer.QueueFree(); }));
        }
        catch (Exception ex)
        {
            PLog.Write($"Toast failed: {ex.Message}");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Harmony patches
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Any card holder gaining focus (hand, grid screens). Subclasses that skip base.OnFocus are not wanted.</summary>
[HarmonyPatch(typeof(NCardHolder), "OnFocus")]
internal static class Patch_NCardHolder_OnFocus
{
    [HarmonyPostfix]
    public static void Postfix(NCardHolder __instance)
    {
        try { PredictionManager.OnHolderFocused(__instance); }
        catch (Exception ex) { PLog.Write($"OnFocus hook failed: {ex}"); }
    }
}

[HarmonyPatch(typeof(NCardHolder), "OnUnfocus")]
internal static class Patch_NCardHolder_OnUnfocus
{
    [HarmonyPostfix]
    public static void Postfix(NCardHolder __instance)
    {
        try { PredictionManager.OnHolderUnfocused(__instance); }
        catch (Exception ex) { PLog.Write($"OnUnfocus hook failed: {ex}"); }
    }
}

// The base NCardHolder.OnFocus/OnUnfocus bodies are tiny, so once an override gets hot the JIT inlines the
// base call and the patch on the base method is bypassed. Patch the overrides too (handlers are idempotent).
[HarmonyPatch(typeof(NGridCardHolder), "OnFocus")]
internal static class Patch_NGridCardHolder_OnFocus
{
    [HarmonyPostfix]
    public static void Postfix(NGridCardHolder __instance)
    {
        try { PredictionManager.OnHolderFocused(__instance); }
        catch (Exception ex) { PLog.Write($"Grid OnFocus hook failed: {ex}"); }
    }
}

[HarmonyPatch(typeof(NHandCardHolder), "OnFocus")]
internal static class Patch_NHandCardHolder_OnFocus
{
    [HarmonyPostfix]
    public static void Postfix(NHandCardHolder __instance)
    {
        try { PredictionManager.OnHolderFocused(__instance); }
        catch (Exception ex) { PLog.Write($"Hand OnFocus hook failed: {ex}"); }
    }
}

[HarmonyPatch(typeof(NHandCardHolder), "OnUnfocus")]
internal static class Patch_NHandCardHolder_OnUnfocus
{
    [HarmonyPostfix]
    public static void Postfix(NHandCardHolder __instance)
    {
        try { PredictionManager.OnHolderUnfocused(__instance); }
        catch (Exception ex) { PLog.Write($"Hand OnUnfocus hook failed: {ex}"); }
    }
}

[HarmonyPatch(typeof(HoveredModelTracker), nameof(HoveredModelTracker.OnLocalPotionHovered))]
internal static class Patch_PotionHovered
{
    [HarmonyPostfix]
    public static void Postfix(PotionModel potionModel)
    {
        try { PredictionManager.OnPotionHovered(potionModel); }
        catch (Exception ex) { PLog.Write($"Potion hover hook failed: {ex}"); }
    }
}

[HarmonyPatch(typeof(HoveredModelTracker), nameof(HoveredModelTracker.OnLocalPotionUnhovered))]
internal static class Patch_PotionUnhovered
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        try { PredictionManager.OnPotionUnhovered(); }
        catch (Exception ex) { PLog.Write($"Potion unhover hook failed: {ex}"); }
    }
}

/// <summary>Draw / discard pile buttons in combat: shuffle-order prediction.</summary>
[HarmonyPatch(typeof(NCombatCardPile), "OnFocus")]
internal static class Patch_Pile_OnFocus
{
    [HarmonyPostfix]
    public static void Postfix(NCombatCardPile __instance)
    {
        try { PredictionManager.OnPileFocused(__instance); }
        catch (Exception ex) { PLog.Write($"Pile focus hook failed: {ex}"); }
    }
}

[HarmonyPatch(typeof(NCombatCardPile), "OnUnfocus")]
internal static class Patch_Pile_OnUnfocus
{
    [HarmonyPostfix]
    public static void Postfix(NCombatCardPile __instance)
    {
        try { PredictionManager.OnPileUnfocused(__instance); }
        catch (Exception ex) { PLog.Write($"Pile unfocus hook failed: {ex}"); }
    }
}

/// <summary>The "choose a card to transform" screen (events, relics): remember it so grid hovers can be predicted.</summary>
[HarmonyPatch(typeof(NDeckTransformSelectScreen), nameof(NDeckTransformSelectScreen.ShowScreen))]
internal static class Patch_TransformScreen_Show
{
    [HarmonyPostfix]
    public static void Postfix(NDeckTransformSelectScreen __result, IReadOnlyList<CardModel> cards, Func<CardModel, CardTransformation> cardToTransformation, CardSelectorPrefs prefs)
    {
        try { PredictionManager.OnTransformScreenShown(__result, cardToTransformation, prefs); }
        catch (Exception ex) { PLog.Write($"Transform screen hook failed: {ex}"); }
    }
}

/// <summary>Event option buttons (Neow etc.): relics that transform deck cards are predicted before choosing.</summary>
[HarmonyPatch(typeof(NEventOptionButton), "OnFocus")]
internal static class Patch_EventOption_OnFocus
{
    [HarmonyPostfix]
    public static void Postfix(NEventOptionButton __instance)
    {
        try { PredictionManager.OnEventOptionFocused(__instance); }
        catch (Exception ex) { PLog.Write($"Event option focus hook failed: {ex}"); }
    }
}

[HarmonyPatch(typeof(NEventOptionButton), "OnUnfocus")]
internal static class Patch_EventOption_OnUnfocus
{
    [HarmonyPostfix]
    public static void Postfix(NEventOptionButton __instance)
    {
        try { PredictionManager.OnEventOptionUnfocused(__instance); }
        catch (Exception ex) { PLog.Write($"Event option unfocus hook failed: {ex}"); }
    }
}

/// <summary>Rest-site option buttons: Dig shows the relic that will be found.</summary>
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton), "OnFocus")]
internal static class Patch_RestSiteButton_OnFocus
{
    [HarmonyPostfix]
    public static void Postfix(MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton __instance)
    {
        try { PredictionManager.OnRestSiteOptionFocused(__instance); }
        catch (Exception ex) { PLog.Write($"Rest site focus hook failed: {ex}"); }
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton), "OnUnfocus")]
internal static class Patch_RestSiteButton_OnUnfocus
{
    [HarmonyPostfix]
    public static void Postfix(MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton __instance)
    {
        try { PredictionManager.OnRestSiteOptionUnfocused(__instance); }
        catch (Exception ex) { PLog.Write($"Rest site unfocus hook failed: {ex}"); }
    }
}

/// <summary>New Leaf opens the transform screen and rolls with the Niche stream (not the event's).</summary>
[HarmonyPatch(typeof(NewLeaf), "AfterObtained")]
internal static class Patch_NewLeaf_AfterObtained
{
    [HarmonyPrefix]
    public static void Prefix(NewLeaf __instance)
    {
        try { PredictionManager.SetRelicTransformContext(__instance.Owner.RunState.Rng.Niche, upgraded: false, L.T("新叶", "New Leaf")); }
        catch (Exception ex) { PLog.Write($"New Leaf context failed: {ex.Message}"); }
    }

    [HarmonyPostfix]
    public static void Postfix(System.Threading.Tasks.Task __result)
    {
        __result.ContinueWith(_ => PredictionManager.ClearRelicTransformContext(), System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
    }
}

/// <summary>Astrolabe: same as New Leaf but three cards, results upgraded.</summary>
[HarmonyPatch(typeof(Astrolabe), "AfterObtained")]
internal static class Patch_Astrolabe_AfterObtained
{
    [HarmonyPrefix]
    public static void Prefix(Astrolabe __instance)
    {
        try { PredictionManager.SetRelicTransformContext(__instance.Owner.RunState.Rng.Niche, upgraded: true, L.T("星盘", "Astrolabe")); }
        catch (Exception ex) { PLog.Write($"Astrolabe context failed: {ex.Message}"); }
    }

    [HarmonyPostfix]
    public static void Postfix(System.Threading.Tasks.Task __result)
    {
        __result.ContinueWith(_ => PredictionManager.ClearRelicTransformContext(), System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
    }
}

/// <summary>F8 toggles the predictions.</summary>
[HarmonyPatch(typeof(NGame), "_Input")]
internal static class Patch_NGame_Input
{
    [HarmonyPrefix]
    public static void Prefix(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F8 })
        {
            try { PredictionManager.Toggle(); }
            catch (Exception ex) { PLog.Write($"Toggle failed: {ex}"); }
        }
    }
}
