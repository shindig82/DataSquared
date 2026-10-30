using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DataSquared.Core.Model;

namespace DataSquared.Core.Formats;

/// <summary>
/// INI and .cfg files: [section] headers, key = value lines, and ; or # comments.
/// Comments, blank lines, repeated sections and value spacing are all kept, so an unedited file
/// writes back byte for byte.
/// </summary>
public static class IniFormat
{
    public static Document Read(string text)
    {
        var root = DocNode.Container(NodeKind.Object);
        var doc = new Document(DocumentFormat.Ini, root)
        {
            Newline = Document.DetectNewline(text),
            EndsWithNewline = text.Length == 0 || text.EndsWith('\n'),
        };

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var lineCount = doc.EndsWithNewline && text.Length > 0 ? lines.Length - 1 : lines.Length;

        var current = root;
        var pending = new List<string>();

        for (var i = 0; i < lineCount; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                pending.Add(line);
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && line == trimmed)
            {
                current = root.Add(DocNode.Container(NodeKind.Section, trimmed[1..^1].Trim()));
                current.LeadingTrivia.AddRange(pending);
                pending.Clear();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || line[0] == ' ' || line[0] == '\t')
            {
                // Not something we understand. Keep it verbatim rather than lose it.
                pending.Add(line);
                continue;
            }

            var key = line[..eq].TrimEnd();
            var valueStart = eq + 1;
            while (valueStart < line.Length && (line[valueStart] == ' ' || line[valueStart] == '\t'))
                valueStart++;

            var value = line[valueStart..];
            var node = current.Add(DocNode.Scalar(key, InferKind(value), value));
            node.Separator = line[key.Length..valueStart];
            node.LeadingTrivia.AddRange(pending);
            pending.Clear();
        }

        root.TrailingTrivia.AddRange(pending);
        return doc;
    }

    public static string Write(Document doc)
    {
        var lines = new List<string>();
        foreach (var child in doc.Root.Children)
        {
            lines.AddRange(child.LeadingTrivia);
            if (child.Kind == NodeKind.Section)
            {
                lines.Add($"[{child.Key}]");
                foreach (var entry in child.Children)
                {
                    lines.AddRange(entry.LeadingTrivia);
                    lines.Add(WriteEntry(entry));
                }
                lines.AddRange(child.TrailingTrivia);
            }
            else
            {
                lines.Add(WriteEntry(child));
            }
        }
        lines.AddRange(doc.Root.TrailingTrivia);

        var sb = new StringBuilder(string.Join(doc.Newline, lines));
        if (doc.EndsWithNewline && lines.Count > 0)
            sb.Append(doc.Newline);
        return sb.ToString();
    }

    /// <summary>INI has no types, so this is only a guess used to pick an editor.</summary>
    public static ValueKind InferKind(string value)
    {
        var v = value.Trim();
        if (v == "true" || v == "false")
            return ValueKind.Boolean;
        if (v.Length > 0 && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return ValueKind.Number;
        return ValueKind.String;
    }

    private static string WriteEntry(DocNode entry) => $"{entry.Key}{entry.Separator ?? " = "}{entry.Text}";
}
