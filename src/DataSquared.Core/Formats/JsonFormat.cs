using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DataSquared.Core.Model;

namespace DataSquared.Core.Formats;

/// <summary>
/// JSON, plus the // and /* */ comments many game tools allow. Key order, number text and comments
/// are kept, and a save indents the way the file already was (two spaces for a minified file).
/// </summary>
public static class JsonFormat
{
    public static Document Read(string text)
    {
        var reader = new Reader(text);
        var leading = reader.SkipTrivia();
        var root = reader.ReadValue(null);
        root.LeadingTrivia.AddRange(leading);
        var trailing = reader.SkipTrivia();
        if (!reader.AtEnd)
            throw reader.Error("Unexpected text after the end of the document");

        var doc = new Document(DocumentFormat.Json, root)
        {
            Newline = Document.DetectNewline(text),
            Indent = DetectIndent(text),
            EndsWithNewline = text.Length == 0 || text.EndsWith('\n'),
        };
        root.TrailingTrivia.AddRange(trailing);
        return doc;
    }

    public static string Write(Document doc)
    {
        var w = new Writer(doc.Newline, doc.Indent);
        foreach (var line in doc.Root.LeadingTrivia)
            w.Line(line);
        w.Value(doc.Root, 0);
        if (doc.EndsWithNewline || doc.Root.TrailingTrivia.Count > 0)
            w.EndLine();
        foreach (var line in doc.Root.TrailingTrivia)
            w.Line(line);
        return w.ToString();
    }

    /// <summary>The whitespace before the first indented line, such as two spaces or a tab. Minified files get two spaces.</summary>
    private static string DetectIndent(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var line in lines.Skip(1))
        {
            var trimmed = line.TrimStart(' ', '\t');
            if (trimmed.Length > 0 && trimmed.Length < line.Length)
                return line[..(line.Length - trimmed.Length)];
        }
        return "  ";
    }

    public static ValueKind InferKind(string text) => text switch
    {
        "true" or "false" => ValueKind.Boolean,
        "null" => ValueKind.Null,
        _ when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) => ValueKind.Number,
        _ => ValueKind.String,
    };

    private sealed class Reader
    {
        private readonly string _s;
        private int _i;

        public Reader(string s)
        {
            _s = s;
            if (_s.StartsWith('\uFEFF'))
                _i = 1;
        }

        public bool AtEnd => _i >= _s.Length;

        /// <summary>Skips whitespace and returns any comments passed, one entry per line.</summary>
        public List<string> SkipTrivia()
        {
            var comments = new List<string>();
            while (_i < _s.Length)
            {
                var c = _s[_i];
                if (char.IsWhiteSpace(c))
                {
                    _i++;
                }
                else if (c == '/' && Peek(1) == '/')
                {
                    var end = _s.IndexOf('\n', _i);
                    if (end < 0) end = _s.Length;
                    comments.Add(_s[_i..end].TrimEnd('\r'));
                    _i = end;
                }
                else if (c == '/' && Peek(1) == '*')
                {
                    var end = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    if (end < 0) throw Error("Unclosed /* comment");
                    foreach (var line in _s[_i..(end + 2)].Replace("\r\n", "\n").Split('\n'))
                        comments.Add(line.Trim());
                    _i = end + 2;
                }
                else
                {
                    break;
                }
            }
            return comments;
        }

        public DocNode ReadValue(string? key)
        {
            if (AtEnd) throw Error("Unexpected end of file");
            switch (_s[_i])
            {
                case '{': return ReadContainer(key, NodeKind.Object, '}');
                case '[': return ReadContainer(key, NodeKind.Array, ']');
                case '"': return DocNode.Scalar(key, ValueKind.String, ReadString());
            }

            var start = _i;
            while (_i < _s.Length && !IsDelimiter(_s[_i]))
                _i++;
            var token = _s[start.._i];
            var kind = InferKind(token);
            if (kind == ValueKind.String)
                throw Error($"Unexpected '{token}'", start);
            return DocNode.Scalar(key, kind, token);
        }

        private DocNode ReadContainer(string? key, NodeKind kind, char close)
        {
            var node = DocNode.Container(kind, key);
            var carried = new List<string>();
            _i++;
            while (true)
            {
                var trivia = carried;
                trivia.AddRange(SkipTrivia());
                if (AtEnd) throw Error($"Missing '{close}'");
                if (_s[_i] == close)
                {
                    node.TrailingTrivia.AddRange(trivia);
                    _i++;
                    return node;
                }

                string? childKey = null;
                if (kind == NodeKind.Object)
                {
                    if (_s[_i] != '"') throw Error("Expected a quoted key");
                    childKey = ReadString();
                    SkipTrivia();
                    Expect(':');
                    trivia.AddRange(SkipTrivia());
                }

                var child = node.Add(ReadValue(childKey));
                child.LeadingTrivia.AddRange(trivia);

                var after = SkipTrivia();
                if (!AtEnd && _s[_i] == ',')
                {
                    _i++;
                    // A comment between a value and its comma belongs to the next entry.
                    carried = after;
                    continue;
                }
                node.TrailingTrivia.AddRange(after);
                if (AtEnd) throw Error($"Missing '{close}'");
                if (_s[_i] != close) throw Error($"Expected ',' or '{close}'");
            }
        }

        private string ReadString()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (true)
            {
                if (AtEnd) throw Error("Unclosed string");
                var c = _s[_i++];
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (AtEnd) throw Error("Unclosed string");
                var e = _s[_i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_i + 4 > _s.Length || !int.TryParse(_s.AsSpan(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            throw Error("Bad \\u escape");
                        sb.Append((char)code);
                        _i += 4;
                        break;
                    default:
                        throw Error($"Bad escape '\\{e}'", _i - 2);
                }
            }
        }

        private void Expect(char c)
        {
            if (AtEnd || _s[_i] != c) throw Error($"Expected '{c}'");
            _i++;
        }

        private char Peek(int offset) => _i + offset < _s.Length ? _s[_i + offset] : '\0';

        private static bool IsDelimiter(char c) => char.IsWhiteSpace(c) || c is ',' or ']' or '}' or ':' or '/';

        public FormatException Error(string message, int? at = null)
        {
            var pos = Math.Min(at ?? _i, _s.Length);
            var line = 1;
            var col = 1;
            for (var k = 0; k < pos; k++)
            {
                if (_s[k] == '\n') { line++; col = 1; }
                else col++;
            }
            return new FormatException($"{message} at line {line}, column {col}.");
        }
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

        private string IndentFor(int depth) => string.Concat(Enumerable.Repeat(_indent, depth));

        public void Line(string text) => _sb.Append(text).Append(_newline);

        public void EndLine() => _sb.Append(_newline);

        public void Value(DocNode node, int depth)
        {
            switch (node.Kind)
            {
                case NodeKind.Object:
                case NodeKind.Section:
                    Container(node, depth, '{', '}');
                    break;
                case NodeKind.Array:
                    Container(node, depth, '[', ']');
                    break;
                default:
                    _sb.Append(node.ValueKind == ValueKind.String ? Quote(node.Text) : node.Text);
                    break;
            }
        }

        private void Container(DocNode node, int depth, char open, char close)
        {
            if (node.Children.Count == 0 && node.TrailingTrivia.Count == 0)
            {
                _sb.Append(open).Append(close);
                return;
            }

            var indent = IndentFor(depth + 1);
            _sb.Append(open).Append(_newline);
            for (var k = 0; k < node.Children.Count; k++)
            {
                var child = node.Children[k];
                foreach (var comment in child.LeadingTrivia)
                    _sb.Append(indent).Append(comment).Append(_newline);
                _sb.Append(indent);
                if (node.Kind != NodeKind.Array)
                    _sb.Append(Quote(child.Key ?? "")).Append(": ");
                Value(child, depth + 1);
                if (k < node.Children.Count - 1)
                    _sb.Append(',');
                _sb.Append(_newline);
            }
            foreach (var comment in node.TrailingTrivia)
                _sb.Append(indent).Append(comment).Append(_newline);
            _sb.Append(IndentFor(depth)).Append(close);
        }

        public override string ToString() => _sb.ToString();
    }

    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
