using System;
using System.Collections.Generic;
using System.Linq;
using DataSquared.Core.Model;
using Godot;

namespace DataSquared;

/// <summary>The outline on the left: every node as a row, with its type and value. Click one to find it on the canvas.</summary>
public partial class NodeTree : Tree
{
    private readonly Dictionary<DocNode, TreeItem> _items = new();
    private readonly Dictionary<TreeItem, DocNode> _nodes = new();
    private Document? _document;
    private string _title = "";
    private bool _rebuildQueued;
    private bool _selecting;

    /// <summary>Raised when the user picks a node in the tree.</summary>
    public event Action<DocNode>? NodePicked;

    public override void _Ready()
    {
        Columns = 3;
        HideRoot = false;
        SelectMode = SelectModeEnum.Row;
        SetColumnExpand(0, true);
        SetColumnExpand(1, false);
        SetColumnCustomMinimumWidth(1, 88);
        SetColumnExpand(2, true);
        ItemSelected += () =>
        {
            if (!_selecting && GetSelected() is { } item && _nodes.TryGetValue(item, out var node))
                NodePicked?.Invoke(node);
        };
    }

    public void ShowDocument(Document document, string title)
    {
        _document = document;
        _title = title;
        Rebuild();
    }

    /// <summary>Rebuilds once at the end of the frame, however many changes arrive before then.</summary>
    public void QueueRebuild()
    {
        if (_rebuildQueued)
            return;
        _rebuildQueued = true;
        Callable.From(Rebuild).CallDeferred();
    }

    public void SelectNode(DocNode node)
    {
        if (!_items.TryGetValue(node, out var item))
            return;
        _selecting = true;
        for (var p = item.GetParent(); p != null; p = p.GetParent())
            p.Collapsed = false;
        item.Select(0);
        ScrollToItem(item);
        _selecting = false;
    }

    private void Rebuild()
    {
        _rebuildQueued = false;
        var collapsed = _items.Where(kv => kv.Value.Collapsed).Select(kv => kv.Key).ToHashSet();
        var selected = GetSelected() is { } sel ? _nodes.GetValueOrDefault(sel) : null;

        Clear();
        _items.Clear();
        _nodes.Clear();
        if (_document == null)
            return;

        Add(_document.Root, null);
        if (selected != null && _items.TryGetValue(selected, out var again))
        {
            _selecting = true;
            again.Select(0);
            _selecting = false;
        }

        void Add(DocNode node, TreeItem? parent)
        {
            var item = CreateItem(parent);
            _items[node] = item;
            _nodes[item] = node;

            item.SetText(0, node.Parent == null ? _title : node.Key ?? $"[{node.IndexInParent}]");
            item.SetTooltipText(0, node.Comment.Length > 0 ? node.Comment : node.Path);
            item.SetText(1, Palette.KindLabel(node).ToUpperInvariant());
            item.SetCustomColor(1, Palette.KindColor(node));
            item.SetText(2, node.IsContainer ? Palette.Count(node) : Preview(node));
            item.SetCustomColor(2, node.IsContainer ? new Color(1, 1, 1, 0.45f) : Palette.KindColor(node));
            item.Collapsed = collapsed.Contains(node);

            foreach (var child in node.Children)
                Add(child, item);
        }
    }

    private static string Preview(DocNode node)
    {
        var text = node.ValueKind == ValueKind.String ? $"\"{node.Text}\"" : node.Text;
        text = text.Replace("\n", " ");
        return text.Length > 60 ? text[..60] + "…" : text;
    }
}
