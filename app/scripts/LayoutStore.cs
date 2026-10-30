using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;

namespace DataSquared;

/// <summary>
/// Remembers where cards were placed, and which schema was picked, per file. Kept in the user data
/// folder rather than next to the file, so data files in a game repo stay untouched.
/// </summary>
public static class LayoutStore
{
    private sealed class Layout
    {
        public int Version { get; set; } = 1;
        public string File { get; set; } = "";
        public float Zoom { get; set; } = 1f;
        public float[] Scroll { get; set; } = { 0, 0 };
        public Dictionary<string, float[]> Cards { get; set; } = new();
        public string? Schema { get; set; }
    }

    public sealed record Saved(Dictionary<string, Vector2> Cards, float Zoom, Vector2 Scroll);

    public static Saved? Load(string filePath)
    {
        var layout = Read(filePath);
        if (layout == null)
            return null;
        var cards = new Dictionary<string, Vector2>();
        foreach (var (key, xy) in layout.Cards)
            if (xy.Length == 2)
                cards[key] = new Vector2(xy[0], xy[1]);
        return new Saved(cards, layout.Zoom, new Vector2(layout.Scroll[0], layout.Scroll[1]));
    }

    public static void Save(string filePath, IReadOnlyDictionary<string, Vector2> cards, float zoom, Vector2 scroll)
    {
        var layout = Read(filePath) ?? new Layout();
        layout.Zoom = zoom;
        layout.Scroll = new[] { scroll.X, scroll.Y };
        layout.Cards.Clear();
        foreach (var (key, pos) in cards)
            layout.Cards[key] = new[] { MathF.Round(pos.X), MathF.Round(pos.Y) };
        Write(filePath, layout);
    }

    /// <summary>The schema picked for this file, if one was.</summary>
    public static string? LoadSchema(string filePath) => Read(filePath)?.Schema;

    public static void SaveSchema(string filePath, string? schemaPath)
    {
        var layout = Read(filePath) ?? new Layout();
        layout.Schema = schemaPath;
        Write(filePath, layout);
    }

    private static Layout? Read(string filePath)
    {
        try
        {
            var path = LayoutPath(filePath);
            return System.IO.File.Exists(path) ? JsonSerializer.Deserialize<Layout>(System.IO.File.ReadAllText(path)) : null;
        }
        catch (Exception e)
        {
            GD.PushWarning($"Ignoring unreadable layout for {filePath}: {e.Message}");
            return null;
        }
    }

    private static void Write(string filePath, Layout layout)
    {
        layout.File = Path.GetFullPath(filePath);
        var path = LayoutPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string LayoutPath(string filePath)
    {
        // Windows paths are case-insensitive, so fold case there to find the same layout either way.
        var full = Path.GetFullPath(filePath);
        if (OperatingSystem.IsWindows())
            full = full.ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16].ToLowerInvariant();
        var dir = ProjectSettings.GlobalizePath("user://layouts");
        return Path.Combine(dir, $"{Path.GetFileName(filePath)}.{hash}.json");
    }
}
