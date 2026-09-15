using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Pooling;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace RngPredictor;

/// <summary>
/// The on-screen panel: a title, rows of real <see cref="NCard"/> nodes (pooled game card visuals, scaled down),
/// text lines, and floating markers over predicted enemy targets. Built only from Godot built-in node types plus
/// the game's own NCard, so nothing has to be registered as a script class.
/// </summary>
internal sealed class Overlay
{
    private const float PanelX = 24f;
    private const float PanelY = 140f;
    private const float Pad = 10f;

    private CanvasLayer? _layer;
    private Control? _root;
    private ColorRect? _bg;
    private readonly List<Node> _content = new();
    private readonly List<NCard> _cards = new();
    private readonly List<(Creature creature, Label label)> _markers = new();

    public bool IsShowing => _layer != null && _content.Count > 0;

    private bool EnsureNodes()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer)) return true;
        var game = NGame.Instance;
        if (game == null) return false;
        _layer = new CanvasLayer { Layer = 95, Name = "RngPredictorOverlay" };
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Name = "Root" };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _bg = new ColorRect { Color = new Color(0f, 0f, 0f, 0.62f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _root.AddChild(_bg);
        _layer.AddChild(_root);
        game.AddChild(_layer);
        return true;
    }

    public void Hide()
    {
        foreach (var (_, label) in _markers)
            if (GodotObject.IsInstanceValid(label)) label.QueueFree();
        _markers.Clear();
        foreach (var card in _cards)
        {
            try
            {
                if (!GodotObject.IsInstanceValid(card)) continue;
                card.GetParent()?.RemoveChild(card);
                NodePool.Free(card);
            }
            catch (System.Exception ex)
            {
                PLog.Write($"Freeing card node failed: {ex.Message}");
                try { if (GodotObject.IsInstanceValid(card)) card.QueueFree(); } catch { }
            }
        }
        _cards.Clear();
        foreach (var n in _content)
            if (GodotObject.IsInstanceValid(n)) n.QueueFree();
        _content.Clear();
        if (_bg != null && GodotObject.IsInstanceValid(_bg)) _bg.Visible = false;
    }

    public void Show(Prediction pr)
    {
        if (!EnsureNodes()) return;
        Hide();
        var root = _root!;
        float y = PanelY + Pad;
        float maxW = 0f;

        var title = MakeLabel(pr.Title, 22, new Color(1f, 0.9f, 0.55f));
        title.Position = new Vector2(PanelX + Pad, y);
        root.AddChild(title);
        _content.Add(title);
        maxW = Mathf.Max(maxW, title.GetMinimumSize().X);
        y += title.GetMinimumSize().Y + 6f;

        if (pr.Cards.Count > 0)
        {
            float s = pr.CardScale;
            var cardSize = NCard.defaultSize * s;
            float labelH = 22f;
            float gap = 8f;
            int perRow = System.Math.Max(1, pr.CardsPerRow);
            int col = 0;
            float rowY = y;
            for (int i = 0; i < pr.Cards.Count; i++)
            {
                var pc = pr.Cards[i];
                float x = PanelX + Pad + col * (cardSize.X + gap);
                // Scale the wrapper, not the card: NCard lays out its children for its unscaled 300x422 size
                // (this is also how the game's own card holders shrink cards).
                var wrapper = new Control
                {
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    Position = new Vector2(x, rowY),
                    Size = NCard.defaultSize,
                    Scale = new Vector2(s, s),
                };
                root.AddChild(wrapper);
                _content.Add(wrapper);
                var display = MakeDisplayCard(pc);
                var nCard = display != null ? NCard.Create(display) : null;
                if (nCard != null)
                {
                    nCard.MouseFilter = Control.MouseFilterEnum.Ignore;
                    nCard.Position = NCard.defaultSize / 2f; // NCard draws itself centred on its own origin
                    nCard.Scale = Vector2.One;
                    wrapper.AddChild(nCard);
                    try
                    {
                        nCard.UpdateVisuals(PileType.Deck, MegaCrit.Sts2.Core.Entities.Cards.CardPreviewMode.Normal);
                        if (!nCard.IsNodeReady())
                        {
                            // UpdateVisuals is a no-op before _Ready; redo it once the node is ready.
                            var captured = nCard;
                            Callable.From(() =>
                            {
                                try { if (GodotObject.IsInstanceValid(captured) && captured.IsInsideTree()) captured.UpdateVisuals(PileType.Deck, MegaCrit.Sts2.Core.Entities.Cards.CardPreviewMode.Normal); }
                                catch (System.Exception ex) { PLog.Write($"deferred UpdateVisuals failed: {ex.Message}"); }
                            }).CallDeferred();
                        }
                    }
                    catch (System.Exception ex) { PLog.Write($"UpdateVisuals failed: {ex.Message}"); }
                    _cards.Add(nCard);
                    if (i == 0 && Verify.Verbose)
                        PLog.Write($"Overlay: {pr.Cards.Count} cards; first {pc.Card.Id.Entry}: ready={nCard.IsNodeReady()} visible={nCard.Visible} inTree={nCard.IsInsideTree()} size={nCard.Size} scale={nCard.Scale} modulate={nCard.Modulate} children={nCard.GetChildCount()} parentVisible={wrapper.IsVisibleInTree()}");
                }
                else
                {
                    var fallback = MakeLabel(SafeTitle(pc.Card), 16, Colors.White);
                    fallback.Position = new Vector2(x, rowY + cardSize.Y / 2f);
                    root.AddChild(fallback);
                    _content.Add(fallback);
                }
                if (!string.IsNullOrEmpty(pc.Label))
                {
                    var lab = MakeLabel(pc.Label, 15, new Color(0.8f, 0.95f, 1f));
                    lab.Position = new Vector2(x, rowY + cardSize.Y + 2f);
                    lab.Size = new Vector2(cardSize.X, labelH);
                    lab.HorizontalAlignment = HorizontalAlignment.Center;
                    lab.ClipText = true;
                    root.AddChild(lab);
                    _content.Add(lab);
                }
                maxW = Mathf.Max(maxW, x - PanelX - Pad + cardSize.X);
                col++;
                if (col >= perRow && i < pr.Cards.Count - 1)
                {
                    col = 0;
                    rowY += cardSize.Y + labelH + gap;
                }
            }
            y = rowY + cardSize.Y + labelH + gap;
        }

        foreach (var line in pr.Lines)
        {
            var lab = MakeLabel(line, 18, Colors.White);
            lab.Position = new Vector2(PanelX + Pad, y);
            root.AddChild(lab);
            _content.Add(lab);
            var min = lab.GetMinimumSize();
            maxW = Mathf.Max(maxW, min.X);
            y += min.Y + 2f;
        }

        if (_bg != null)
        {
            _bg.Position = new Vector2(PanelX, PanelY);
            _bg.Size = new Vector2(maxW + Pad * 2f, y - PanelY + Pad);
            _bg.Visible = true;
            root.MoveChild(_bg, 0);
        }

        foreach (var (creature, text) in pr.Targets)
        {
            var lab = MakeLabel(text, 24, new Color(1f, 0.45f, 0.35f));
            root.AddChild(lab);
            _content.Add(lab);
            _markers.Add((creature, lab));
        }
        UpdateMarkers();
    }

    /// <summary>Keep target markers glued to the creatures (they move slightly while idle).</summary>
    public void UpdateMarkers()
    {
        if (_markers.Count == 0) return;
        var room = NCombatRoom.Instance;
        foreach (var (creature, label) in _markers)
        {
            if (!GodotObject.IsInstanceValid(label)) continue;
            try
            {
                var node = room?.GetCreatureNode(creature);
                var hitbox = node?.Hitbox;
                if (hitbox == null || !GodotObject.IsInstanceValid(hitbox))
                {
                    label.Visible = false;
                    continue;
                }
                var xf = hitbox.GetGlobalTransformWithCanvas();
                var size = hitbox.Size * xf.Scale;
                var min = label.GetMinimumSize();
                label.Visible = true;
                label.Position = new Vector2(xf.Origin.X + size.X / 2f - min.X / 2f, xf.Origin.Y - min.Y - 8f);
            }
            catch
            {
                label.Visible = false;
            }
        }
    }

    private static Label MakeLabel(string text, int size, Color color)
    {
        var label = new Label
        {
            Text = text,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.95f));
        label.AddThemeConstantOverride("outline_size", 5);
        return label;
    }

    private static string SafeTitle(CardModel c)
    {
        try { return c.Title; } catch { return c.Id.Entry; }
    }

    /// <summary>
    /// A detached mutable copy of the canonical card for display (the same thing the game's card hover tips do),
    /// upgraded to the requested level. Never the live card: NCard subscribes to its model.
    /// </summary>
    private static CardModel? MakeDisplayCard(PredCard pc)
    {
        try
        {
            var canonical = pc.Card.CanonicalInstance ?? pc.Card;
            var m = canonical.ToMutable();
            int guard = 0;
            while (guard++ < 5 && pc.UpgradeLevel > 0 && SafeLevel(m) < pc.UpgradeLevel && m.IsUpgradable)
            {
                m.UpgradeInternal();
                m.FinalizeUpgradeInternal();
            }
            return m;
        }
        catch (System.Exception ex)
        {
            PLog.Write($"MakeDisplayCard failed for {pc.Card.Id.Entry}: {ex.Message}");
            return null;
        }
    }

    private static int SafeLevel(CardModel c)
    {
        try { return c.CurrentUpgradeLevel; } catch { return 0; }
    }
}
