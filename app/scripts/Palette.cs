using System.Linq;
using DataSquared.Core.Model;
using Godot;

namespace DataSquared;

/// <summary>Names and colors for each kind of node, shared by the canvas, tree and text pane.</summary>
public static class Palette
{
    public static readonly Color Object = new("9d8cff");
    public static readonly Color Array = new("5cb8ff");
    public static readonly Color String = new("6fdc9a");
    public static readonly Color Number = new("f0b450");
    public static readonly Color Boolean = new("ff7eb6");
    public static readonly Color Null = new("9a9aa8");
    public static readonly Color Comment = new("7a7a8c");
    public static readonly Color Error = new("ff5c6c");

    public static string KindLabel(DocNode node) => node.Kind switch
    {
        NodeKind.Object => "obj",
        NodeKind.Array => "arr",
        NodeKind.Section => "section",
        NodeKind.Element => "elem",
        NodeKind.Attribute => "attr",
        _ => node.ValueKind.ToString().ToLowerInvariant(),
    };

    public static Color KindColor(DocNode node) => node.Kind switch
    {
        NodeKind.Object or NodeKind.Section or NodeKind.Element => Object,
        NodeKind.Array => Array,
        _ => node.ValueKind switch
        {
            ValueKind.String => String,
            ValueKind.Number => Number,
            ValueKind.Boolean => Boolean,
            _ => Null,
        },
    };

    /// <summary>How a node's name is shown: @name for an attribute, "text" for a run of XML text, [i] for an array item.</summary>
    public static string Name(DocNode node) => node switch
    {
        { Key: DocNode.TextKey } => "text",
        { Kind: NodeKind.Attribute } => $"@{node.Key}",
        { Key: null } => $"[{node.IndexInParent}]",
        _ => node.Key,
    };

    public static string Count(DocNode node)
    {
        var n = node.Children.Count;
        if (node.Kind == NodeKind.Element)
        {
            var attributes = node.Children.Count(c => c.Kind == NodeKind.Attribute);
            var content = n - attributes;
            return (attributes > 0 ? $"{attributes} attr · " : "") + (content == 1 ? "1 child" : $"{content} children");
        }
        return node.Kind == NodeKind.Array
            ? n == 1 ? "1 item" : $"{n} items"
            : n == 1 ? "1 key" : $"{n} keys";
    }
}
