using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DataSquared.Core.Model;

namespace DataSquared.Core.Formats;

/// <summary>
/// XML. An element that holds only text becomes a value, so &lt;port&gt;9000&lt;/port&gt; shows as a row
/// rather than a card of its own; anything with attributes or child elements becomes an element card.
/// Comments, the declaration and the order of everything are kept. A save re-indents the way the file
/// already was, and writes an empty element as &lt;name/&gt;.
/// </summary>
public static class XmlFormat
{
    public static Document Read(string text)
    {
        XDocument xml;
        try
        {
            xml = XDocument.Parse(text, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException e)
        {
            throw new FormatException($"{e.Message.Split(" Line ")[0].TrimEnd('.')} at line {e.LineNumber}, column {e.LinePosition}.", e);
        }
        if (xml.Root == null)
            throw new FormatException("The file has no root element.");

        var root = FromElement(xml.Root);
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith("<?xml"))
            root.LeadingTrivia.Add(trimmed[..(trimmed.IndexOf("?>", StringComparison.Ordinal) + 2)]);

        var before = true;
        foreach (var node in xml.Nodes())
        {
            if (node == xml.Root)
            {
                before = false;
                continue;
            }
            if (node is XText)
                continue;
            (before ? root.LeadingTrivia : root.TrailingTrivia).AddRange(Lines(node.ToString()));
        }

        return new Document(DocumentFormat.Xml, root)
        {
            Newline = Document.DetectNewline(text),
            Indent = DetectIndent(text),
            EndsWithNewline = text.EndsWith('\n'),
        };
    }

    public static string Write(Document doc)
    {
        var w = new Writer(doc.Newline, doc.Indent);
        foreach (var line in doc.Root.LeadingTrivia)
            w.Line(0, line);
        w.Node(doc.Root, 0);
        foreach (var line in doc.Root.TrailingTrivia)
            w.Line(0, line);
        var text = w.ToString();
        return doc.EndsWithNewline ? text : text[..^doc.Newline.Length];
    }

    /// <summary>A guess at the type of an attribute or text, used to pick an editor. XML itself has no types.</summary>
    public static ValueKind InferKind(string value) => IniFormat.InferKind(value);

    /// <summary>Why <paramref name="name"/> isn't a valid element or attribute name, or null if it is.</summary>
    public static string? BadName(string name)
    {
        try
        {
            XmlConvert.VerifyName(name);
            return null;
        }
        catch (XmlException)
        {
            return "Not a valid XML name. Names start with a letter or _, and can't contain spaces.";
        }
    }

    private static DocNode FromElement(XElement element)
    {
        var name = Name(element);
        var content = element.Nodes().Where(n => n is not XText t || !string.IsNullOrWhiteSpace(t.Value)).ToList();
        var onlyText = !element.HasAttributes && content.All(n => n is XText or XComment) && !content.OfType<XComment>().Any();

        if (onlyText)
        {
            var value = element.Value;
            return DocNode.Scalar(name, InferKind(value), value.Trim() == value ? value : value.Trim());
        }

        var node = DocNode.Container(NodeKind.Element, name);
        foreach (var attribute in element.Attributes())
            node.Add(DocNode.Scalar(AttributeName(attribute), InferKind(attribute.Value), attribute.Value)).Kind = NodeKind.Attribute;

        var pending = new List<string>();
        foreach (var child in content)
        {
            switch (child)
            {
                case XComment comment:
                    pending.AddRange(Lines(comment.ToString()));
                    continue;
                case XElement childElement:
                {
                    var added = node.Add(FromElement(childElement));
                    added.LeadingTrivia.AddRange(pending);
                    break;
                }
                case XText text:
                {
                    var added = node.Add(DocNode.Scalar(DocNode.TextKey, ValueKind.String, text.Value.Trim()));
                    added.LeadingTrivia.AddRange(pending);
                    break;
                }
                default:
                    // Processing instructions and the like: keep them, as comments are kept.
                    pending.AddRange(Lines(child.ToString()));
                    continue;
            }
            pending.Clear();
        }
        node.TrailingTrivia.AddRange(pending);
        return node;
    }

    private static string Name(XElement element)
    {
        var prefix = element.GetPrefixOfNamespace(element.Name.Namespace);
        return string.IsNullOrEmpty(prefix) ? element.Name.LocalName : $"{prefix}:{element.Name.LocalName}";
    }

    private static string AttributeName(XAttribute attribute)
    {
        if (attribute.IsNamespaceDeclaration)
            return attribute.Name.Namespace == XNamespace.None ? "xmlns" : $"xmlns:{attribute.Name.LocalName}";
        if (attribute.Name.Namespace == XNamespace.None)
            return attribute.Name.LocalName;
        var prefix = attribute.Parent?.GetPrefixOfNamespace(attribute.Name.Namespace);
        return string.IsNullOrEmpty(prefix) ? attribute.Name.LocalName : $"{prefix}:{attribute.Name.LocalName}";
    }

    /// <summary>A comment or similar as trimmed lines, so re-indenting on each save doesn't make it drift.</summary>
    private static IEnumerable<string> Lines(string raw) => raw.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim());

    private static string DetectIndent(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n').Skip(1))
        {
            var trimmed = line.TrimStart(' ', '\t');
            if (trimmed.StartsWith('<') && trimmed.Length < line.Length)
                return line[..(line.Length - trimmed.Length)];
        }
        return "  ";
    }

    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();
        private readonly string _newline;
        private readonly string _indent;

        public Writer(string newline, string indent)
        {
            _newline = newline;
            _indent = indent;
        }

        public void Line(int depth, string text) => _sb.Append(Indent(depth)).Append(text).Append(_newline);

        public void Node(DocNode node, int depth)
        {
            foreach (var line in node.Parent == null ? Enumerable.Empty<string>() : node.LeadingTrivia)
                Line(depth, line);

            if (node.Kind == NodeKind.Value)
            {
                if (node.Key == DocNode.TextKey)
                    Line(depth, EscapeText(node.Text));
                else
                    Line(depth, node.Text.Length == 0 ? $"<{node.Key}/>" : $"<{node.Key}>{EscapeText(node.Text)}</{node.Key}>");
                return;
            }

            var attributes = string.Concat(node.Children.Where(c => c.Kind == NodeKind.Attribute).Select(a => $" {a.Key}=\"{EscapeAttribute(a.Text)}\""));
            var content = node.Children.Where(c => c.Kind != NodeKind.Attribute).ToList();
            var open = $"<{node.Key}{attributes}";

            if (content.Count == 0 && node.TrailingTrivia.Count == 0)
            {
                Line(depth, open + "/>");
                return;
            }
            // <item id="1">Apple</item> stays on one line.
            if (content.Count == 1 && content[0].Key == DocNode.TextKey && content[0].LeadingTrivia.Count == 0 && node.TrailingTrivia.Count == 0)
            {
                Line(depth, $"{open}>{EscapeText(content[0].Text)}</{node.Key}>");
                return;
            }

            Line(depth, open + ">");
            foreach (var child in content)
                Node(child, depth + 1);
            foreach (var line in node.TrailingTrivia)
                Line(depth + 1, line);
            Line(depth, $"</{node.Key}>");
        }

        private string Indent(int depth) => string.Concat(Enumerable.Repeat(_indent, depth));

        public override string ToString() => _sb.ToString();
    }

    private static string EscapeText(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string EscapeAttribute(string s) => EscapeText(s).Replace("\"", "&quot;");
}
