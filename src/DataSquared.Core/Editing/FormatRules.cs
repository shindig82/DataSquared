using System.Linq;
using DataSquared.Core.Model;

namespace DataSquared.Core.Editing;

/// <summary>What each format can hold, so an edit can't produce a file that won't read back.</summary>
internal static class FormatRules
{
    /// <summary>Why <paramref name="parent"/> can't hold a node of this kind, or null if it can.</summary>
    public static string? CannotHold(DocumentFormat format, DocNode parent, NodeKind kind, ValueKind valueKind)
    {
        if (parent.Kind == NodeKind.Value)
            return "Values can't hold other nodes. Change it to an object or array first.";

        if (format == DocumentFormat.Json)
            return kind == NodeKind.Section ? "JSON has no sections. Add an object instead." : null;

        // INI: the file holds sections and values, and sections hold values.
        if (kind is NodeKind.Object or NodeKind.Array)
            return "INI files can't nest objects or arrays.";
        if (kind == NodeKind.Section && parent.Parent != null)
            return "Sections can only go at the top level.";
        if (kind == NodeKind.Value && valueKind == ValueKind.Null)
            return "INI files have no null.";
        return null;
    }

    /// <summary>
    /// INI values before the first section belong to no section, and values after a section belong to it,
    /// so at the top level values have to stay before every section and sections after every value.
    /// </summary>
    public static int ClampIndex(DocumentFormat format, DocNode parent, NodeKind kind, int index)
    {
        index = System.Math.Clamp(index, 0, parent.Children.Count);
        if (format != DocumentFormat.Ini || parent.Parent != null)
            return index;

        var firstSection = parent.Children.FindIndex(c => c.Kind == NodeKind.Section);
        if (firstSection < 0)
            return index;
        return kind == NodeKind.Section ? System.Math.Max(index, firstSection) : System.Math.Min(index, firstSection);
    }

    public static string? BadIniName(string key)
    {
        if (key != key.Trim())
            return "INI names can't start or end with spaces.";
        if (key.Any(c => c is '=' or '\n' or '\r' or '[' or ']'))
            return "INI names can't contain =, [, ] or line breaks.";
        if (key[0] is ';' or '#')
            return "INI names can't start with ; or #.";
        return null;
    }
}
