using System;
using System.Collections.Generic;
using System.Linq;

namespace DataSquared.Core.Model;

public enum NodeKind
{
    /// <summary>JSON object, or the root of an INI file.</summary>
    Object,
    /// <summary>JSON array.</summary>
    Array,
    /// <summary>INI [section]. Behaves like an object, but a file may repeat one.</summary>
    Section,
    /// <summary>A scalar: string, number, boolean or null.</summary>
    Value,
}

public enum ValueKind
{
    None,
    String,
    Number,
    Boolean,
    Null,
}

/// <summary>
/// One node of a document, shared by every format. Containers hold children in file order;
/// scalars hold their text. Comments and blank lines ride along as trivia so they survive a save.
/// </summary>
public sealed class DocNode
{
    public NodeKind Kind { get; set; }

    /// <summary>The key or section name. Null for array items and the root.</summary>
    public string? Key { get; set; }

    public ValueKind ValueKind { get; set; }

    /// <summary>
    /// A scalar's value. JSON strings are stored unescaped; INI values are stored exactly as written.
    /// </summary>
    public string Text { get; set; } = "";

    public DocNode? Parent { get; private set; }

    public List<DocNode> Children { get; } = new();

    /// <summary>Raw comment and blank lines written before this node, without newlines.</summary>
    public List<string> LeadingTrivia { get; } = new();

    /// <summary>Raw comment and blank lines written after a container's last child.</summary>
    public List<string> TrailingTrivia { get; } = new();

    /// <summary>INI only: the text between key and value, such as " = ".</summary>
    public string? Separator { get; set; }

    public bool IsContainer => Kind != NodeKind.Value;

    public static DocNode Container(NodeKind kind, string? key = null) => new() { Kind = kind, Key = key };

    public static DocNode Scalar(string? key, ValueKind valueKind, string text) =>
        new() { Kind = NodeKind.Value, Key = key, ValueKind = valueKind, Text = text };

    public DocNode Add(DocNode child) => Insert(Children.Count, child);

    public DocNode Insert(int index, DocNode child)
    {
        if (child.Parent != null)
            throw new InvalidOperationException("The node already has a parent.");
        child.Parent = this;
        Children.Insert(index, child);
        return child;
    }

    public void Remove(DocNode child)
    {
        if (child.Parent != this)
            throw new InvalidOperationException("The node isn't a child of this one.");
        Children.Remove(child);
        child.Parent = null;
    }

    public bool IsAncestorOf(DocNode node)
    {
        for (var p = node.Parent; p != null; p = p.Parent)
            if (p == this)
                return true;
        return false;
    }

    public int IndexInParent => Parent?.Children.IndexOf(this) ?? -1;

    /// <summary>
    /// A stable address for this node, such as "$/network/port" or "$/items/0/label".
    /// A repeated key or section gets "#n" for its n-th repeat, so "$/network#1" is the second [network].
    /// </summary>
    public string Path
    {
        get
        {
            if (Parent == null)
                return "$";
            if (Parent.Kind == NodeKind.Array)
                return $"{Parent.Path}/{IndexInParent}";

            var name = EscapePathSegment(Key ?? "");
            var repeat = Parent.Children.TakeWhile(c => c != this).Count(c => c.Key == Key);
            return repeat == 0 ? $"{Parent.Path}/{name}" : $"{Parent.Path}/{name}#{repeat}";
        }
    }

    /// <summary>This node's comment lines with their markers stripped, for showing as help text.</summary>
    public string Comment
    {
        get
        {
            var lines = LeadingTrivia
                .Select(l => l.Trim())
                .Where(l => l.StartsWith(';') || l.StartsWith('#') || l.StartsWith("//") || l.StartsWith("/*") || l.StartsWith('*'))
                .Select(l => l.TrimStart(';', '#', '/', '*').TrimEnd('/', '*').Trim());
            return string.Join("\n", lines).Trim();
        }
    }

    public IEnumerable<DocNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var d in child.Descendants())
                yield return d;
        }
    }

    private static string EscapePathSegment(string key) =>
        key.Replace("~", "~0").Replace("/", "~1").Replace("#", "~2");
}
