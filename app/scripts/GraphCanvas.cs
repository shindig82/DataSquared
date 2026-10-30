using System;
using System.Collections.Generic;
using System.Linq;
using DataSquared.Core.Editing;
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

    private static readonly (string Label, NodeKind Kind, ValueKind ValueKind)[] Types =
    {
        ("String", NodeKind.Value, ValueKind.String),
        ("Number", NodeKind.Value, ValueKind.Number),
        ("Boolean", NodeKind.Value, ValueKind.Boolean),
        ("Null", NodeKind.Value, ValueKind.Null),
        ("Object", NodeKind.Object, ValueKind.None),
        ("Array", NodeKind.Array, ValueKind.None),
        ("Section", NodeKind.Section, ValueKind.None),
    };

    private enum MenuId
    {
        Rename = 1,
        Duplicate,
        MoveUp,
        MoveDown,
        Delete,
        Add = 100,
        ChangeType = 200,
    }

    private readonly Dictionary<DocNode, GraphNode> _cards = new();
    private readonly Dictionary<long, DocNode> _ids = new();
    private readonly Dictionary<GraphNode, string> _builtPaths = new();
    private int _nextCardId;
    private string _title = "";

    private PopupMenu _menu = null!;
    private PopupMenu _addMenu = null!;
    private PopupMenu _typeMenu = null!;
    private DocNode? _menuTarget;

    private ConfirmationDialog _renameDialog = null!;
    private LineEdit _renameEdit = null!;
    private Label _renameError = null!;
    private DocNode? _renaming;

    /// <summary>Every change made on a card goes through this, so it can be undone.</summary>
    public DocumentEditor? Editor { get; set; }

    /// <summary>Raised with a message when an edit is refused, such as a duplicate name.</summary>
    public event Action<string>? Problem;

    /// <summary>Raised when the user selects a card.</summary>
    public event Action<DocNode>? CardSelected;

    public override void _Ready()
    {
        ShowGrid = true;
        MinimapEnabled = true;
        RightDisconnects = false;
        ShowArrangeButton = false;
        // Connections mirror the data's structure, so they're changed by moving rows, not rewiring.
        ConnectionRequest += (_, _, _, _) => { };
        DeleteNodesRequest += names =>
        {
            foreach (var name in names)
                if (_cards.FirstOrDefault(kv => kv.Value.Name == name).Key is { Parent: not null } node)
                    Try(() => Editor?.Remove(node));
            Refresh();
        };

        NodeSelected += selected =>
        {
            if (_cards.FirstOrDefault(kv => kv.Value == selected).Key is { } node)
                CardSelected?.Invoke(node);
        };

        _menu = new PopupMenu();
        _addMenu = new PopupMenu();
        _typeMenu = new PopupMenu();
        _menu.IdPressed += id => OnMenu((MenuId)id);
        _addMenu.IdPressed += id => OnMenu((MenuId)id);
        _typeMenu.IdPressed += id => OnMenu((MenuId)id);
        AddChild(_menu);

        _renameEdit = new LineEdit { CustomMinimumSize = new Vector2(320, 0), SelectAllOnFocus = true };
        _renameError = new Label { Modulate = Palette.Error };
        var box = new VBoxContainer();
        box.AddChild(_renameEdit);
        box.AddChild(_renameError);
        _renameDialog = new ConfirmationDialog { Title = "Rename" };
        _renameDialog.AddChild(box);
        _renameDialog.RegisterTextEnter(_renameEdit);
        _renameEdit.TextChanged += ValidateRename;
        _renameDialog.Confirmed += () =>
        {
            if (_renaming is { } node)
                Try(() => Editor?.Rename(node, _renameEdit.Text));
            Refresh();
        };
        AddChild(_renameDialog);
    }

    /// <summary>Selects the card showing <paramref name="node"/> (or holding it, for a value) and scrolls it into the middle.</summary>
    public void FocusNode(DocNode node)
    {
        var holder = node;
        while (!_cards.ContainsKey(holder) && holder.Parent != null)
            holder = holder.Parent;
        if (!_cards.TryGetValue(holder, out var card))
            return;

        foreach (var other in _cards.Values)
            other.Selected = other == card;
        ScrollOffset = card.PositionOffset * Zoom - (Size - card.Size * Zoom) / 2;
    }

    public DocNode? NodeById(long id) => _ids.GetValueOrDefault(id);

    public bool CanMove(DocNode node, DocNode parent, int index) =>
        Editor != null && node.Parent != null && node != parent && !node.IsAncestorOf(parent)
        && Editor.CannotHold(parent, node.Kind, node.ValueKind) == null;

    public void Move(DocNode node, DocNode parent, int index)
    {
        Try(() => Editor?.Move(node, parent, index));
        Refresh();
    }

    public void OpenMenu(DocNode target)
    {
        if (Editor == null)
            return;
        _menuTarget = target;
        _menu.Clear();
        _addMenu.Clear();
        _typeMenu.Clear();
        if (_addMenu.GetParent() != null) _menu.RemoveChild(_addMenu);
        if (_typeMenu.GetParent() != null) _menu.RemoveChild(_typeMenu);

        if (target.IsContainer)
        {
            for (var i = 0; i < Types.Length; i++)
                if (Editor.CannotHold(target, Types[i].Kind, Types[i].ValueKind) == null)
                    _addMenu.AddItem(Types[i].Label, (int)MenuId.Add + i);
            if (_addMenu.ItemCount > 0)
                _menu.AddSubmenuNodeItem("Add", _addMenu);
        }

        if (target.Parent is { } parent)
        {
            if (_menu.ItemCount > 0)
                _menu.AddSeparator();
            if (parent.Kind != NodeKind.Array)
                _menu.AddItem("Rename…", (int)MenuId.Rename);

            if (target.Kind != NodeKind.Section)
            {
                for (var i = 0; i < Types.Length; i++)
                {
                    var (label, kind, valueKind) = Types[i];
                    if (kind == NodeKind.Section || Editor.CannotHold(parent, kind, valueKind) != null)
                        continue;
                    _typeMenu.AddRadioCheckItem(label, (int)MenuId.ChangeType + i);
                    _typeMenu.SetItemChecked(_typeMenu.ItemCount - 1, kind == target.Kind && valueKind == target.ValueKind);
                }
                _menu.AddSubmenuNodeItem("Change type", _typeMenu);
            }

            _menu.AddItem("Duplicate", (int)MenuId.Duplicate);
            _menu.AddItem("Move up", (int)MenuId.MoveUp);
            _menu.SetItemDisabled(_menu.ItemCount - 1, target.IndexInParent == 0);
            _menu.AddItem("Move down", (int)MenuId.MoveDown);
            _menu.SetItemDisabled(_menu.ItemCount - 1, target.IndexInParent == parent.Children.Count - 1);
            _menu.AddSeparator();
            _menu.AddItem("Delete", (int)MenuId.Delete);
        }

        if (_menu.ItemCount == 0)
            return;
        _menu.Position = DisplayServer.MouseGetPosition();
        _menu.ResetSize();
        _menu.Popup();
    }

    private void OnMenu(MenuId id)
    {
        if (Editor == null || _menuTarget is not { } node)
            return;

        switch (id)
        {
            case MenuId.Rename:
                StartRename(node);
                return;
            case MenuId.Duplicate:
                Try(() => Editor.Duplicate(node));
                break;
            case MenuId.MoveUp:
                Try(() => Editor.Move(node, node.Parent!, node.IndexInParent - 1));
                break;
            case MenuId.MoveDown:
                Try(() => Editor.Move(node, node.Parent!, node.IndexInParent + 1));
                break;
            case MenuId.Delete:
                Try(() => Editor.Remove(node));
                break;
            case >= MenuId.ChangeType:
            {
                var (_, kind, valueKind) = Types[id - MenuId.ChangeType];
                Try(() => Editor.ChangeType(node, kind, valueKind));
                break;
            }
            case >= MenuId.Add:
            {
                var (_, kind, valueKind) = Types[id - MenuId.Add];
                DocNode? added = null;
                Try(() => added = Editor.Add(node, kind, valueKind));
                Refresh();
                // A new key starts with a placeholder name, so ask for a real one straight away.
                if (added?.Key != null)
                    StartRename(added);
                return;
            }
        }
        Refresh();
    }

    private void StartRename(DocNode node)
    {
        _renaming = node;
        _renameDialog.Title = $"Rename {node.Key}";
        _renameEdit.Text = node.Key ?? "";
        ValidateRename(_renameEdit.Text);
        _renameDialog.PopupCentered();
        _renameEdit.GrabFocus();
    }

    private void ValidateRename(string text)
    {
        var error = _renaming == null || Editor == null ? null : Editor.ValidateKey(_renaming, text);
        _renameError.Text = error ?? "";
        _renameDialog.GetOkButton().Disabled = error != null;
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Problem?.Invoke(e.Message);
        }
    }

    public void ShowDocument(Document doc, string title, LayoutStore.Saved? saved)
    {
        _title = title;
        Clear();
        BuildCard(doc.Root, title);
        AutoLayout();

        if (saved != null)
        {
            foreach (var (node, card) in _cards)
                if (saved.Cards.TryGetValue(node.Path, out var pos))
                    card.PositionOffset = pos;
        }

        RestoreView(saved?.Zoom ?? 1, saved?.Scroll ?? new Vector2(-40, -80));
    }

    /// <summary>
    /// Rebuilds every card from the document after a change. Cards stay where they were, even if
    /// renamed or moved, and a new card appears beside its parent.
    /// </summary>
    public void Refresh()
    {
        if (Editor == null)
            return;
        var positions = _cards.ToDictionary(kv => kv.Key, kv => kv.Value.PositionOffset);
        // Text applied from the text pane brings all-new nodes, so fall back to matching by path.
        // Those old nodes are detached by now, so use the paths they had when their cards were built.
        var byPath = _cards.ToDictionary(kv => _builtPaths[kv.Value], kv => kv.Value.PositionOffset);
        var heights = _cards.ToDictionary(kv => kv.Key, kv => kv.Value.Size.Y);
        var heightsByPath = _cards.ToDictionary(kv => _builtPaths[kv.Value], kv => kv.Value.Size.Y);
        var (zoom, scroll) = (Zoom, ScrollOffset);

        Clear();
        BuildCard(Editor.Document.Root, _title);
        PlaceCard(Editor.Document.Root);
        foreach (var (node, card) in _cards)
            if ((heights.TryGetValue(node, out var before) || heightsByPath.TryGetValue(_builtPaths[card], out before)) && card.Size.Y > before)
                PushDown(card, before);
        RestoreView(zoom, scroll);

        void PlaceCard(DocNode node)
        {
            var card = _cards[node];
            if (positions.TryGetValue(node, out var pos) || byPath.TryGetValue(node.Path, out pos))
            {
                card.PositionOffset = pos;
            }
            else if (node.Parent is { } parent && _cards.TryGetValue(parent, out var parentCard))
            {
                card.PositionOffset = parentCard.PositionOffset + new Vector2(parentCard.Size.X + ColumnGap, RowTop(parentCard, node.IndexInParent + 1));
            }
            foreach (var child in node.Children.Where(c => c.IsContainer))
                PlaceCard(child);
        }
    }

    /// <summary>How far down a card its row starts. Worked out from sizes, since rows aren't laid out until the next frame.</summary>
    private static float RowTop(GraphNode card, int row)
    {
        var gap = card.GetThemeConstant("separation");
        var top = 0f;
        for (var i = 0; i < row && i < card.GetChildCount(); i++)
            top += card.GetChild<Control>(i).GetCombinedMinimumSize().Y + gap;
        return top;
    }

    /// <summary>When a card grows, moves the cards that were below it down by as much, so they don't overlap.</summary>
    private void PushDown(GraphNode grown, float oldHeight)
    {
        var growth = grown.Size.Y - oldHeight;
        var oldBottom = grown.PositionOffset.Y + oldHeight;
        var left = grown.PositionOffset.X;
        var right = left + grown.Size.X;
        foreach (var card in _cards.Values)
        {
            var x = card.PositionOffset.X;
            if (card != grown && card.PositionOffset.Y >= oldBottom - 1 && x < right && x + card.Size.X > left)
                card.PositionOffset += new Vector2(0, growth);
        }
    }

    private void RestoreView(float zoom, Vector2 scroll)
    {
        // Scrolling only sticks once the new cards have been laid out, so wait a frame.
        Callable.From(() =>
        {
            Zoom = zoom;
            ScrollOffset = scroll;
        }).CallDeferred();
    }

    public Dictionary<string, Vector2> CardPositions() =>
        _cards.ToDictionary(kv => _builtPaths[kv.Value], kv => kv.Value.PositionOffset);

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
        _ids.Clear();
        _builtPaths.Clear();
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
        card.GetTitlebarHBox().AddChild(Badge(Palette.KindLabel(node), Palette.KindColor(node)));
        card.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
                OpenMenu(node);
        };
        AddChild(card);
        _cards[node] = card;
        _builtPaths[card] = node.Path;

        // Row 0 is the summary, and takes the incoming connection from the parent card.
        var summary = new CardRow(this, node, isHeader: true);
        summary.AddChild(new Label { Text = Summary(node), Modulate = new Color(1, 1, 1, 0.55f) });
        card.AddChild(summary);
        card.SetSlot(0, node.Parent != null, 0, Palette.KindColor(node), false, 0, Colors.White);

        var rightPort = 0;
        foreach (var child in node.Children)
        {
            var row = child.IsContainer ? ContainerRow(child) : ValueRow(child);
            row.TooltipText = child.Comment;
            card.AddChild(row);

            if (!child.IsContainer)
                continue;

            card.SetSlot(row.GetIndex(), false, 0, Colors.White, true, 0, Palette.KindColor(child));
            var childCard = BuildCard(child);
            ConnectNode(card.Name, rightPort++, childCard.Name, 0);
        }

        card.Size = card.GetCombinedMinimumSize();
        return card;
    }

    private CardRow NewRow(DocNode node)
    {
        var id = _ids.Count + 1L;
        _ids[id] = node;
        var row = new CardRow(this, node, isHeader: false);
        row.AddChild(new DragHandle(id, node.Key ?? $"[{node.IndexInParent}]"));
        row.AddChild(KeyLabel(node));
        return row;
    }

    private Control ContainerRow(DocNode child)
    {
        var row = NewRow(child);
        row.AddChild(new Label
        {
            Text = $"{Palette.KindLabel(child)} · {Palette.Count(child)}",
            HorizontalAlignment = HorizontalAlignment.Right,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Modulate = Palette.KindColor(child),
        });
        return row;
    }

    private Control ValueRow(DocNode value)
    {
        var row = NewRow(value);

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
                    Editor?.SetValue(value, on ? "true" : "false");
                    check.Text = value.Text;
                };
                row.AddChild(check);
                break;
            }
            case ValueKind.Null:
                row.AddChild(new Label { Text = "null", SizeFlagsHorizontal = SizeFlags.ExpandFill, Modulate = Palette.KindColor(value) });
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
                edit.AddThemeColorOverride("font_color", Palette.KindColor(value));
                edit.TextChanged += text =>
                {
                    // A value that doesn't fit its type isn't applied, so a save never writes broken data.
                    var error = DocumentEditor.ValidateValue(value, text);
                    edit.AddThemeColorOverride("font_color", error == null ? Palette.KindColor(value) : Palette.Error);
                    edit.TooltipText = error ?? "";
                    if (error == null)
                        Editor?.SetValue(value, text);
                };
                row.AddChild(edit);
                break;
            }
        }

        return row;
    }

    private Label KeyLabel(DocNode node)
    {
        var label = new Label
        {
            Text = node.Key ?? $"[{node.IndexInParent}]",
            CustomMinimumSize = new Vector2(120, 0),
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        if (node.Key != null)
        {
            // Double-click a name to rename it, as on the reference editor.
            label.MouseFilter = MouseFilterEnum.Pass;
            label.GuiInput += e =>
            {
                if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, DoubleClick: true })
                    StartRename(node);
            };
        }
        return label;
    }

    private static Control Badge(string text, Color color)
    {
        var badge = new Label { Text = text.ToUpperInvariant(), Modulate = color };
        badge.AddThemeFontSizeOverride("font_size", 11);
        return badge;
    }

    private static string CardTitle(DocNode node) => node.Parent?.Kind == NodeKind.Array
        ? $"{node.Parent.Key ?? ""}[{node.IndexInParent}]"
        : node.Key ?? "";

    private static string Summary(DocNode node) => $"{Palette.Count(node)}  ·  {node.Path}";

    private static int Depth(DocNode node)
    {
        var d = 0;
        for (var p = node.Parent; p != null; p = p.Parent)
            d++;
        return d;
    }
}
