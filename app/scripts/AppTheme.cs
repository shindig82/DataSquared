using Godot;

namespace DataSquared;

/// <summary>The dark indigo look from the reference design, built in code so it's easy to tweak in one place.</summary>
public static class AppTheme
{
    public static readonly Color Background = new("15122a");
    public static readonly Color Panel = new("1d1935");
    public static readonly Color Card = new("252041");
    public static readonly Color CardTitle = new("2e2852");
    public static readonly Color Border = new("3a3362");
    public static readonly Color Accent = new("7c6cf0");
    public static readonly Color Text = new("e4e1f5");
    public static readonly Color Field = new("1a1631");

    public static Theme Build()
    {
        var t = new Theme();

        t.SetStylebox("panel", "PanelContainer", Box(Panel));
        t.SetStylebox("panel", "Tree", Box(Panel));
        t.SetStylebox("panel", "PopupMenu", Box(Card, Border, 1, 6));
        t.SetStylebox("panel", "AcceptDialog", Box(Panel));
        t.SetStylebox("panel", "GraphEdit", Box(Background));
        t.SetColor("grid_major", "GraphEdit", new Color(1, 1, 1, 0.06f));
        t.SetColor("grid_minor", "GraphEdit", new Color(1, 1, 1, 0.025f));

        t.SetStylebox("panel", "GraphNode", Box(Card, Border, 1, 6, top: false, pad: 10));
        t.SetStylebox("panel_selected", "GraphNode", Box(Card, Accent, 2, 6, top: false, pad: 10));
        t.SetStylebox("titlebar", "GraphNode", Box(CardTitle, Border, 1, 6, bottom: false, pad: 8));
        t.SetStylebox("titlebar_selected", "GraphNode", Box(CardTitle, Accent, 2, 6, bottom: false, pad: 8));
        t.SetConstant("separation", "GraphNode", 4);

        foreach (var style in new[] { "normal", "read_only" })
        {
            t.SetStylebox(style, "LineEdit", Box(Field, null, 0, 4, pad: 6));
            t.SetStylebox(style, "TextEdit", Box(Field, null, 0, 0, pad: 6));
            t.SetStylebox(style, "CodeEdit", Box(Field, null, 0, 0, pad: 6));
        }
        t.SetStylebox("focus", "LineEdit", Box(Colors.Transparent, Accent, 1, 4));
        t.SetColor("background_color", "CodeEdit", Field);
        t.SetColor("current_line_color", "CodeEdit", new Color(1, 1, 1, 0.04f));

        t.SetStylebox("normal", "Button", Box(Colors.Transparent, null, 0, 4, pad: 8));
        t.SetStylebox("hover", "Button", Box(new Color(1, 1, 1, 0.07f), null, 0, 4, pad: 8));
        t.SetStylebox("pressed", "Button", Box(new Color(Accent, 0.35f), null, 0, 4, pad: 8));
        t.SetStylebox("disabled", "Button", Box(Colors.Transparent, null, 0, 4, pad: 8));
        t.SetStylebox("focus", "Button", new StyleBoxEmpty());

        t.SetStylebox("selected", "Tree", Box(new Color(Accent, 0.35f)));
        t.SetStylebox("selected_focus", "Tree", Box(new Color(Accent, 0.45f)));
        t.SetColor("font_color", "Label", Text);
        t.SetColor("font_color", "Tree", Text);
        t.SetColor("font_color", "Button", Text);
        t.SetColor("font_color", "LineEdit", Text);
        return t;
    }

    private static StyleBoxFlat Box(Color bg, Color? border = null, int width = 0, int radius = 0, bool top = true, bool bottom = true, int pad = 4)
    {
        var box = new StyleBoxFlat
        {
            BgColor = bg,
            ContentMarginLeft = pad,
            ContentMarginRight = pad,
            ContentMarginTop = pad / 2f,
            ContentMarginBottom = pad / 2f,
        };
        if (border is { } c)
        {
            box.BorderColor = c;
            box.BorderWidthLeft = box.BorderWidthRight = width;
            box.BorderWidthTop = top ? width : 0;
            box.BorderWidthBottom = bottom ? width : 0;
        }
        box.CornerRadiusTopLeft = box.CornerRadiusTopRight = top ? radius : 0;
        box.CornerRadiusBottomLeft = box.CornerRadiusBottomRight = bottom ? radius : 0;
        return box;
    }
}
