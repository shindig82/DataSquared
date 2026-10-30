using System;
using System.IO;
using System.Linq;
using DataSquared.Core.Editing;
using DataSquared.Core.Model;
using DataSquared.Core.Templates;
using Xunit;

namespace DataSquared.Core.Tests;

public class TemplateTests
{
    private static DocumentEditor Open(string fixture, DocumentFormat format) =>
        new(Document.Parse(File.ReadAllText(Path.Combine("Fixtures", fixture)), format));

    private static DocumentEditor Edit(string text, DocumentFormat format) => new(Document.Parse(text, format));

    [Fact]
    public void ASectionGoesBackInWithItsCommentsAndANewName()
    {
        var editor = Open("server.cfg", DocumentFormat.Ini);
        var network = editor.Document.Root.Children.First(c => c.Key == "network");
        var template = Template.FromNode("Network", network, DocumentFormat.Ini);

        editor.Insert(editor.Document.Root, template.Instantiate(DocumentFormat.Ini));

        // Sections may repeat in INI, so it keeps its name.
        Assert.EndsWith("query_port = 27016\n\n[network]\n; The UDP port players connect to.\nport = 9000\n", editor.Document.Write());
        editor.Undo();
        Assert.Equal(File.ReadAllText(Path.Combine("Fixtures", "server.cfg")), editor.Document.Write());
    }

    [Fact]
    public void AnIniSectionBecomesAJsonObject()
    {
        var ini = Open("server.cfg", DocumentFormat.Ini);
        var template = Template.FromNode("Server", ini.Document.Root.Children.First(c => c.Key == "server"), DocumentFormat.Ini);
        var json = Edit("{\n  \"server\": {}\n}\n", DocumentFormat.Json);

        var added = json.Insert(json.Document.Root, template.Instantiate(DocumentFormat.Json));

        Assert.Equal("server_2", added.Key);
        Assert.Equal(NodeKind.Object, added.Kind);
        var written = json.Document.Write();
        Assert.Contains("// The most pilots in the arena at once, from 1 to 64.", written);
        Assert.Contains("\"max_players\": 16", written);
        Assert.Equal(written, Document.Parse(written, DocumentFormat.Json).Write());
    }

    [Fact]
    public void AnXmlElementBecomesAJsonObjectAndBack()
    {
        var xml = Edit("<ships><ship id=\"7\" hull=\"light\"><!-- Fast. --><speed>12</speed></ship></ships>", DocumentFormat.Xml);
        var template = Template.FromNode("Ship", xml.Document.Root.Children[0], DocumentFormat.Xml);

        var json = Edit("{}", DocumentFormat.Json);
        var ship = json.Insert(json.Document.Root, template.Instantiate(DocumentFormat.Json));
        Assert.Equal(new[] { "id", "hull", "speed" }, ship.Children.Select(c => c.Key));
        Assert.All(ship.Children, c => Assert.Equal(NodeKind.Value, c.Kind));
        Assert.Equal("Fast.", ship.Children[2].Comment);

        xml.Insert(xml.Document.Root, template.Instantiate(DocumentFormat.Xml));
        Assert.Equal(2, xml.Document.Root.Children.Count(c => c.Key == "ship"));
        Assert.Contains("<!-- Fast. -->", xml.Document.Write());
    }

    [Fact]
    public void WhatTheFormatCantHoldIsRefused()
    {
        var json = Edit("{ \"a\": { \"b\": { \"c\": 1 } }, \"list\": [1] }", DocumentFormat.Json);
        var nested = Template.FromNode("Nested", json.Document.Root.Children[0], DocumentFormat.Json);
        var list = Template.FromNode("List", json.Document.Root.Children[1], DocumentFormat.Json);
        var ini = Edit("[server]\nport = 1\n", DocumentFormat.Ini);

        Assert.Equal("INI files can't nest objects or arrays.", ini.CanInsert(ini.Document.Root, nested.Instantiate(DocumentFormat.Ini)));
        Assert.Equal("INI files can't nest objects or arrays.", ini.CanInsert(ini.Document.Root.Children[0], list.Instantiate(DocumentFormat.Ini)));
        Assert.Throws<InvalidOperationException>(() => ini.Insert(ini.Document.Root, nested.Instantiate(DocumentFormat.Ini)));
        Assert.False(ini.IsDirty);

        // A plain key-value object is fine as a section.
        var flat = Template.FromNode("Flat", json.Document.Root.Children[0].Children[0], DocumentFormat.Json);
        Assert.Null(ini.CanInsert(ini.Document.Root, flat.Instantiate(DocumentFormat.Ini)));
    }

    [Fact]
    public void ValuesGoIntoArraysWithoutNames()
    {
        var json = Edit("{ \"port\": 9000, \"ports\": [] }", DocumentFormat.Json);
        var template = Template.FromNode("Port", json.Document.Root.Children[0], DocumentFormat.Json);

        json.Insert(json.Document.Root.Children[1], template.Instantiate(DocumentFormat.Json));

        Assert.Contains("[9000]", json.Document.Write().Replace(" ", "").Replace("\n", ""));
    }

    [Fact]
    public void ATemplateSurvivesSavingAndLoading()
    {
        var editor = Open("server.cfg", DocumentFormat.Ini);
        var match = editor.Document.Root.Children.First(c => c.Key == "match");
        var template = Template.FromNode("  Match defaults ", match, DocumentFormat.Ini);

        var loaded = Template.FromJson(template.ToJson());

        Assert.Equal("Match defaults", loaded.Name);
        Assert.Equal(DocumentFormat.Ini, loaded.Format);
        var a = Edit("", DocumentFormat.Ini);
        var b = Edit("", DocumentFormat.Ini);
        a.Insert(a.Document.Root, template.Instantiate(DocumentFormat.Ini));
        b.Insert(b.Document.Root, loaded.Instantiate(DocumentFormat.Ini));
        Assert.Equal(a.Document.Write(), b.Document.Write());
        Assert.Contains("friendly_fire = false", b.Document.Write());
        Assert.Throws<FormatException>(() => Template.FromJson("{ \"name\": \"x\" }"));
    }
}
