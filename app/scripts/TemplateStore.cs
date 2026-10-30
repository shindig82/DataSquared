using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DataSquared.Core.Templates;
using Godot;

namespace DataSquared;

/// <summary>
/// Saved templates, one .json file each in the user data folder, so they're there for every file.
/// </summary>
public static class TemplateStore
{
    private static string Dir => ProjectSettings.GlobalizePath("user://templates");

    public static List<Template> All()
    {
        var templates = new List<Template>();
        if (!Directory.Exists(Dir))
            return templates;
        foreach (var path in Directory.GetFiles(Dir, "*.json"))
        {
            try
            {
                templates.Add(Template.FromJson(File.ReadAllText(path)));
            }
            catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
            {
                GD.PushWarning($"Ignoring unreadable template {path}: {e.Message}");
            }
        }
        return templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>Saves a template, replacing one with the same name.</summary>
    public static void Save(Template template)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(PathFor(template.Name), template.ToJson());
    }

    public static void Delete(string name)
    {
        if (File.Exists(PathFor(name)))
            File.Delete(PathFor(name));
    }

    /// <summary>A file name for a template: its name with anything Windows or Linux won't take replaced.</summary>
    private static string PathFor(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        var safe = new StringBuilder();
        foreach (var c in name.Trim())
            safe.Append(bad.Contains(c) || char.IsControl(c) ? '_' : c);
        return Path.Combine(Dir, $"{safe.ToString().ToLowerInvariant()}.json");
    }
}
