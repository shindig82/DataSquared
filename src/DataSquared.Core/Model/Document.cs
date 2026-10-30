using System;
using System.IO;
using System.Text.RegularExpressions;
using DataSquared.Core.Formats;

namespace DataSquared.Core.Model;

public enum DocumentFormat
{
    Json,
    Ini,
}

public sealed class Document
{
    public Document(DocumentFormat format, DocNode root)
    {
        Format = format;
        Root = root;
    }

    public DocumentFormat Format { get; }

    public DocNode Root { get; }

    /// <summary>The line ending the file was written with, kept so a save doesn't churn every line.</summary>
    public string Newline { get; set; } = "\n";

    public bool EndsWithNewline { get; set; } = true;

    public static Document Parse(string text, DocumentFormat format) => format switch
    {
        DocumentFormat.Json => JsonFormat.Read(text),
        DocumentFormat.Ini => IniFormat.Read(text),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public string Write() => Format switch
    {
        DocumentFormat.Json => JsonFormat.Write(this),
        DocumentFormat.Ini => IniFormat.Write(this),
        _ => throw new ArgumentOutOfRangeException(nameof(Format)),
    };

    public static Document Load(string path)
    {
        var text = File.ReadAllText(path);
        return Parse(text, DetectFormat(path, text));
    }

    public void Save(string path) => File.WriteAllText(path, Write());

    /// <summary>Picks a format from the file extension, falling back to the first character.</summary>
    public static DocumentFormat DetectFormat(string path, string text)
    {
        switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
        {
            case ".json":
                return DocumentFormat.Json;
            case ".cfg":
            case ".ini":
                return DocumentFormat.Ini;
        }

        var first = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (first.StartsWith('{'))
            return DocumentFormat.Json;
        if (!first.StartsWith('['))
            return DocumentFormat.Ini;

        // "[1, 2]" is JSON; a first line like "[server]" is an INI section header.
        var firstLine = first.Split('\n')[0].Trim();
        var header = Regex.Match(firstLine, @"^\[\s*([A-Za-z_][\w .:-]*?)\s*\]$");
        return header.Success && header.Groups[1].Value is not ("true" or "false" or "null")
            ? DocumentFormat.Ini
            : DocumentFormat.Json;
    }

    internal static string DetectNewline(string text) => text.Contains("\r\n") ? "\r\n" : "\n";
}
