using System.IO;
using System.Linq;
using DataSquared.Core.Model;
using Xunit;

namespace DataSquared.Core.Tests;

public class IniFormatTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    [Fact]
    public void UneditedServerConfigWritesBackByteForByte()
    {
        var text = Fixture("server.cfg");
        Assert.Equal(text, Document.Parse(text, DocumentFormat.Ini).Write());
    }

    [Fact]
    public void WindowsLineEndingsAreKept()
    {
        var text = Fixture("server.cfg").Replace("\n", "\r\n");
        var doc = Document.Parse(text, DocumentFormat.Ini);
        Assert.Equal("\r\n", doc.Newline);
        Assert.Equal(text, doc.Write());
    }

    [Fact]
    public void RepeatedSectionsStaySeparate()
    {
        var doc = Document.Parse(Fixture("server.cfg"), DocumentFormat.Ini);
        var network = doc.Root.Children.Where(c => c.Key == "network").ToList();

        Assert.Equal(2, network.Count);
        Assert.Equal("$/network", network[0].Path);
        Assert.Equal("$/network#1", network[1].Path);
        Assert.Equal("$/network#1/aoi_range", network[1].Children.Last().Path);
    }

    [Fact]
    public void ValuesGetTypesAndComments()
    {
        var doc = Document.Parse(Fixture("server.cfg"), DocumentFormat.Ini);
        var server = doc.Root.Children.First(c => c.Key == "server");
        var maxPlayers = server.Children.First(c => c.Key == "max_players");
        var name = server.Children.First(c => c.Key == "name");
        var match = doc.Root.Children.First(c => c.Key == "match");

        Assert.Equal(ValueKind.Number, maxPlayers.ValueKind);
        Assert.Equal("16", maxPlayers.Text);
        Assert.Equal("The most pilots in the arena at once, from 1 to 64.", maxPlayers.Comment);
        Assert.Equal("", name.Text);
        Assert.Equal(ValueKind.Boolean, match.Children.First(c => c.Key == "friendly_fire").ValueKind);
        Assert.Equal(ValueKind.String, match.Children.First(c => c.Key == "system").ValueKind);
    }

    [Fact]
    public void EditingAValueChangesOnlyThatLine()
    {
        var text = Fixture("server.cfg");
        var doc = Document.Parse(text, DocumentFormat.Ini);
        var server = doc.Root.Children.First(c => c.Key == "server");
        server.Children.First(c => c.Key == "max_players").Text = "32";
        server.Children.First(c => c.Key == "name").Text = "Fleetborne EU";

        var expected = text.Replace("max_players = 16", "max_players = 32").Replace("name = \n", "name = Fleetborne EU\n");
        Assert.Equal(expected, doc.Write());
    }

    [Fact]
    public void KeysBeforeAnySectionAndUnknownLinesAreKept()
    {
        const string text = "top = 1\n  odd line\n\n[a]\nb=2\n; end\n";
        var doc = Document.Parse(text, DocumentFormat.Ini);
        Assert.Equal("top", doc.Root.Children[0].Key);
        Assert.Equal(text, doc.Write());
    }

    [Fact]
    public void FileWithoutFinalNewlineIsKept()
    {
        const string text = "[a]\nb = 2";
        Assert.Equal(text, Document.Parse(text, DocumentFormat.Ini).Write());
    }
}
