using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace RngPredictor;

/// <summary>
/// Tracks what the player is hovering (hand card, potion, deck card on the transform screen, draw/discard pile,
/// a Neow-style relic option), recomputes the matching prediction a few times per second while it stays hovered,
/// and drives the overlay.
/// </summary>
internal static class PredictionManager
{
    private enum SourceKind { None, Hand, Potion, Transform, Pile, EventOption, RestSite }

    public static bool Enabled = true;

    private static readonly Overlay _overlay = new();

    /// <summary>For the self-test: is the overlay currently showing something.</summary>
    public static bool OverlayShowing => _overlay.IsShowing;

    /// <summary>For the self-test: render an arbitrary prediction.</summary>
    public static void ShowForTest(Prediction pr) => _overlay.Show(pr);

    /// <summary>For the self-test: the last transform-selection screen shown.</summary>
    public static NDeckTransformSelectScreen? CurrentTransformScreen =>
        _transformScreen != null && GodotObject.IsInstanceValid(_transformScreen) ? _transformScreen : null;

    private static SourceKind _kind = SourceKind.None;
    private static GodotObject? _sourceNode;
    private static CardModel? _card;
    private static PotionModel? _potion;
    private static RelicModel? _relic;
    private static Player? _player;
    private static string _lastSig = "";
    private static ulong _lastComputeMs;
    private static bool _tickHooked;

    private static NDeckTransformSelectScreen? _transformScreen;
    private static int _transformMax = 1;
    private static Func<CardModel, CardTransformation>? _transformFunc;

    // A relic (New Leaf, Astrolabe) opened the transform screen: it uses the Niche stream, not the event's.
    private static Rng? _relicTransformRng;
    private static bool _relicTransformUpgraded;
    private static string _relicTransformLabel = "";

    private static readonly AccessTools.FieldRef<NEventRoom, EventModel>? EventField =
        SafeFieldRef<NEventRoom, EventModel>("_event");

    private static readonly AccessTools.FieldRef<NCombatCardPile, Player>? PileLocalPlayerField =
        SafeFieldRef<NCombatCardPile, Player>("_localPlayer");

    private static AccessTools.FieldRef<T, F>? SafeFieldRef<T, F>(string name) where T : class
    {
        try { return AccessTools.FieldRefAccess<T, F>(name); }
        catch (Exception ex) { PLog.Write($"Field {typeof(T).Name}.{name} not found: {ex.Message}"); return null; }
    }

    private const uint RecomputeIntervalMs = 250;

    // ───────────────────────────── hooks ─────────────────────────────

    public static void OnHolderFocused(NCardHolder holder)
    {
        if (!Enabled) return;
        var card = holder.CardModel;
        if (card == null) return;
        EnsureTick();

        if (holder is NHandCardHolder && CombatManager.Instance.IsInProgress && card.Pile?.Type == PileType.Hand)
        {
            Set(SourceKind.Hand, holder, card, null, null, card.Owner);
            return;
        }
        if (holder is NGridCardHolder && _transformScreen != null && GodotObject.IsInstanceValid(_transformScreen)
            && _transformScreen.IsInsideTree() && _transformScreen.IsAncestorOf(holder))
        {
            Set(SourceKind.Transform, holder, card, null, null, card.Owner);
            return;
        }
    }

    public static void OnHolderUnfocused(NCardHolder holder)
    {
        if (_sourceNode == holder) Clear();
    }

    public static void OnPotionHovered(PotionModel potion)
    {
        if (!Enabled) return;
        EnsureTick();
        Set(SourceKind.Potion, null, null, potion, null, potion.Owner);
    }

    public static void OnPotionUnhovered()
    {
        if (_kind == SourceKind.Potion) Clear();
    }

    public static void OnPileFocused(NCombatCardPile pile)
    {
        if (!Enabled) return;
        if (pile is not (NDiscardPileButton or NDrawPileButton)) return;
        if (!CombatManager.Instance.IsInProgress) return;
        var player = PlayerOfPile(pile);
        if (player == null) return;
        EnsureTick();
        Set(SourceKind.Pile, pile, null, null, null, player);
    }

    public static void OnPileUnfocused(NCombatCardPile pile)
    {
        if (_sourceNode == pile) Clear();
    }

    /// <summary>An event option (Neow etc.) that grants a relic with a random deck effect.</summary>
    public static void OnEventOptionFocused(NEventOptionButton button)
    {
        if (!Enabled) return;
        RelicModel? relic;
        try { relic = button.Option?.Relic; } catch { relic = null; }
        var player = EventOwner();
        if (player == null) return;
        if (relic != null && Predictors.IsPredictableNeowRelic(relic))
        {
            EnsureTick();
            _eventOptionKey = null;
            Set(SourceKind.EventOption, button, null, null, relic, player);
            return;
        }
        var ev = CurrentEvent();
        string? key = null;
        try { key = button.Option?.TextKey; } catch { }
        if (ev == null || key == null || !Predictors.HasEventOptionPredictor(ev, key)) return;
        EnsureTick();
        _eventOptionKey = key;
        _eventForOption = ev;
        Set(SourceKind.EventOption, button, null, null, null, player);
    }

    private static string? _eventOptionKey;
    private static EventModel? _eventForOption;
    private static MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption? _restOption;

    /// <summary>A rest-site option button (Dig: the next relic from the grab bag).</summary>
    public static void OnRestSiteOptionFocused(MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton button)
    {
        if (!Enabled) return;
        MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption? option;
        try { option = button.Option; } catch { option = null; }
        if (option == null || !Predictors.IsPredictableRestSiteOption(option)) return;
        Player? player = null;
        try { player = Traverse.Create(option).Property("Owner").GetValue<Player>(); } catch { }
        player ??= EventOwner();
        if (player == null) return;
        EnsureTick();
        _restOption = option;
        Set(SourceKind.RestSite, button, null, null, null, player);
    }

    public static void OnRestSiteOptionUnfocused(MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton button)
    {
        if (_sourceNode == button) Clear();
    }

    public static void OnEventOptionUnfocused(NEventOptionButton button)
    {
        if (_sourceNode == button) Clear();
    }

    public static void OnTransformScreenShown(NDeckTransformSelectScreen screen, Func<CardModel, CardTransformation> func, CardSelectorPrefs prefs)
    {
        _transformScreen = screen;
        _transformFunc = func;
        try { _transformMax = Math.Max(1, prefs.MaxSelect); } catch { _transformMax = 1; }
        PLog.Write($"Transform screen shown (max select {_transformMax}, relic context: {(_relicTransformRng != null ? _relicTransformLabel : "none")})");
    }

    public static void SetRelicTransformContext(Rng rng, bool upgraded, string label)
    {
        _relicTransformRng = rng;
        _relicTransformUpgraded = upgraded;
        _relicTransformLabel = label;
    }

    public static void ClearRelicTransformContext()
    {
        _relicTransformRng = null;
        _relicTransformUpgraded = false;
        _relicTransformLabel = "";
    }

    public static void Toggle()
    {
        Enabled = !Enabled;
        if (!Enabled) Clear();
        RngPredictorMod.Toast(Enabled ? L.T("随机数预测：开", "RNG prediction: ON") : L.T("随机数预测：关", "RNG prediction: OFF"));
    }

    // ───────────────────────────── state ─────────────────────────────

    private static void Set(SourceKind kind, GodotObject? node, CardModel? card, PotionModel? potion, RelicModel? relic, Player? player)
    {
        _kind = kind;
        _sourceNode = node;
        _card = card;
        _potion = potion;
        _relic = relic;
        _player = player;
        _lastSig = "";
        _lastComputeMs = 0;
        Recompute();
    }

    public static void Clear()
    {
        _kind = SourceKind.None;
        _sourceNode = null;
        _card = null;
        _potion = null;
        _relic = null;
        _eventOptionKey = null;
        _eventForOption = null;
        _restOption = null;
        _player = null;
        _lastSig = "";
        _overlay.Hide();
    }

    private static void EnsureTick()
    {
        if (_tickHooked) return;
        var game = NGame.Instance;
        if (game == null) return;
        try
        {
            game.GetTree().ProcessFrame += Tick;
            _tickHooked = true;
            PLog.Write("Per-frame tick hooked");
        }
        catch (Exception ex)
        {
            PLog.Write($"Failed to hook ProcessFrame: {ex.Message}");
        }
    }

    private static Player? PlayerOfPile(NCombatCardPile pile)
    {
        try { return PileLocalPlayerField != null ? PileLocalPlayerField(pile) : null; }
        catch (Exception ex) { PLog.Write($"Pile player lookup failed: {ex.Message}"); return null; }
    }

    private static EventModel? CurrentEvent()
    {
        var eventRoom = NEventRoom.Instance;
        if (eventRoom == null || EventField == null) return null;
        try { return EventField(eventRoom); } catch { return null; }
    }

    /// <summary>The player at the current event (its owner), else the first player of the run.</summary>
    public static Player? EventOwner()
    {
        try
        {
            var ev = CurrentEvent();
            if (ev != null)
            {
                var owner = Traverse.Create(ev).Property("Owner").GetValue<Player>();
                if (owner != null) return owner;
            }
        }
        catch (Exception ex) { PLog.Write($"Event owner lookup failed: {ex.Message}"); }
        try
        {
            var state = RunManager.Instance.DebugOnlyGetState();
            if (state != null && state.Players.Count > 0) return state.Players[0];
        }
        catch { }
        return null;
    }

    private static void Tick()
    {
        try
        {
            if (_kind == SourceKind.None) return;
            if (!Enabled || !StillValid())
            {
                Clear();
                return;
            }
            ulong now = Time.GetTicksMsec();
            if (now - _lastComputeMs >= RecomputeIntervalMs)
                Recompute();
            _overlay.UpdateMarkers();
        }
        catch (Exception ex)
        {
            PLog.Write($"Tick failed: {ex}");
            Clear();
        }
    }

    private static bool StillValid()
    {
        switch (_kind)
        {
            case SourceKind.Hand:
                return CombatManager.Instance.IsInProgress && NodeAlive(_sourceNode) && _card != null && _card.Pile?.Type == PileType.Hand;
            case SourceKind.Potion:
                if (_potion == null || _player == null) return false;
                try
                {
                    bool inSlots = _player.PotionSlots.Contains(_potion);
                    if (!inSlots) return false;
                    if (_potion.Usage == PotionUsage.CombatOnly && !CombatManager.Instance.IsInProgress) return false;
                    return true;
                }
                catch { return false; }
            case SourceKind.Transform:
                return NodeAlive(_sourceNode) && _transformScreen != null && GodotObject.IsInstanceValid(_transformScreen) && _transformScreen.IsInsideTree();
            case SourceKind.Pile:
                return CombatManager.Instance.IsInProgress && NodeAlive(_sourceNode);
            case SourceKind.EventOption:
                return NodeAlive(_sourceNode) && (_relic != null || _eventOptionKey != null) && _player != null;
            case SourceKind.RestSite:
                return NodeAlive(_sourceNode) && _restOption != null && _player != null;
        }
        return false;
    }

    private static bool NodeAlive(GodotObject? o)
    {
        return o is Node n && GodotObject.IsInstanceValid(n) && n.IsInsideTree();
    }

    private static void Recompute()
    {
        _lastComputeMs = Time.GetTicksMsec();
        Prediction? pr = null;
        try
        {
            switch (_kind)
            {
                case SourceKind.Hand:
                    if (_card != null) pr = Predictors.ForHandCard(_card);
                    break;
                case SourceKind.Potion:
                    if (_potion != null) pr = Predictors.ForPotion(_potion);
                    break;
                case SourceKind.Transform:
                    pr = TransformPrediction();
                    break;
                case SourceKind.Pile:
                    if (_player != null) pr = Predictors.ForShuffle(_player);
                    break;
                case SourceKind.EventOption:
                    if (_relic != null && _player != null) pr = Predictors.ForNeowRelic(_relic, _player);
                    else if (_eventOptionKey != null && _eventForOption != null && _player != null) pr = Predictors.ForEventOption(_eventForOption, _eventOptionKey, _player);
                    break;
                case SourceKind.RestSite:
                    if (_restOption != null && _player != null) pr = Predictors.ForRestSiteOption(_restOption, _player);
                    break;
            }
        }
        catch (Exception ex)
        {
            PLog.Write($"Prediction failed ({_kind}, {_card?.Id.Entry ?? _potion?.Id.Entry ?? _relic?.Id.Entry}): {ex}");
            pr = null;
        }

        if (pr == null)
        {
            if (_lastSig != "")
            {
                _lastSig = "";
                _overlay.Hide();
            }
            return;
        }
        string sig = pr.Signature();
        if (sig == _lastSig) return;
        _lastSig = sig;
        _overlay.Show(pr);
    }

    /// <summary>Prediction for a deck card on the transform-selection screen. Public so the self-test can compare.</summary>
    public static Prediction? TransformPrediction(CardModel? card = null)
    {
        card ??= _card;
        if (card == null || card.Owner == null) return null;
        // Screens with a fixed replacement (e.g. Claws → Maul) already preview it themselves.
        try
        {
            if (_transformFunc != null && _transformFunc(card).Replacement != null) return null;
        }
        catch { }

        Rng? rng;
        string source;
        bool upgraded = false;
        if (_relicTransformRng != null)
        {
            rng = _relicTransformRng;
            upgraded = _relicTransformUpgraded;
            source = _relicTransformLabel;
        }
        else
        {
            var ev = CurrentEvent();
            if (ev != null)
            {
                try { rng = ev.Rng; } catch { rng = null; }
                source = L.T("事件", "event");
            }
            else
            {
                rng = card.Owner.RunState.Rng.Niche;
                source = L.T("遗物", "relic");
            }
        }
        if (rng == null) return null;
        return Predictors.ForTransform(card, rng, _transformMax, source, upgraded);
    }
}
