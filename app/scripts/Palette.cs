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
        _ => node.ValueKind.ToString().ToLowerInvariant(),
    };

    public static Color KindColor(DocNode node) => node.Kind switch
    {
        NodeKind.Object or NodeKind.Section => Object,
        NodeKind.Array => Array,
        _ => node.ValueKind switch
        {
            ValueKind.String => String,
            ValueKind.Number => Number,
            ValueKind.Boolean => Boolean,
            _ => Null,
        },
    };

    public static string Count(DocNode node)
    {
        var n = node.Children.Count;
        return node.Kind == NodeKind.Array
            ? n == 1 ? "1 item" : $"{n} items"
            : n == 1 ? "1 key" : $"{n} keys";
    }
}
