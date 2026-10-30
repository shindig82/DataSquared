using System.IO;
using System.Linq;
using DataSquared.Core.Editing;
using DataSquared.Core.Model;
using DataSquared.Core.Schema;
using Xunit;

namespace DataSquared.Core.Tests;

public class SchemaTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    private static JsonSchema ServerSchema => JsonSchema.Parse(Fixture("server.schema.json"));

    [Fact]
    public void TheShippedServerConfigPasses()
    {
        var doc = Document.Parse(Fixture("server.cfg"), DocumentFormat.Ini);
        Assert.Empty(ServerSchema.Validate(doc));
    }

    [Fact]
    public void IniValuesAreCheckedByWhatTheyLookLike()
    {
        var editor = new DocumentEditor(Document.Parse(Fixture("server.cfg"), DocumentFormat.Ini));
        var root = editor.Document.Root;
        var server = root.Children.First(c => c.Key == "server");
        var port = root.Children.First(c => c.Key == "network").Children[0];

        editor.SetValue(server.Children.First(c => c.Key == "max_players"), "100");
        port.Text = "lots"; // As if typed into the text pane; the card itself won't take it.
        editor.SetValue(root.Children.First(c => c.Key == "match").Children.First(c => c.Key == "system"), "Arena");
        editor.Add(server, NodeKind.Value, ValueKind.String, key: "motd");

        var problems = ServerSchema.Validate(editor.Document).Select(p => (Key: p.Node.Key ?? "", p.Message)).ToList();
        Assert.Equal(new[]
        {
            ("max_players", "max_players should be at most 64."),
            ("motd", "server doesn't have a motd."),
            ("port", "port should be an integer, not a string."),
            ("system", "system doesn't match the pattern ^[a-z_]+:[a-z0-9_]+$."),
        }, problems.OrderBy(p => p.Key));
    }

    [Fact]
    public void RepeatedSectionsAreEachChecked()
    {
        var doc = Document.Parse(Fixture("server.cfg"), DocumentFormat.Ini);
        var second = doc.Root.Children.Where(c => c.Key == "network").Last();
        second.Children.First(c => c.Key == "aoi_range").Text = "9";

        var problem = Assert.Single(ServerSchema.Validate(doc));
        Assert.Same(second.Children.First(c => c.Key == "aoi_range"), problem.Node);
    }

    [Fact]
    public void MissingSectionsAreReportedOnTheFile()
    {
        var doc = Document.Parse("[server]\nmax_players = 8\n", DocumentFormat.Ini);
        var messages = ServerSchema.Validate(doc).Select(p => p.Message).ToList();
        Assert.Equal(new[] { "The file is missing network.", "The file is missing match." }, messages);
    }

    [Fact]
    public void JsonTypesAreStrict()
    {
        var schema = JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "port": { "type": "integer" },
            "name": { "type": "string", "minLength": 2 },
            "tags": { "type": "array", "items": { "enum": ["pvp", "pve"] }, "uniqueItems": true, "maxItems": 2 },
            "mode": { "oneOf": [ { "const": "fast" }, { "const": "slow" } ] }
          },
          "required": ["port"]
        }
        """);
        var doc = Document.Parse("""{ "port": "9000", "name": "x", "tags": ["pvp", "pvp", "co-op"], "mode": "fast" }""", DocumentFormat.Json);

        var messages = schema.Validate(doc).Select(p => p.Message).ToList();
        Assert.Equal(new[]
        {
            "port should be an integer, not a string.",
            "name needs at least 2 characters.",
            "tags can have at most 2 items.",
            "Item 3 should be one of \"pvp\", \"pve\".",
            "tags already has pvp.",
        }, messages);
    }

    [Fact]
    public void XmlAttributesAndElementsAreProperties()
    {
        var schema = JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "version": { "type": "integer" },
            "player": { "type": "object", "required": ["name"], "properties": { "level": { "type": "integer", "maximum": 99 } } }
          }
        }
        """);
        var doc = Document.Parse("<save version=\"x\"><player><level>120</level></player></save>", DocumentFormat.Xml);

        var messages = schema.Validate(doc).Select(p => p.Message).ToList();
        Assert.Equal(new[] { "version should be an integer, not a string.", "player is missing name.", "level should be at most 99." }, messages);
    }

    [Fact]
    public void DescriptionsAndChoicesAreFoundForANode()
    {
        var doc = Document.Parse(Fixture("server.cfg"), DocumentFormat.Ini);
        var match = doc.Root.Children.First(c => c.Key == "match");

        Assert.Equal("The star system the match is flown in.", ServerSchema.DescriptionOf(doc, match.Children.First(c => c.Key == "system")));
        Assert.Equal(new[] { "0", "1", "2", "3", "4", "5", "6" }, ServerSchema.ChoicesFor(doc, match.Children.First(c => c.Key == "teams")));
        Assert.Null(ServerSchema.ChoicesFor(doc, match.Children.First(c => c.Key == "ai_count")));
    }

    [Fact]
    public void ABrokenSchemaSaysSo()
    {
        Assert.Throws<System.FormatException>(() => JsonSchema.Parse("{ \"type\": "));
    }
}
