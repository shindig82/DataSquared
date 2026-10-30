using System;
using System.IO;
using System.Linq;
using DataSquared.Core.Model;
using Xunit;

namespace DataSquared.Core.Tests;

public class JsonFormatTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    [Fact]
    public void IndentedFileWritesBackUnchanged()
    {
        var text = Fixture("sample.json");
        Assert.Equal(text, Document.Parse(text, DocumentFormat.Json).Write());
    }

    [Fact]
    public void MinifiedFileIsFormattedAndKeepsKeyOrderAndNumberText()
    {
        var doc = Document.Parse("{\"b\":1.50,\"a\":[1,2e3],\"c\":{}}", DocumentFormat.Json);
        Assert.Equal("{\n  \"b\": 1.50,\n  \"a\": [\n    1,\n    2e3\n  ],\n  \"c\": {}\n}\n", doc.Write());
    }

    [Fact]
    public void TypesPathsAndCommentsAreRead()
    {
        var doc = Document.Parse(Fixture("sample.json"), DocumentFormat.Json);
        var root = doc.Root;

        Assert.Equal("Shown in the title bar.", root.Children[0].Comment);
        Assert.Equal(ValueKind.Boolean, root.Children.First(c => c.Key == "active").ValueKind);
        Assert.Equal(ValueKind.Null, root.Descendants().First(c => c.Key == "email").ValueKind);
        Assert.Equal("Anonymous \"anon\"", root.Descendants().First(c => c.Key == "name").Text);
        Assert.Equal("$/items/1/label", root.Descendants().Last(c => c.Key == "label").Path);
    }

    [Fact]
    public void EditedStringIsEscapedOnSave()
    {
        var doc = Document.Parse("{\"a\": \"x\"}", DocumentFormat.Json);
        doc.Root.Children[0].Text = "line\n\"quoted\"\\";
        var written = doc.Write();
        Assert.Contains("\"a\": \"line\\n\\\"quoted\\\"\\\\\"", written);
        Assert.Equal("line\n\"quoted\"\\", Document.Parse(written, DocumentFormat.Json).Root.Children[0].Text);
    }

    [Fact]
    public void CommentBeforeCommaMovesToNextEntry()
    {
        var doc = Document.Parse("{\"a\": 1 /* about b */, \"b\": 2}", DocumentFormat.Json);
        Assert.Equal("about b", doc.Root.Children[1].Comment);
    }

    [Theory]
    [InlineData("{\"a\": }", "line 1, column 7")]
    [InlineData("{\"a\": 1,\n \"b\" 2}", "line 2, column 6")]
    [InlineData("[1, 2", "Missing ']'")]
    [InlineData("{} x", "after the end")]
    public void BadJsonReportsWhere(string text, string expected)
    {
        var ex = Assert.Throws<FormatException>(() => Document.Parse(text, DocumentFormat.Json));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void FormatIsDetectedFromExtensionThenContent()
    {
        Assert.Equal(DocumentFormat.Ini, Document.DetectFormat("server.cfg", "{"));
        Assert.Equal(DocumentFormat.Json, Document.DetectFormat("data.txt", "  [1]"));
        Assert.Equal(DocumentFormat.Ini, Document.DetectFormat("data.txt", "[section]\na=1"));
    }
}
