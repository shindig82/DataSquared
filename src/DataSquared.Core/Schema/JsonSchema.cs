using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DataSquared.Core.Model;

namespace DataSquared.Core.Schema;

/// <summary>Something in a document that its schema doesn't allow.</summary>
public sealed record SchemaProblem(DocNode Node, string Message);

/// <summary>
/// A JSON Schema, checked against a document of any format. INI sections and XML elements count as
/// objects, and their keys and attributes as properties. INI and XML values are text, so in those
/// formats any value passes as a string, and a number or boolean only has to look like one.
/// Covers the common keywords of drafts 4 to 2020-12: type, enum, const, properties, required,
/// additionalProperties, patternProperties, items, prefixItems, min/max for numbers, lengths and
/// counts, pattern, allOf/anyOf/oneOf/not, and $ref within the same file.
/// </summary>
public sealed class JsonSchema
{
    private readonly JsonNode _root;
    private readonly Dictionary<string, Regex?> _patterns = new();

    private JsonSchema(JsonNode root) => _root = root;

    public static JsonSchema Parse(string text)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            throw new FormatException($"The schema isn't valid JSON: {e.Message}", e);
        }
        if (root is not JsonObject and not JsonValue)
            throw new FormatException("A schema has to be a JSON object.");
        return new JsonSchema(root);
    }

    public IReadOnlyList<SchemaProblem> Validate(Document doc)
    {
        var problems = new List<SchemaProblem>();
        Check(_root, doc.Root, doc.Format, problems, 0);
        return problems;
    }

    /// <summary>The description the schema gives for <paramref name="node"/>, if it has one.</summary>
    public string? DescriptionOf(Document doc, DocNode node)
    {
        var schema = SchemaAt(doc, node);
        return schema is JsonObject o && o["description"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    /// <summary>The values the schema allows for <paramref name="node"/>, from its enum, if it has one.</summary>
    public IReadOnlyList<string>? ChoicesFor(Document doc, DocNode node)
    {
        if (SchemaAt(doc, node) is not JsonObject o || o["enum"] is not JsonArray values)
            return null;
        return values.Select(v => v is JsonValue j && j.TryGetValue<string>(out var s) ? s : v?.ToJsonString() ?? "null").ToList();
    }

    /// <summary>The schema that applies to a node, following properties and items down from the root.</summary>
    private JsonNode? SchemaAt(Document doc, DocNode node)
    {
        var chain = new Stack<DocNode>();
        for (var n = node; n.Parent != null; n = n.Parent)
            chain.Push(n);

        var schema = Resolve(_root);
        while (chain.Count > 0 && schema is JsonObject)
        {
            var child = chain.Pop();
            schema = Resolve(child.Parent!.Kind == NodeKind.Array ? ItemSchema(schema, child.IndexInParent) : PropertySchema(schema, child.Key ?? ""));
        }
        return schema;
    }

    private void Check(JsonNode? schema, DocNode node, DocumentFormat format, List<SchemaProblem> problems, int depth)
    {
        schema = Resolve(schema);
        if (depth > 64 || schema == null)
            return;
        if (schema is JsonValue b && b.TryGetValue<bool>(out var allowed))
        {
            if (!allowed)
                problems.Add(new(node, $"{Name(node)} isn't allowed here."));
            return;
        }
        if (schema is not JsonObject s)
            return;

        if (s["type"] is { } type)
        {
            var types = type is JsonArray list ? list.Select(t => t?.GetValue<string>() ?? "").ToList() : new List<string> { type.GetValue<string>() };
            if (!types.Any(t => Matches(t, node, format)))
            {
                problems.Add(new(node, $"{Name(node)} should be {JoinOr(types.Select(Article))}, not {Article(TypeOf(node, format))}."));
                return;
            }
        }

        if (s["enum"] is JsonArray choices && !choices.Any(c => Equal(c, node, format)))
            problems.Add(new(node, $"{Name(node)} should be one of {string.Join(", ", choices.Select(c => c?.ToJsonString() ?? "null"))}."));
        if (s.TryGetPropertyValue("const", out var constant) && !Equal(constant, node, format))
            problems.Add(new(node, $"{Name(node)} should be {constant?.ToJsonString() ?? "null"}."));

        if (node.IsContainer && node.Kind != NodeKind.Array)
            CheckObject(s, node, format, problems, depth);
        else if (node.Kind == NodeKind.Array)
            CheckArray(s, node, format, problems, depth);
        else
            CheckValue(s, node, format, problems);

        if (s["allOf"] is JsonArray all)
            foreach (var part in all)
                Check(part, node, format, problems, depth + 1);
        if (s["anyOf"] is JsonArray any && !any.Any(part => Passes(part, node, format, depth)))
            problems.Add(new(node, $"{Name(node)} doesn't match any of the allowed shapes."));
        if (s["oneOf"] is JsonArray one && one.Count(part => Passes(part, node, format, depth)) != 1)
            problems.Add(new(node, $"{Name(node)} should match exactly one of the allowed shapes."));
        if (s["not"] is { } not && Passes(not, node, format, depth))
            problems.Add(new(node, $"{Name(node)} matches a shape that isn't allowed."));
    }

    private bool Passes(JsonNode? schema, DocNode node, DocumentFormat format, int depth)
    {
        var trial = new List<SchemaProblem>();
        Check(schema, node, format, trial, depth + 1);
        return trial.Count == 0;
    }

    private void CheckObject(JsonObject s, DocNode node, DocumentFormat format, List<SchemaProblem> problems, int depth)
    {
        var members = node.Children.Where(c => c.Key != DocNode.TextKey).ToList();

        if (s["required"] is JsonArray required)
            foreach (var key in required.Select(r => r?.GetValue<string>()).OfType<string>())
                if (members.All(m => m.Key != key))
                    problems.Add(new(node, $"{Name(node)} is missing {key}."));

        Count(s, "minProperties", "maxProperties", members.Count, "key", node, problems);

        var properties = s["properties"] as JsonObject;
        var patterns = s["patternProperties"] as JsonObject;
        var additional = s["additionalProperties"];
        foreach (var member in members)
        {
            var key = member.Key ?? "";
            var known = false;
            if (properties != null && properties.TryGetPropertyValue(key, out var property))
            {
                known = true;
                Check(property, member, format, problems, depth + 1);
            }
            if (patterns != null)
                foreach (var (pattern, propertySchema) in patterns)
                    if (Regex(pattern)?.IsMatch(key) == true)
                    {
                        known = true;
                        Check(propertySchema, member, format, problems, depth + 1);
                    }
            if (known || additional == null)
                continue;
            if (additional is JsonValue v && v.TryGetValue<bool>(out var open))
            {
                if (!open)
                    problems.Add(new(member, $"{Name(node)} doesn't have a {key}."));
            }
            else
                Check(additional, member, format, problems, depth + 1);
        }
    }

    private void CheckArray(JsonObject s, DocNode node, DocumentFormat format, List<SchemaProblem> problems, int depth)
    {
        Count(s, "minItems", "maxItems", node.Children.Count, "item", node, problems);
        for (var i = 0; i < node.Children.Count; i++)
            Check(ItemSchema(s, i), node.Children[i], format, problems, depth + 1);

        if (s["uniqueItems"] is JsonValue u && u.TryGetValue<bool>(out var unique) && unique)
        {
            var seen = new HashSet<string>();
            foreach (var item in node.Children.Where(c => !c.IsContainer))
                if (!seen.Add(item.Text))
                    problems.Add(new(item, $"{Name(node)} already has {item.Text}."));
        }
    }

    private void CheckValue(JsonObject s, DocNode node, DocumentFormat format, List<SchemaProblem> problems)
    {
        var text = Plain(node, format);
        if (IsNumber(node, format) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            if (Number(s, "minimum") is { } min && n < min)
                problems.Add(new(node, $"{Name(node)} should be at least {min.ToString(CultureInfo.InvariantCulture)}."));
            if (Number(s, "maximum") is { } max && n > max)
                problems.Add(new(node, $"{Name(node)} should be at most {max.ToString(CultureInfo.InvariantCulture)}."));
            if (Number(s, "exclusiveMinimum") is { } xmin && n <= xmin)
                problems.Add(new(node, $"{Name(node)} should be more than {xmin.ToString(CultureInfo.InvariantCulture)}."));
            if (Number(s, "exclusiveMaximum") is { } xmax && n >= xmax)
                problems.Add(new(node, $"{Name(node)} should be less than {xmax.ToString(CultureInfo.InvariantCulture)}."));
            if (Number(s, "multipleOf") is { } step && step > 0 && Math.Abs(Math.IEEERemainder(n, step)) > 1e-9)
                problems.Add(new(node, $"{Name(node)} should be a multiple of {step.ToString(CultureInfo.InvariantCulture)}."));
        }

        if (node.ValueKind == ValueKind.String || format != DocumentFormat.Json)
        {
            Count(s, "minLength", "maxLength", text.Length, "character", node, problems);
            if (s["pattern"] is JsonValue p && p.TryGetValue<string>(out var pattern) && Regex(pattern) is { } regex && !regex.IsMatch(text))
                problems.Add(new(node, $"{Name(node)} doesn't match the pattern {pattern}."));
        }
    }

    private static void Count(JsonObject s, string minKey, string maxKey, int count, string unit, DocNode node, List<SchemaProblem> problems)
    {
        if (Number(s, minKey) is { } min && count < min)
            problems.Add(new(node, $"{Name(node)} needs at least {Plural((int)min, unit)}."));
        if (Number(s, maxKey) is { } max && count > max)
            problems.Add(new(node, $"{Name(node)} can have at most {Plural((int)max, unit)}."));
    }

    private JsonNode? PropertySchema(JsonNode? schema, string key)
    {
        if (schema is not JsonObject s)
            return null;
        if (s["properties"] is JsonObject properties && properties.TryGetPropertyValue(key, out var property))
            return property;
        if (s["patternProperties"] is JsonObject patterns)
            foreach (var (pattern, propertySchema) in patterns)
                if (Regex(pattern)?.IsMatch(key) == true)
                    return propertySchema;
        return s["additionalProperties"] is JsonObject additional ? additional : null;
    }

    private static JsonNode? ItemSchema(JsonNode? schema, int index)
    {
        if (schema is not JsonObject s)
            return null;
        // 2020-12 uses prefixItems + items; earlier drafts use items as an array + additionalItems.
        if (s["prefixItems"] is JsonArray prefix)
            return index < prefix.Count ? prefix[index] : s["items"];
        if (s["items"] is JsonArray tuple)
            return index < tuple.Count ? tuple[index] : s["additionalItems"];
        return s["items"];
    }

    /// <summary>Follows $ref to a schema elsewhere in the same file, such as #/$defs/port.</summary>
    private JsonNode? Resolve(JsonNode? schema)
    {
        for (var hops = 0; hops < 32 && schema is JsonObject s && s["$ref"] is JsonValue r && r.TryGetValue<string>(out var reference); hops++)
        {
            if (!reference.StartsWith('#'))
                return null; // Schemas in other files aren't fetched.
            JsonNode? target = _root;
            foreach (var part in reference.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = Uri.UnescapeDataString(part).Replace("~1", "/").Replace("~0", "~");
                target = target switch
                {
                    JsonObject o => o[name],
                    JsonArray a when int.TryParse(name, out var i) && i < a.Count => a[i],
                    _ => null,
                };
            }
            schema = target;
        }
        return schema;
    }

    private Regex? Regex(string pattern)
    {
        if (_patterns.TryGetValue(pattern, out var regex))
            return regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException)
        {
            regex = null;
        }
        _patterns[pattern] = regex;
        return regex;
    }

    private static bool Matches(string type, DocNode node, DocumentFormat format) => type switch
    {
        "object" => node.IsContainer && node.Kind != NodeKind.Array,
        "array" => node.Kind == NodeKind.Array,
        "string" => !node.IsContainer && (node.ValueKind == ValueKind.String || format != DocumentFormat.Json),
        "number" => IsNumber(node, format),
        "integer" => IsNumber(node, format) && double.TryParse(Plain(node, format), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && Math.Floor(n) == n,
        "boolean" => !node.IsContainer && (node.ValueKind == ValueKind.Boolean || format != DocumentFormat.Json && Plain(node, format) is "true" or "false"),
        "null" => node.ValueKind == ValueKind.Null,
        _ => true,
    };

    private static bool IsNumber(DocNode node, DocumentFormat format) =>
        !node.IsContainer && (node.ValueKind == ValueKind.Number
            || format != DocumentFormat.Json && double.TryParse(Plain(node, format), NumberStyles.Float, CultureInfo.InvariantCulture, out _));

    private static bool Equal(JsonNode? expected, DocNode node, DocumentFormat format)
    {
        if (node.IsContainer)
            return false;
        if (expected == null)
            return node.ValueKind == ValueKind.Null;
        var text = Plain(node, format);
        if (expected is JsonValue v && v.TryGetValue<string>(out var s))
            return text == s && (node.ValueKind == ValueKind.String || format != DocumentFormat.Json);
        if (expected is JsonValue bv && bv.TryGetValue<bool>(out var flag))
            return text == (flag ? "true" : "false");
        if (expected is JsonValue nv && nv.TryGetValue<double>(out var number))
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n == number;
        return false;
    }

    /// <summary>A value's text as the schema should see it. INI values may be quoted, and the quotes aren't part of the value.</summary>
    private static string Plain(DocNode node, DocumentFormat format)
    {
        if (format != DocumentFormat.Ini)
            return node.Text;
        var t = node.Text.Trim();
        return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1] : t;
    }

    private static double? Number(JsonObject s, string key) =>
        s[key] is JsonValue v && v.TryGetValue<double>(out var n) ? n : null;

    /// <summary>What a node is, in schema terms. INI and XML text is described by what it looks like.</summary>
    private static string TypeOf(DocNode node, DocumentFormat format) => node.Kind switch
    {
        NodeKind.Array => "array",
        NodeKind.Object or NodeKind.Section or NodeKind.Element => "object",
        _ => (format == DocumentFormat.Json ? node.ValueKind : Formats.IniFormat.InferKind(Plain(node, format))) switch
        {
            ValueKind.Number => "number",
            ValueKind.Boolean => "boolean",
            ValueKind.Null => "null",
            _ => "string",
        },
    };

    private static string Name(DocNode node) =>
        node.Parent == null ? "The file" : node.Key == null ? $"Item {node.IndexInParent + 1}" : node.Key;

    private static string Article(string type) => type switch
    {
        "array" or "object" or "integer" => $"an {type}",
        "null" => "null",
        _ => $"a {type}",
    };

    private static string JoinOr(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list.Take(list.Count - 1))} or {list[^1]}";
    }

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
}
