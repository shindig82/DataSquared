using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DataSquared.Core.Model;
using Godot;

namespace DataSquared;

/// <summary>
/// The node canvas. Each container (object, array, section) is a card; its scalar values are rows
/// edited in place, and its child containers hang off ports on the right.
/// </summary>
public partial class GraphCanvas : GraphEdit
{
    private const float CardWidth = 340;
    private const float ColumnGap = 90;
    private const float RowGap = 28;

    private readonly Dictionary<DocNode, GraphNode> _cards = new();
    private int _nextCardId;

    /// <summary>Raised after any value changes on a card.</summary>
    public event Action? Edited;

    public override void _Ready()
    {
        ShowGrid = true;
        MinimapEnabled = true;
        RightDisconnects = false;
        ShowArrangeButton = false;
        // Connections mirror the data's structure, so they can't be rewired by hand yet.
        ConnectionRequest += (_, _, _, _) => { };
        DeleteNodesRequest += _ => { };
    }

    public void ShowDocument(Document doc, string title, LayoutStore.Saved? saved)
    {
        Clear();
        BuildCard(doc.Root, title);
        AutoLayout();

        if (saved != null)
        {
            foreach (var (node, card) in _cards)
                if (saved.Cards.TryGetValue(node.Path, out var pos))
                    card.PositionOffset = pos;
        }

        // Scrolling only sticks once the new cards have been laid out, so wait a frame.
        var zoom = saved?.Zoom ?? 1;
        var scroll = saved?.Scroll ?? new Vector2(-40, -80);
        Callable.From(() =>
        {
            Zoom = zoom;
            ScrollOffset = scroll;
        }).CallDeferred();
    }

    public Dictionary<string, Vector2> CardPositions() =>
        _cards.ToDictionary(kv => kv.Key.Path, kv => kv.Value.PositionOffset);

    public int CardCount => _cards.Count;

    /// <summary>Lays cards out as a left-to-right tree: each depth is a column, children stacked beside their parent.</summary>
    public void AutoLayout()
    {
        var root = _cards.Keys.FirstOrDefault(n => n.Parent == null);
        if (root == null)
            return;

        var columnWidth = new Dictionary<int, float>();
        foreach (var (node, card) in _cards)
        {
            var depth = Depth(node);
            columnWidth[depth] = Math.Max(columnWidth.GetValueOrDefault(depth), card.Size.X);
        }

        var columnX = new Dictionary<int, float> { [0] = 0 };
        for (var d = 1; columnWidth.ContainsKey(d); d++)
            columnX[d] = columnX[d - 1] + columnWidth[d - 1] + ColumnGap;

        var nextY = 0f;
        Place(root, 0);

        void Place(DocNode node, int depth)
        {
            var card = _cards[node];
            var top = nextY;
            var children = node.Children.Where(c => c.IsContainer).ToList();
            foreach (var child in children)
                Place(child, depth + 1);

            // Line the parent up with its first child, and make sure the next sibling clears it.
            var y = children.Count > 0 ? _cards[children[0]].PositionOffset.Y : top;
            card.PositionOffset = new Vector2(columnX[depth], y);
            nextY = Math.Max(nextY, y + card.Size.Y + RowGap);
        }
    }

    private void Clear()
    {
        ClearConnections();
        foreach (var card in _cards.Values)
        {
            RemoveChild(card);
            card.QueueFree();
        }
        _cards.Clear();
    }

    private GraphNode BuildCard(DocNode node, string? title = null)
    {
        var card = new GraphNode
        {
            Name = $"card{_nextCardId++}",
            Title = title ?? CardTitle(node),
            CustomMinimumSize = new Vector2(CardWidth, 0),
            TooltipText = node.Comment,
            Resizable = false,
        };
        card.GetTitlebarHBox().AddChild(Badge(KindLabel(node), KindColor(node)));
        AddChild(card);
        _cards[node] = card;

        // Row 0 is the summary, and takes the incoming connection from the parent card.
        var summary = new Label { Text = Summary(node), Modulate = new Color(1, 1, 1, 0.55f) };
        card.AddChild(summary);
        card.SetSlot(0, node.Parent != null, 0, KindColor(node), false, 0, Colors.White);

        var rightPort = 0;
        foreach (var child in node.Children)
        {
            var row = child.IsContainer ? ContainerRow(child) : ValueRow(child);
            row.TooltipText = child.Comment;
            card.AddChild(row);

            if (!child.IsContainer)
                continue;

            card.SetSlot(row.GetIndex(), false, 0, Colors.White, true, 0, KindColor(child));
            var childCard = BuildCard(child);
            ConnectNode(card.Name, rightPort++, childCard.Name, 0);
        }

        card.Size = card.GetCombinedMinimumSize();
        return card;
    }

    private static Control ContainerRow(DocNode child)
    {
        var row = new HBoxContainer();
        row.AddChild(KeyLabel(child));
        row.AddChild(new Label
        {
            Text = $"{KindLabel(child)} · {Count(child)}",
            HorizontalAlignment = HorizontalAlignment.Right,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Modulate = KindColor(child),
        });
        return row;
    }

    private Control ValueRow(DocNode value)
    {
        var row = new HBoxContainer();
        row.AddChild(KeyLabel(value));

        switch (value.ValueKind)
        {
            case ValueKind.Boolean:
            {
                var check = new CheckBox
                {
                    ButtonPressed = value.Text.Trim() == "true",
                    Text = value.Text.Trim(),
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                };
                check.Toggled += on =>
                {
                    value.Text = on ? "true" : "false";
                    check.Text = value.Text;
                    Edited?.Invoke();
                };
                row.AddChild(check);
                break;
            }
            case ValueKind.Null:
                row.AddChild(new Label { Text = "null", SizeFlagsHorizontal = SizeFlags.ExpandFill, Modulate = KindColor(value) });
                break;
            default:
            {
                var edit = new LineEdit
                {
                    Text = value.Text,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    PlaceholderText = value.ValueKind == ValueKind.Number ? "number" : "empty",
                    SelectAllOnFocus = true,
                };
                edit.AddThemeColorOverride("font_color", KindColor(value));
                edit.TextChanged += text =>
                {
                    // A number that doesn't parse isn't applied, so a save never writes broken data.
                    var ok = value.ValueKind != ValueKind.Number
                        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                    edit.AddThemeColorOverride("font_color", ok ? KindColor(value) : new Color("ff5c6c"));
                    if (!ok)
                        return;
                    value.Text = text;
                    Edited?.Invoke();
                };
                row.AddChild(edit);
                break;
            }
        }

        return row;
    }

    private static Label KeyLabel(DocNode node) => new()
    {
        Text = node.Key ?? $"[{node.IndexInParent}]",
        CustomMinimumSize = new Vector2(120, 0),
        ClipText = true,
        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
    };

    private static Control Badge(string text, Color color)
    {
        var badge = new Label { Text = text.ToUpperInvariant(), Modulate = color };
        badge.AddThemeFontSizeOverride("font_size", 11);
        return badge;
    }

    private static string CardTitle(DocNode node) => node.Parent?.Kind == NodeKind.Array
        ? $"{node.Parent.Key ?? ""}[{node.IndexInParent}]"
        : node.Key ?? "";

    private static string Summary(DocNode node)
    {
        var path = node.Path;
        return $"{Count(node)}  ·  {path}";
    }

    private static string Count(DocNode node)
    {
        var n = node.Children.Count;
        return node.Kind switch
        {
            NodeKind.Array => n == 1 ? "1 item" : $"{n} items",
            _ => n == 1 ? "1 key" : $"{n} keys",
        };
    }

    private static string KindLabel(DocNode node) => node.Kind switch
    {
        NodeKind.Object => "obj",
        NodeKind.Array => "arr",
        NodeKind.Section => "section",
        _ => node.ValueKind.ToString().ToLowerInvariant(),
    };

    private static Color KindColor(DocNode node) => node.Kind switch
    {
        NodeKind.Object => new Color("9d8cff"),
        NodeKind.Section => new Color("9d8cff"),
        NodeKind.Array => new Color("5cb8ff"),
        _ => node.ValueKind switch
        {
            ValueKind.String => new Color("6fdc9a"),
            ValueKind.Number => new Color("f0b450"),
            ValueKind.Boolean => new Color("ff7eb6"),
            _ => new Color("9a9aa8"),
        },
    };

    private static int Depth(DocNode node)
    {
        var d = 0;
        for (var p = node.Parent; p != null; p = p.Parent)
            d++;
        return d;
    }
}
