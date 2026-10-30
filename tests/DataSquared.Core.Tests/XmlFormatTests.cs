using System;
using System.IO;
using System.Linq;
using DataSquared.Core.Editing;
using DataSquared.Core.Model;
using Xunit;

namespace DataSquared.Core.Tests;

public class XmlFormatTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    private static Document Sample() => Document.Parse(Fixture("sample.xml"), DocumentFormat.Xml);

    [Fact]
    public void ElementsAttributesAndTextBecomeNodes()
    {
        var root = Sample().Root;

        Assert.Equal(NodeKind.Element, root.Kind);
        Assert.Equal("fleet", root.Key);
        Assert.Equal(new[] { "name", "version" }, root.Children.Where(c => c.Kind == NodeKind.Attribute).Select(c => c.Key));

        var ship = root.Children.First(c => c.Key == "ship");
        Assert.Equal("Ships the server spawns.", ship.Comment);
        Assert.Equal(NodeKind.Element, ship.Kind);
        var speed = ship.Children.First(c => c.Key == "speed");
        Assert.Equal(NodeKind.Value, speed.Kind);
        Assert.Equal(ValueKind.Number, speed.ValueKind);
        Assert.Equal(ValueKind.Boolean, ship.Children.First(c => c.Key == "armed").ValueKind);
        Assert.Equal("", ship.Children.First(c => c.Key == "cargo").Text);

        Assert.Equal("Escort & guard", root.Children.Where(c => c.Key == "ship").Last().Children.First(c => c.Key == "label").Text);
        Assert.Equal("$/ship#1/label", root.Children.Where(c => c.Key == "ship").Last().Children.First(c => c.Key == "label").Path);

        var note = root.Children.First(c => c.Key == "note");
        Assert.Equal(NodeKind.Element, note.Kind);
        Assert.Equal("Spawn escorts last", note.Children.Single(c => c.Key == DocNode.TextKey).Text);

        var description = root.Children.First(c => c.Key == "description");
        Assert.Equal(new[] { DocNode.TextKey, "b", DocNode.TextKey }, description.Children.Select(c => c.Key));
    }

    [Fact]
    public void SavingKeepsDeclarationCommentsAndOrder()
    {
        var written = Sample().Write();

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- Fleet loadout for the arena. -->\n<fleet name=\"Arena\" version=\"2\">\n  <!-- Ships the server spawns. -->\n  <ship id=\"1\"", written);
        Assert.Contains("    <label>Escort &amp; guard</label>\n", written);
        Assert.Contains("    <cargo/>\n", written);
        Assert.Contains("  <note priority=\"high\">Spawn escorts last</note>\n", written);
        Assert.EndsWith("</fleet>\n", written);

        // Saving what was saved changes nothing.
        Assert.Equal(written, Document.Parse(written, DocumentFormat.Xml).Write());
    }

    [Fact]
    public void UnchangedWellFormattedFileWritesBackExactly()
    {
        const string text = "<config>\n\t<port>9000</port>\n\t<!-- off by default -->\n\t<debug enabled=\"false\"/>\n</config>\n";
        Assert.Equal(text, Document.Parse(text, DocumentFormat.Xml).Write());
    }

    [Fact]
    public void BadXmlReportsWhere()
    {
        var ex = Assert.Throws<FormatException>(() => Document.Parse("<a>\n  <b>\n</a>", DocumentFormat.Xml));
        Assert.Contains("line 3", ex.Message);
    }

    [Fact]
    public void FormatIsDetected()
    {
        Assert.Equal(DocumentFormat.Xml, Document.DetectFormat("fleet.xml", "{"));
        Assert.Equal(DocumentFormat.Xml, Document.DetectFormat("fleet.data", "  <fleet/>"));
    }

    [Fact]
    public void EditingFollowsXmlRules()
    {
        var editor = new DocumentEditor(Sample());
        var root = editor.Document.Root;
        var ship = root.Children.First(c => c.Key == "ship");

        // Attributes go before content, and must be unique; elements may repeat.
        var attr = editor.Add(ship, NodeKind.Attribute, key: "crew");
        Assert.Equal(2, attr.IndexInParent);
        Assert.Throws<ArgumentException>(() => editor.Rename(attr, "id"));
        Assert.Throws<ArgumentException>(() => editor.Rename(attr, "not valid"));
        var label = editor.Add(ship, NodeKind.Value, key: "label");
        Assert.Equal(2, ship.Children.Count(c => c.Key == "label"));

        // Repeated elements keep their name when duplicated.
        var copy = editor.Duplicate(ship);
        Assert.Equal("ship", copy.Key);

        Assert.Throws<InvalidOperationException>(() => editor.Add(ship, NodeKind.Object));
        Assert.Throws<InvalidOperationException>(() => editor.Add(ship, NodeKind.Value, ValueKind.Null));
        Assert.Throws<InvalidOperationException>(() => editor.ChangeType(attr, NodeKind.Element));
        Assert.Throws<ArgumentException>(() => editor.Rename(root.Children.First(c => c.Key == "description").Children[0], "x"));

        // The root element can be renamed.
        editor.Rename(root, "squadron");

        // A text-only element turned into an element keeps its text.
        var speed = ship.Children.First(c => c.Key == "speed");
        editor.ChangeType(speed, NodeKind.Element);
        editor.Add(speed, NodeKind.Attribute, key: "unit");

        var written = editor.Document.Write();
        Assert.Contains("<squadron name=\"Arena\" version=\"2\">", written);
        Assert.Contains("<ship id=\"1\" hull=\"core:hull_light_corvette\" crew=\"\">", written);
        Assert.Contains("<speed unit=\"\">12.5</speed>", written);
        Assert.Equal(written, Document.Parse(written, DocumentFormat.Xml).Write());

        while (editor.Undo()) { }
        Assert.Equal(Sample().Write(), editor.Document.Write());
    }
}
