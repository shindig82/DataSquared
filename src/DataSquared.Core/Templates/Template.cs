using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataSquared.Core.Editing;
using DataSquared.Core.Model;

namespace DataSquared.Core.Templates;

/// <summary>
/// A node and everything under it, saved under a name to insert again later, in this file or another.
/// It's kept apart from any format: comments are kept as text, and the node is fitted to the file it
/// goes into, so a section saved from an INI file can go into a JSON file as an object.
/// </summary>
public sealed class Template
{
    private Template(string name, DocumentFormat format, DocNode node)
    {
        Name = name;
        Format = format;
        Node = node;
    }

    public string Name { get; }

    /// <summary>The format of the file it was saved from.</summary>
    public DocumentFormat Format { get; }

    /// <summary>The saved node, detached from any document. Not to be changed; see <see cref="Instantiate"/>.</summary>
    public DocNode Node { get; }

    public static Template FromNode(string name, DocNode node, DocumentFormat format)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A template needs a name.", nameof(name));
        if (node.Key == DocNode.TextKey)
            throw new InvalidOperationException("A run of XML text can't be a template.");
        return new Template(name.Trim(), format, Copy(node));
    }

    /// <summary>
    /// A fresh copy of the node, fitted to <paramref name="format"/>: sections, objects and elements
    /// become whichever that format has, attributes become plain values outside XML, and comments are
    /// written in its style. <see cref="DocumentEditor.CanInsert"/> says whether the result fits.
    /// </summary>
    public DocNode Instantiate(DocumentFormat format)
    {
        var copy = Node.DeepClone();
        Fit(copy, format, top: true);
        return copy;
    }

    public string ToJson() =>
        JsonSerializer.Serialize(new Stored(1, Name, Format.ToString(), Save(Node)), Options);

    public static Template FromJson(string json)
    {
        Stored? stored;
        try
        {
            stored = JsonSerializer.Deserialize<Stored>(json, Options);
        }
        catch (JsonException e)
        {
            throw new FormatException($"Not a DataSquared template: {e.Message}", e);
        }
        if (stored?.Node == null || string.IsNullOrWhiteSpace(stored.Name) || !Enum.TryParse<DocumentFormat>(stored.Format, out var format))
            throw new FormatException("Not a DataSquared template.");
        return new Template(stored.Name, format, Load(stored.Node));
    }

    /// <summary>Copies a node with its comment as plain text, free of the source format's comment markers.</summary>
    private static DocNode Copy(DocNode node)
    {
        var copy = node.Kind is NodeKind.Value or NodeKind.Attribute
            ? DocNode.Scalar(node.Key, node.ValueKind, node.Text)
            : DocNode.Container(node.Kind, node.Key);
        copy.Kind = node.Kind;
        if (node.Comment.Length > 0)
            copy.LeadingTrivia.Add(node.Comment);
        foreach (var child in node.Children)
            copy.Add(Copy(child));
        return copy;
    }

    private static void Fit(DocNode node, DocumentFormat format, bool top)
    {
        node.Kind = (node.Kind, format) switch
        {
            (NodeKind.Section or NodeKind.Element, DocumentFormat.Json) => NodeKind.Object,
            (NodeKind.Object or NodeKind.Element, DocumentFormat.Ini) when top => NodeKind.Section,
            (NodeKind.Element, DocumentFormat.Ini) => NodeKind.Object, // Refused on insert: INI can't nest.
            (NodeKind.Object or NodeKind.Section, DocumentFormat.Xml) => NodeKind.Element,
            (NodeKind.Attribute, not DocumentFormat.Xml) => NodeKind.Value,
            var (kind, _) => kind,
        };
        node.Separator = null;

        // The template keeps a comment as plain text; write it the way this format does.
        var comment = string.Join("\n", node.LeadingTrivia);
        node.LeadingTrivia.Clear();
        node.TrailingTrivia.Clear();
        node.LeadingTrivia.AddRange(FormatRules.CommentLines(format, comment));
        // INI files set sections apart with a blank line.
        if (node.Kind == NodeKind.Section)
            node.LeadingTrivia.Insert(0, "");

        foreach (var child in node.Children)
            Fit(child, format, top: false);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Stored(int Version, string Name, string Format, StoredNode Node);

    private sealed record StoredNode(string Kind, string? Key, string? Type, string? Text, string? Comment, List<StoredNode>? Children);

    private static StoredNode Save(DocNode node) => new(
        node.Kind.ToString(),
        node.Key,
        node.IsContainer ? null : node.ValueKind.ToString(),
        node.IsContainer ? null : node.Text,
        node.LeadingTrivia.Count > 0 ? string.Join("\n", node.LeadingTrivia) : null,
        node.IsContainer ? node.Children.Select(Save).ToList() : null);

    private static DocNode Load(StoredNode stored)
    {
        if (!Enum.TryParse<NodeKind>(stored.Kind, out var kind))
            throw new FormatException($"Unknown kind {stored.Kind}.");
        DocNode node;
        if (kind is NodeKind.Value or NodeKind.Attribute)
        {
            if (!Enum.TryParse<ValueKind>(stored.Type, out var valueKind) || valueKind == ValueKind.None)
                throw new FormatException($"Unknown type {stored.Type}.");
            node = DocNode.Scalar(stored.Key, valueKind, stored.Text ?? "");
            node.Kind = kind;
        }
        else
        {
            node = DocNode.Container(kind, stored.Key);
            foreach (var child in stored.Children ?? new List<StoredNode>())
                node.Add(Load(child));
        }
        if (!string.IsNullOrEmpty(stored.Comment))
            node.LeadingTrivia.Add(stored.Comment);
        return node;
    }
}
