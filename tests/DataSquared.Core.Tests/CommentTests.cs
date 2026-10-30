using System.IO;
using System.Linq;
using DataSquared.Core.Editing;
using DataSquared.Core.Model;
using Xunit;

namespace DataSquared.Core.Tests;

public class CommentTests
{
    private static DocumentEditor Open(string fixture, DocumentFormat format) =>
        new(Document.Parse(File.ReadAllText(Path.Combine("Fixtures", fixture)), format));

    [Fact]
    public void OnlyTheBlockDirectlyAboveCounts()
    {
        var editor = Open("server.cfg", DocumentFormat.Ini);
        var server = editor.Document.Root.Children[0];

        // The file header is separated from [server] by a blank line, so it isn't [server]'s comment.
        Assert.Equal("", server.Comment);
        Assert.Equal("The UDP port players connect to.", editor.Document.Root.Children[1].Children[0].Comment);
    }

    [Fact]
    public void IniCommentsAreWrittenWithSemicolonsAndKeepTheHeader()
    {
        var editor = Open("server.cfg", DocumentFormat.Ini);
        var server = editor.Document.Root.Children[0];
        var maxPlayers = server.Children.First(c => c.Key == "max_players");

        editor.SetComment(server, "Who can join.");
        editor.SetComment(maxPlayers, "Up to 64.\n\nKeep it low on small maps.");
        editor.SetComment(server.Children.First(c => c.Key == "name"), "");

        var written = editor.Document.Write();
        Assert.StartsWith("; FLEETBORNE dedicated server settings.", written);
        Assert.Contains("next starts.\n\n; Who can join.\n[server]\n", written);
        Assert.Contains("\n; Up to 64.\n;\n; Keep it low on small maps.\nmax_players = 16\n", written);
        Assert.Contains("[server]\n\nname = \n", written);
        Assert.Equal("Up to 64.\n\nKeep it low on small maps.", Document.Parse(written, DocumentFormat.Ini).Root.Children[0].Children.First(c => c.Key == "max_players").Comment);

        while (editor.Undo()) { }
        Assert.Equal(File.ReadAllText(Path.Combine("Fixtures", "server.cfg")), editor.Document.Write());
    }

    [Fact]
    public void JsonAndXmlUseTheirOwnStyle()
    {
        var json = new DocumentEditor(Document.Parse("{\n  \"a\": 1\n}\n", DocumentFormat.Json));
        json.SetComment(json.Document.Root.Children[0], "First line\nSecond line");
        Assert.Equal("{\n  // First line\n  // Second line\n  \"a\": 1\n}\n", json.Document.Write());

        var xml = Open("sample.xml", DocumentFormat.Xml);
        var root = xml.Document.Root;
        xml.SetComment(root, "Whole fleet -- all ships");
        xml.SetComment(root.Children.First(c => c.Key == "ship"), "First ship");
        var written = xml.Document.Write();
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- Whole fleet - - all ships -->\n<fleet", written);
        Assert.Contains("  <!-- First ship -->\n  <ship id=\"1\"", written);
        Assert.Equal("First ship", Document.Parse(written, DocumentFormat.Xml).Root.Children.First(c => c.Key == "ship").Comment);
    }
}
