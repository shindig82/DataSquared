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
    /// <summary>
    /// A scalar: string, number, boolean or null. In XML, an element holding only text, such as
    /// &lt;port&gt;9000&lt;/port&gt;, or (keyed <see cref="DocNode.TextKey"/>) a run of text between child elements.
    /// </summary>
    Value,
    /// <summary>XML element with attributes or child elements.</summary>
    Element,
    /// <summary>XML attribute. A scalar that always comes before its element's content.</summary>
    Attribute,
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
    /// <summary>The key of an XML text run that sits between child elements.</summary>
    public const string TextKey = "#text";

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

    public bool IsContainer => Kind is NodeKind.Object or NodeKind.Array or NodeKind.Section or NodeKind.Element;

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

    /// <summary>A copy of this node and everything under it, comments included, with no parent.</summary>
    public DocNode DeepClone()
    {
        var copy = new DocNode { Kind = Kind, Key = Key, ValueKind = ValueKind, Text = Text, Separator = Separator };
        copy.LeadingTrivia.AddRange(LeadingTrivia);
        copy.TrailingTrivia.AddRange(TrailingTrivia);
        foreach (var child in Children)
            copy.Add(child.DeepClone());
        return copy;
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

    /// <summary>
    /// Where this node's own comment starts in <see cref="LeadingTrivia"/>: the run of comment lines
    /// directly above it. A comment separated by a blank line, such as a file header, isn't counted.
    /// </summary>
    public int CommentStart
    {
        get
        {
            var start = LeadingTrivia.Count;
            while (start > 0 && IsCommentLine(LeadingTrivia[start - 1]))
                start--;
            return start;
        }
    }

    /// <summary>This node's comment with its markers stripped, for showing and editing as plain text.</summary>
    public string Comment
    {
        get
        {
            var lines = new List<string>();
            for (var i = CommentStart; i < LeadingTrivia.Count; i++)
            {
                var line = LeadingTrivia[i].Trim();
                if (line.StartsWith("<!--")) line = line[4..];
                if (line.EndsWith("-->")) line = line[..^3];
                if (line.StartsWith("/*")) line = line[2..];
                if (line.EndsWith("*/")) line = line[..^2];
                if (line.StartsWith("//")) line = line[2..];
                else if (line.StartsWith(';') || line.StartsWith('#') || line.StartsWith('*')) line = line[1..];
                lines.Add(line.Trim());
            }
            return string.Join("\n", lines).Trim('\n');
        }
    }

    /// <summary>Anything that isn't blank, an XML declaration or a doctype counts as part of a comment.</summary>
    private static bool IsCommentLine(string line)
    {
        var t = line.Trim();
        return t.Length > 0 && !t.StartsWith("<?") && !t.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
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
