using System;
using System.IO;
using System.Linq;
using DataSquared.Core.Platform;
using Microsoft.Win32;
using Xunit;

namespace DataSquared.Core.Tests;

public class FileAssociationTests
{
    [Fact]
    public void ExtensionsAreParsedFromAList()
    {
        Assert.Equal(FileAssociations.Extensions, FileAssociations.ParseExtensions(null));
        Assert.Equal(new[] { ".json", ".cfg" }, FileAssociations.ParseExtensions("JSON, .cfg,json"));
        var error = Assert.Throws<ArgumentException>(() => FileAssociations.ParseExtensions("json,txt"));
        Assert.Contains(".txt", error.Message);
    }

    [Fact]
    public void LinuxGetsADesktopEntryAndDefaults()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var home = Directory.CreateTempSubdirectory("datasquared-assoc-").FullName;
        try
        {
            var data = Path.Combine(home, "share");
            var config = Path.Combine(home, "config");
            Directory.CreateDirectory(config);
            // Someone else's defaults, which must survive.
            File.WriteAllText(Path.Combine(config, "mimeapps.list"), "[Default Applications]\ntext/plain=gedit.desktop;\napplication/json=code.desktop;\n\n[Added Associations]\nimage/png=gimp.desktop;\n");
            var assoc = new FileAssociations("/opt/My Apps/DataSquared.x86_64") { LinuxDataHome = data, LinuxConfigHome = config, NotifySystem = false };

            assoc.Register(FileAssociations.ParseExtensions("json,cfg"));

            var desktop = File.ReadAllText(Path.Combine(data, "applications", "datasquared.desktop"));
            Assert.Contains("Exec=\"/opt/My Apps/DataSquared.x86_64\" %f", desktop);
            Assert.Contains("MimeType=application/json;application/xml;text/xml;text/x-datasquared-ini;application/x-wine-extension-ini;text/x-datasquared-cfg;", desktop);
            Assert.DoesNotContain("*.ini", File.ReadAllText(Path.Combine(data, "mime", "packages", "datasquared.xml")));
            Assert.Contains("<glob pattern=\"*.cfg\"/>", File.ReadAllText(Path.Combine(data, "mime", "packages", "datasquared.xml")));
            Assert.Equal(
                "[Default Applications]\ntext/plain=gedit.desktop;\napplication/json=datasquared.desktop;\ntext/x-datasquared-cfg=datasquared.desktop;\n\n[Added Associations]\nimage/png=gimp.desktop;\n",
                File.ReadAllText(Path.Combine(config, "mimeapps.list")));

            assoc.Unregister();

            Assert.False(File.Exists(Path.Combine(data, "applications", "datasquared.desktop")));
            Assert.False(File.Exists(Path.Combine(data, "mime", "packages", "datasquared.xml")));
            Assert.Equal(
                "[Default Applications]\ntext/plain=gedit.desktop;\n\n[Added Associations]\nimage/png=gimp.desktop;\n",
                File.ReadAllText(Path.Combine(config, "mimeapps.list")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void WindowsGetsRegistryKeysAndPutsThePreviousAppBack()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var root = $@"Software\DataSquaredTests\{Guid.NewGuid():N}";
        var classesPath = root + @"\Classes";
        try
        {
            using (var json = Registry.CurrentUser.CreateSubKey(classesPath + @"\.json"))
                json.SetValue("", "VSCode.json");
            var assoc = new FileAssociations(@"C:\Program Files\DataSquared\DataSquared.exe") { WindowsClassesKey = classesPath, NotifySystem = false };

            assoc.Register(FileAssociations.Extensions);

            using (var classes = Registry.CurrentUser.OpenSubKey(classesPath)!)
            {
                Assert.Equal("\"C:\\Program Files\\DataSquared\\DataSquared.exe\" \"%1\"", classes.OpenSubKey($@"{FileAssociations.ProgId}\shell\open\command")!.GetValue(""));
                foreach (var ext in FileAssociations.Extensions)
                {
                    using var key = classes.OpenSubKey(ext)!;
                    Assert.Equal(FileAssociations.ProgId, key.GetValue(""));
                    Assert.Contains(FileAssociations.ProgId, key.OpenSubKey("OpenWithProgids")!.GetValueNames());
                }
                Assert.Contains(".cfg", classes.OpenSubKey(@"Applications\DataSquared.exe\SupportedTypes")!.GetValueNames());
            }

            assoc.Unregister();

            using (var classes = Registry.CurrentUser.OpenSubKey(classesPath)!)
            {
                Assert.Equal("VSCode.json", classes.OpenSubKey(".json")!.GetValue(""));
                Assert.Null(classes.OpenSubKey(".cfg")!.GetValue(""));
                Assert.Null(classes.OpenSubKey(FileAssociations.ProgId));
                Assert.DoesNotContain(FileAssociations.ProgId, classes.OpenSubKey(@".json\OpenWithProgids")!.GetValueNames());
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }
}
