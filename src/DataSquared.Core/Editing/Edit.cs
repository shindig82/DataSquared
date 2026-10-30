using System;
using System.Collections.Generic;
using System.Linq;
using DataSquared.Core.Model;

namespace DataSquared.Core.Editing;

/// <summary>One undoable change to a document. Made and applied by <see cref="DocumentEditor"/>.</summary>
public abstract class Edit
{
    public abstract string Description { get; }

    internal abstract void Apply();

    internal abstract void Revert();
}

internal sealed class SetValueEdit : Edit
{
    private readonly string _old;

    public SetValueEdit(DocNode node, string text)
    {
        Node = node;
        _old = node.Text;
        Text = text;
    }

    public DocNode Node { get; }

    public string Text { get; private set; }

    public DateTime At { get; set; }

    public override string Description => $"Edit {Node.Key ?? "value"}";

    internal override void Apply() => Node.Text = Text;

    internal override void Revert() => Node.Text = _old;

    /// <summary>Folds a later keystroke on the same value into this edit, so undo takes back the whole word.</summary>
    public void Absorb(SetValueEdit next)
    {
        Text = next.Text;
        At = next.At;
    }
}

internal sealed class RenameEdit : Edit
{
    private readonly DocNode _node;
    private readonly string? _old;
    private readonly string _new;

    public RenameEdit(DocNode node, string key)
    {
        _node = node;
        _old = node.Key;
        _new = key;
    }

    public override string Description => $"Rename {_old} to {_new}";

    internal override void Apply() => _node.Key = _new;

    internal override void Revert() => _node.Key = _old;
}

/// <summary>Moves a node to a new place, or with no old parent, adds it; with no new parent, removes it.</summary>
internal sealed class PlaceEdit : Edit
{
    private readonly DocNode _node;
    private readonly DocNode? _oldParent;
    private readonly int _oldIndex;
    private readonly string? _oldKey;
    private readonly DocNode? _newParent;
    private readonly int _newIndex;
    private readonly string? _newKey;

    public PlaceEdit(string description, DocNode node, DocNode? newParent, int newIndex, string? newKey)
    {
        Description = description;
        _node = node;
        _oldParent = node.Parent;
        _oldIndex = node.IndexInParent;
        _oldKey = node.Key;
        _newParent = newParent;
        _newIndex = newIndex;
        _newKey = newKey;
    }

    public override string Description { get; }

    internal override void Apply() => Place(_oldParent, _newParent, _newIndex, _newKey);

    internal override void Revert() => Place(_newParent, _oldParent, _oldIndex, _oldKey);

    private void Place(DocNode? from, DocNode? to, int index, string? key)
    {
        from?.Remove(_node);
        _node.Key = key;
        to?.Insert(index, _node);
    }
}

/// <summary>Changes a node's type. Children dropped by the change come back on undo.</summary>
internal sealed class ChangeTypeEdit : Edit
{
    private readonly DocNode _node;
    private readonly Snapshot _old;
    private readonly Snapshot _new;

    public ChangeTypeEdit(DocNode node, NodeKind kind, ValueKind valueKind, string text, IReadOnlyList<(DocNode Child, string? Key)> children)
    {
        _node = node;
        _old = Snapshot.Of(node);
        _new = new Snapshot(kind, valueKind, text, children);
    }

    public override string Description => $"Change {_node.Key ?? "value"} to {(_new.Kind == NodeKind.Value ? _new.ValueKind.ToString() : _new.Kind.ToString()).ToLowerInvariant()}";

    internal override void Apply() => _new.RestoreTo(_node);

    internal override void Revert() => _old.RestoreTo(_node);

    private sealed record Snapshot(NodeKind Kind, ValueKind ValueKind, string Text, IReadOnlyList<(DocNode Child, string? Key)> Children)
    {
        public static Snapshot Of(DocNode node) =>
            new(node.Kind, node.ValueKind, node.Text, node.Children.Select(c => (c, c.Key)).ToList());

        public void RestoreTo(DocNode node)
        {
            foreach (var child in node.Children.ToList())
                node.Remove(child);
            node.Kind = Kind;
            node.ValueKind = ValueKind;
            node.Text = Text;
            foreach (var (child, key) in Children)
            {
                child.Key = key;
                node.Add(child);
            }
        }
    }
}

/// <summary>Swaps the whole document's content for another's, such as text typed into the text pane.</summary>
internal sealed class ReplaceAllEdit : Edit
{
    private readonly DocNode _root;
    private readonly Content _old;
    private readonly Content _new;

    public ReplaceAllEdit(DocNode root, DocNode replacement)
    {
        _root = root;
        _old = Content.Of(root);
        _new = Content.Of(replacement);
        foreach (var child in replacement.Children.ToList())
            replacement.Remove(child);
    }

    public override string Description => "Apply text";

    internal override void Apply() => _new.RestoreTo(_root);

    internal override void Revert() => _old.RestoreTo(_root);

    private sealed record Content(NodeKind Kind, List<DocNode> Children, List<string> Leading, List<string> Trailing)
    {
        public static Content Of(DocNode node) =>
            new(node.Kind, node.Children.ToList(), node.LeadingTrivia.ToList(), node.TrailingTrivia.ToList());

        public void RestoreTo(DocNode node)
        {
            foreach (var child in node.Children.ToList())
                node.Remove(child);
            node.Kind = Kind;
            foreach (var child in Children)
                node.Add(child);
            node.LeadingTrivia.Clear();
            node.LeadingTrivia.AddRange(Leading);
            node.TrailingTrivia.Clear();
            node.TrailingTrivia.AddRange(Trailing);
        }
    }
}

internal sealed class CommentEdit : Edit
{
    private readonly DocNode _node;
    private readonly List<string> _old;
    private readonly List<string> _new;

    public CommentEdit(DocNode node, List<string> trivia)
    {
        _node = node;
        _old = node.LeadingTrivia.ToList();
        _new = trivia;
    }

    public override string Description => $"Comment on {_node.Key ?? "item"}";

    internal override void Apply() => Set(_new);

    internal override void Revert() => Set(_old);

    private void Set(List<string> trivia)
    {
        _node.LeadingTrivia.Clear();
        _node.LeadingTrivia.AddRange(trivia);
    }
}
