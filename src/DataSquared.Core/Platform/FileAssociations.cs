using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DataSquared.Core.Platform;

/// <summary>
/// Makes DataSquared the app that opens data files, for the current user only, so no admin rights are
/// needed. On Windows it writes the usual keys under HKEY_CURRENT_USER\Software\Classes; on Linux it
/// installs a .desktop entry and tells xdg-mime to use it.
/// </summary>
public sealed class FileAssociations
{
    public static readonly IReadOnlyList<string> Extensions = new[] { ".json", ".xml", ".ini", ".cfg" };

    public const string ProgId = "DataSquared.DataFile";
    public const string DesktopFile = "datasquared.desktop";
    private const string MimePackage = "datasquared.xml";

    /// <summary>
    /// Linux MIME types by extension. Not every system knows *.ini, and none knows *.cfg, and those
    /// would otherwise count as plain text, so DataSquared declares a type for each. For .ini it also
    /// takes the name some systems already use.
    /// </summary>
    private static readonly Dictionary<string, string[]> MimeTypes = new()
    {
        [".json"] = new[] { "application/json" },
        [".xml"] = new[] { "application/xml", "text/xml" },
        // shared-mime-info names .ini files this way.
        [".ini"] = new[] { "text/x-datasquared-ini", "application/x-wine-extension-ini" },
        [".cfg"] = new[] { "text/x-datasquared-cfg" },
    };

    private readonly string _exePath;

    /// <param name="exePath">The program files should open with.</param>
    public FileAssociations(string exePath) => _exePath = Path.GetFullPath(exePath);

    /// <summary>Where Windows keys go, relative to HKEY_CURRENT_USER. Tests point it elsewhere.</summary>
    public string WindowsClassesKey { get; init; } = @"Software\Classes";

    /// <summary>The XDG data folder (~/.local/share), where the .desktop file and MIME types go.</summary>
    public string LinuxDataHome { get; init; } = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d
        ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    /// <summary>The XDG config folder (~/.config), which holds mimeapps.list.</summary>
    public string LinuxConfigHome { get; init; } = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c
        ? c
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    /// <summary>Whether to run xdg-mime and friends, or tell Windows' shell. Tests turn it off.</summary>
    public bool NotifySystem { get; init; } = true;

    /// <summary>
    /// Turns "json,cfg" or ".JSON" into known extensions, or all of them when nothing is given.
    /// Throws <see cref="ArgumentException"/> for one DataSquared doesn't open.
    /// </summary>
    public static IReadOnlyList<string> ParseExtensions(string? list)
    {
        if (string.IsNullOrWhiteSpace(list))
            return Extensions;
        var wanted = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => "." + e.TrimStart('.').ToLowerInvariant())
            .Distinct()
            .ToList();
        if (wanted.FirstOrDefault(e => !Extensions.Contains(e)) is { } unknown)
            throw new ArgumentException($"DataSquared doesn't open {unknown} files. It opens {string.Join(", ", Extensions)}.");
        return wanted;
    }

    /// <summary>Registers DataSquared for <paramref name="extensions"/>. Returns what was done, line by line, for the user.</summary>
    public IReadOnlyList<string> Register(IReadOnlyList<string> extensions)
    {
        if (OperatingSystem.IsWindows())
            return RegisterWindows(extensions);
        if (OperatingSystem.IsLinux())
            return RegisterLinux(extensions);
        throw new PlatformNotSupportedException("Setting the default app works on Windows and Linux.");
    }

    /// <summary>Undoes <see cref="Register"/> for every extension. Other apps' settings are left alone.</summary>
    public IReadOnlyList<string> Unregister()
    {
        if (OperatingSystem.IsWindows())
            return UnregisterWindows();
        if (OperatingSystem.IsLinux())
            return UnregisterLinux();
        throw new PlatformNotSupportedException("Setting the default app works on Windows and Linux.");
    }

    // ---- Windows ----

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<string> RegisterWindows(IReadOnlyList<string> extensions)
    {
        using var classes = Registry.CurrentUser.CreateSubKey(WindowsClassesKey);
        using (var prog = classes.CreateSubKey(ProgId))
        {
            prog.SetValue("", "DataSquared data file");
            using (var icon = prog.CreateSubKey("DefaultIcon"))
                icon.SetValue("", $"\"{_exePath}\",0");
            using (var command = prog.CreateSubKey(@"shell\open\command"))
                command.SetValue("", $"\"{_exePath}\" \"%1\"");
        }

        // So DataSquared shows in Open with, and in Default apps under Settings.
        using (var app = classes.CreateSubKey($@"Applications\{Path.GetFileName(_exePath)}"))
        {
            app.SetValue("FriendlyAppName", "DataSquared");
            using (var command = app.CreateSubKey(@"shell\open\command"))
                command.SetValue("", $"\"{_exePath}\" \"%1\"");
            using var supported = app.CreateSubKey("SupportedTypes");
            foreach (var ext in extensions)
                supported.SetValue(ext, "");
        }

        foreach (var ext in extensions)
        {
            using var key = classes.CreateSubKey(ext);
            // Remember what opened it before, so unregistering can put it back.
            if (key.GetValue("") is string previous && previous.Length > 0 && previous != ProgId)
                key.SetValue("DataSquared.Previous", previous);
            key.SetValue("", ProgId);
            using var progIds = key.CreateSubKey("OpenWithProgids");
            progIds.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        if (NotifySystem)
            NotifyShell();

        return new[]
        {
            $"DataSquared now opens {string.Join(", ", extensions)} files for {Environment.UserName}.",
            $"Program: {_exePath}",
            "If Windows still opens one with another app, that's a choice you made in Windows before, and Windows",
            "doesn't let programs change it. Right-click such a file, pick Open with > Choose another app >",
            "DataSquared, and tick Always use this app.",
        };
    }

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<string> UnregisterWindows()
    {
        using var classes = Registry.CurrentUser.OpenSubKey(WindowsClassesKey, writable: true);
        if (classes == null)
            return new[] { "DataSquared wasn't set to open any files." };

        var undone = new List<string>();
        foreach (var ext in Extensions)
        {
            using var key = classes.OpenSubKey(ext, writable: true);
            if (key == null)
                continue;
            using (var progIds = key.OpenSubKey("OpenWithProgids", writable: true))
                progIds?.DeleteValue(ProgId, throwOnMissingValue: false);
            if (key.GetValue("") as string == ProgId)
            {
                if (key.GetValue("DataSquared.Previous") is string previous)
                    key.SetValue("", previous);
                else
                    key.DeleteValue("", throwOnMissingValue: false);
                undone.Add(ext);
            }
            key.DeleteValue("DataSquared.Previous", throwOnMissingValue: false);
        }
        classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
        classes.DeleteSubKeyTree($@"Applications\{Path.GetFileName(_exePath)}", throwOnMissingSubKey: false);

        if (NotifySystem)
            NotifyShell();
        return new[] { undone.Count == 0 ? "DataSquared no longer appears for any file type." : $"DataSquared no longer opens {string.Join(", ", undone)} files." };
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>Tells Explorer file associations changed, so icons and Open with update straight away.</summary>
    private static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    // ---- Linux ----

    private string DesktopPath => Path.Combine(LinuxDataHome, "applications", DesktopFile);

    private string MimePackagePath => Path.Combine(LinuxDataHome, "mime", "packages", MimePackage);

    private IReadOnlyList<string> RegisterLinux(IReadOnlyList<string> extensions)
    {
        var types = extensions.SelectMany(e => MimeTypes[e]).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(DesktopPath)!);
        File.WriteAllText(DesktopPath, DesktopEntry(Extensions.SelectMany(e => MimeTypes[e])));

        var declared = OwnTypes.Where(t => extensions.Contains(t.Extension)).ToList();
        if (declared.Count > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MimePackagePath)!);
            File.WriteAllText(MimePackagePath, MimePackageXml(declared));
        }

        var notes = new List<string>
        {
            $"DataSquared now opens {string.Join(", ", extensions)} files for {Environment.UserName}.",
            $"Program: {_exePath}",
            $"Added {DesktopPath}",
        };

        if (NotifySystem)
        {
            if (File.Exists(MimePackagePath))
                Run("update-mime-database", Path.Combine(LinuxDataHome, "mime"), notes);
            Run("update-desktop-database", Path.GetDirectoryName(DesktopPath)!, notes);
            if (!Run("xdg-mime", $"default {DesktopFile} {string.Join(" ", types)}", notes))
            {
                // xdg-mime isn't everywhere; mimeapps.list is what it would have written.
                SetDefaultsInMimeApps(types);
                notes.Add($"Set the defaults in {MimeAppsPath}");
            }
        }
        else
        {
            SetDefaultsInMimeApps(types);
        }
        return notes;
    }

    private IReadOnlyList<string> UnregisterLinux()
    {
        var removed = new List<string>();
        foreach (var path in new[] { DesktopPath, MimePackagePath })
            if (File.Exists(path))
            {
                File.Delete(path);
                removed.Add(path);
            }
        RemoveFromMimeApps();

        var notes = new List<string>();
        if (NotifySystem)
        {
            Run("update-mime-database", Path.Combine(LinuxDataHome, "mime"), notes);
            Run("update-desktop-database", Path.GetDirectoryName(DesktopPath)!, notes);
        }
        notes.Insert(0, removed.Count == 0 ? "DataSquared wasn't set to open any files." : "DataSquared no longer opens any files. Your system's defaults apply again.");
        notes.AddRange(removed.Select(p => $"Removed {p}"));
        return notes;
    }

    private string DesktopEntry(IEnumerable<string> types)
    {
        // The Exec line quotes the path, as the spec asks for paths with spaces.
        var exec = _exePath.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$");
        return string.Join("\n", new[]
        {
            "[Desktop Entry]",
            "Type=Application",
            "Name=DataSquared",
            "Comment=Visual editor for JSON, XML and INI files",
            $"Exec=\"{exec}\" %f",
            "Terminal=false",
            "Categories=Development;Utility;",
            $"MimeType={string.Join(";", types.Distinct())};",
            "",
        });
    }

    /// <summary>The MIME types DataSquared declares itself, for extensions that have none everywhere.</summary>
    private static readonly (string Extension, string Type, string Comment)[] OwnTypes =
    {
        (".ini", "text/x-datasquared-ini", "INI file"),
        (".cfg", "text/x-datasquared-cfg", "Configuration file"),
    };

    private static string MimePackageXml(IEnumerable<(string Extension, string Type, string Comment)> types)
    {
        var lines = new List<string>
        {
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
            "<mime-info xmlns=\"http://www.freedesktop.org/standards/shared-mime-info\">",
        };
        foreach (var (extension, type, comment) in types)
        {
            lines.Add($"  <mime-type type=\"{type}\">");
            lines.Add($"    <comment>{comment}</comment>");
            lines.Add("    <sub-class-of type=\"text/plain\"/>");
            lines.Add($"    <glob pattern=\"*{extension}\"/>");
            lines.Add("  </mime-type>");
        }
        lines.Add("</mime-info>");
        lines.Add("");
        return string.Join("\n", lines);
    }

    private string MimeAppsPath => Path.Combine(LinuxConfigHome, "mimeapps.list");

    /// <summary>Writes [Default Applications] entries the way xdg-mime does, keeping everything else in the file.</summary>
    private void SetDefaultsInMimeApps(IReadOnlyList<string> types)
    {
        var lines = File.Exists(MimeAppsPath) ? File.ReadAllLines(MimeAppsPath).ToList() : new List<string>();
        var section = lines.FindIndex(l => l.Trim() == "[Default Applications]");
        if (section < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
                lines.Add("");
            lines.Add("[Default Applications]");
            section = lines.Count - 1;
        }
        var end = lines.FindIndex(section + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0)
            end = lines.Count;
        // New entries go after the section's last line, not after the blank line that ends it.
        while (end > section + 1 && lines[end - 1].Trim().Length == 0)
            end--;

        foreach (var type in types)
        {
            var at = lines.FindIndex(section + 1, end - section - 1, l => l.StartsWith(type + "="));
            if (at >= 0)
            {
                lines[at] = $"{type}={DesktopFile};";
            }
            else
            {
                lines.Insert(end, $"{type}={DesktopFile};");
                end++;
            }
        }
        Directory.CreateDirectory(LinuxConfigHome);
        File.WriteAllText(MimeAppsPath, string.Join("\n", lines) + "\n");
    }

    /// <summary>Takes datasquared.desktop out of mimeapps.list, dropping entries left with no app.</summary>
    private void RemoveFromMimeApps()
    {
        if (!File.Exists(MimeAppsPath))
            return;
        var kept = new List<string>();
        foreach (var line in File.ReadAllLines(MimeAppsPath))
        {
            var eq = line.IndexOf('=');
            if (eq < 0 || line.TrimStart().StartsWith('[') || line.TrimStart().StartsWith('#') || !line.Contains(DesktopFile))
            {
                kept.Add(line);
                continue;
            }
            var apps = line[(eq + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries).Where(a => a != DesktopFile).ToList();
            if (apps.Count > 0)
                kept.Add($"{line[..eq]}={string.Join(";", apps)};");
        }
        File.WriteAllText(MimeAppsPath, string.Join("\n", kept) + "\n");
    }

    /// <summary>Runs a helper tool. Returns false if it isn't installed or fails, with a note saying so.</summary>
    private static bool Run(string tool, string arguments, List<string> notes)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process == null)
                return false;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            if (process.ExitCode == 0)
                return true;
            notes.Add($"{tool} failed: {error.Trim()}");
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            notes.Add($"{tool} isn't installed, so that step was skipped.");
            return false;
        }
    }
}
