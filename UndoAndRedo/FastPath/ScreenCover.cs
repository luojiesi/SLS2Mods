using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace UndoAndRedo.FastPath;

/// <summary>
/// Freeze-frame cover used by the fast path: a screenshot of the last presented frame on a high canvas layer,
/// so the visual rebuild underneath (new room node, UI slide-in tweens) is never seen. Independent of the
/// replay path's cover on purpose.
/// </summary>
internal static class ScreenCover
{
    private static CanvasLayer? _layer;

    public static bool IsShown => _layer != null && GodotObject.IsInstanceValid(_layer);

    public static async Task Show()
    {
        var game = NGame.Instance;
        if (game == null || IsShown) return;
        try
        {
            var img = game.GetViewport().GetTexture().GetImage();
            if (img == null || img.IsEmpty()) { Log.Write("fastpath cover: viewport image empty"); return; }
            var rect = new TextureRect
            {
                Texture = ImageTexture.CreateFromImage(img),
                MouseFilter = Control.MouseFilterEnum.Stop,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            };
            rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _layer = new CanvasLayer { Layer = 121 };
            _layer.AddChild(rect);
            game.AddChild(_layer);
            await NextFrame();
        }
        catch (Exception ex) { Log.Write($"fastpath cover failed: {ex.Message}"); }
    }

    public static async Task Hide()
    {
        if (!IsShown) return;
        await NextFrame();
        var layer = _layer;
        _layer = null;
        if (layer != null && GodotObject.IsInstanceValid(layer)) layer.QueueFree();
    }

    public static async Task NextFrame()
    {
        var game = NGame.Instance!;
        await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
