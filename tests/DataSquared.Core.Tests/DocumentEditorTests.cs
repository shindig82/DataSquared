using System;
using System.IO;
using System.Linq;
using DataSquared.Core.Editing;
using DataSquared.Core.Model;
using Xunit;

namespace DataSquared.Core.Tests;

public class DocumentEditorTests
{
    private DateTime _now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    private DocumentEditor Json(string text) => new(Document.Parse(text, DocumentFormat.Json), () => _now);

    private DocumentEditor ServerConfig() =>
        new(Document.Parse(File.ReadAllText(Path.Combine("Fixtures", "server.cfg")), DocumentFormat.Ini), () => _now);

    private static DocNode Find(DocumentEditor editor, string path) =>
        editor.Document.Root.Descendants().Single(n => n.Path == path);

    [Fact]
    public void EveryKindOfEditUndoesBackToTheOriginalFile()
    {
        var original = File.ReadAllText(Path.Combine("Fixtures", "sample.json"));
        var editor = Json(original);
        var root = editor.Document.Root;

        editor.SetValue(Find(editor, "$/count"), "7");
        editor.Rename(Find(editor, "$/project"), "title");
        editor.Add(root, NodeKind.Array, key: "list");
        editor.Remove(Find(editor, "$/author/email"));
        editor.Move(Find(editor, "$/tags"), Find(editor, "$/items/0"), 0);
        editor.ChangeType(Find(editor, "$/author"), NodeKind.Array);
        editor.ChangeType(Find(editor, "$/active"), NodeKind.Value, ValueKind.String);

        Assert.NotEqual(original, editor.Document.Write());
        while (editor.Undo()) { }
        Assert.Equal(original, editor.Document.Write());
        Assert.False(editor.IsDirty);

        while (editor.Redo()) { }
        var redone = editor.Document.Write();
        Assert.Contains("\"title\": \"DataSquared\"", redone);
        Assert.Contains("\"active\": \"true\"", redone);
        Assert.Equal("$/items/0/tags", Find(editor, "$/items/0/tags").Path);
    }

    [Fact]
    public void TypingUndoesAsOneStepUntilAPause()
    {
        var editor = Json("{\"a\": \"\"}");
        var a = editor.Document.Root.Children[0];

        foreach (var text in new[] { "h", "he", "hel", "hell", "hello" })
        {
            editor.SetValue(a, text);
            _now += TimeSpan.FromMilliseconds(200);
        }
        _now += TimeSpan.FromSeconds(5);
        editor.SetValue(a, "hello!");

        editor.Undo();
        Assert.Equal("hello", a.Text);
        editor.Undo();
        Assert.Equal("", a.Text);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void SavingTracksDirtyStateAndStopsTypingMergingIntoTheSavedStep()
    {
        var editor = Json("{\"a\": \"\"}");
        var a = editor.Document.Root.Children[0];
        Assert.False(editor.IsDirty);

        editor.SetValue(a, "x");
        Assert.True(editor.IsDirty);
        editor.MarkSaved();
        Assert.False(editor.IsDirty);

        editor.SetValue(a, "xy");
        Assert.True(editor.IsDirty);
        editor.Undo();
        Assert.False(editor.IsDirty);
        Assert.Equal("x", a.Text);
    }

    [Fact]
    public void BadValuesAndNamesAreRefused()
    {
        var editor = Json("{\"n\": 1, \"b\": true, \"s\": \"x\", \"z\": null}");
        var root = editor.Document.Root;

        Assert.Throws<ArgumentException>(() => editor.SetValue(root.Children[0], "1.2.3"));
        Assert.Throws<ArgumentException>(() => editor.SetValue(root.Children[1], "yes"));
        Assert.Throws<ArgumentException>(() => editor.SetValue(root.Children[3], "x"));
        Assert.Throws<ArgumentException>(() => editor.Rename(root.Children[2], "n"));
        Assert.Throws<ArgumentException>(() => editor.Rename(root.Children[2], ""));
        Assert.Throws<InvalidOperationException>(() => editor.Add(root.Children[0], NodeKind.Value));
        Assert.Throws<InvalidOperationException>(() => editor.Add(root, NodeKind.Section));
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void AddedNodesGetUniqueNamesAndDefaults()
    {
        var editor = Json("{\"key\": 1, \"list\": []}");
        var root = editor.Document.Root;

        var added = editor.Add(root, NodeKind.Value, ValueKind.Number);
        var item = editor.Add(root.Children[1], NodeKind.Value, ValueKind.Boolean);

        Assert.Equal("key_2", added.Key);
        Assert.Equal("0", added.Text);
        Assert.Null(item.Key);
        Assert.Equal("{\"key\": 1, \"list\": [false], \"key_2\": 0}", Minify(editor.Document.Write()));
    }

    [Fact]
    public void MovingCantGoInsideItselfAndRenamesOnClash()
    {
        var editor = Json("{\"a\": {\"x\": 1}, \"b\": {\"x\": 2}}");
        var a = Find(editor, "$/a");

        Assert.Throws<InvalidOperationException>(() => editor.Move(a, Find(editor, "$/a"), 0));
        editor.Move(Find(editor, "$/b/x"), a, 1);
        Assert.Equal("{\"a\": {\"x\": 1, \"x_2\": 2}, \"b\": {}}", Minify(editor.Document.Write()));
    }

    [Fact]
    public void ChangingTypeConvertsWhatItCan()
    {
        var editor = Json("{\"s\": \"12\", \"o\": {\"a\": 1, \"b\": 2}, \"t\": true}");
        var root = editor.Document.Root;

        editor.ChangeType(root.Children[0], NodeKind.Value, ValueKind.Number);
        editor.ChangeType(root.Children[1], NodeKind.Array);
        editor.ChangeType(root.Children[2], NodeKind.Value, ValueKind.Number);
        Assert.Equal("{\"s\": 12, \"o\": [1, 2], \"t\": 1}", Minify(editor.Document.Write()));

        editor.ChangeType(root.Children[1], NodeKind.Object);
        Assert.Equal("{\"s\": 12, \"o\": {\"0\": 1, \"1\": 2}, \"t\": 1}", Minify(editor.Document.Write()));
    }

    [Fact]
    public void IniKeepsValuesAboveSectionsAndRefusesNesting()
    {
        var editor = ServerConfig();
        var root = editor.Document.Root;
        var server = root.Children.First(c => c.Key == "server");

        var top = editor.Add(root, NodeKind.Value, key: "motd");
        var section = editor.Add(root, NodeKind.Section, index: 0, key: "server");

        Assert.Equal(0, top.IndexInParent);
        Assert.Equal(1, section.IndexInParent);
        Assert.Throws<InvalidOperationException>(() => editor.Add(server, NodeKind.Object));
        Assert.Throws<InvalidOperationException>(() => editor.Add(server, NodeKind.Section));
        Assert.Throws<InvalidOperationException>(() => editor.Add(server, NodeKind.Value, ValueKind.Null));
        Assert.Throws<ArgumentException>(() => editor.Rename(server.Children[0], "a=b"));

        var reread = Document.Parse(editor.Document.Write(), DocumentFormat.Ini);
        Assert.Equal("motd", reread.Root.Children[0].Key);
        Assert.Equal(NodeKind.Value, reread.Root.Children[0].Kind);
        Assert.Equal(2, reread.Root.Children.Count(c => c.Key == "server"));
    }

    [Fact]
    public void IniEditWritesAReadableLineAndUndoRestoresTheFile()
    {
        var original = File.ReadAllText(Path.Combine("Fixtures", "server.cfg"));
        var editor = ServerConfig();
        var steam = editor.Document.Root.Children.Last();

        var added = editor.Add(steam, NodeKind.Value, ValueKind.Boolean, key: "vac");
        editor.SetValue(added, "true");
        editor.Move(steam.Children[0], editor.Document.Root.Children.First(c => c.Key == "logging"), 1);

        var written = editor.Document.Write();
        Assert.EndsWith("query_port = 27016\nvac = true\n", written);
        Assert.Contains("log_state = false\n\n; List this server on Steam's server browser, so pilots can find it. Open the query port as well as the game port.\nlist = false\n", written);

        while (editor.Undo()) { }
        Assert.Equal(original, editor.Document.Write());
    }

    [Fact]
    public void ChangedFiresForEditsUndoAndRedo()
    {
        var editor = Json("{\"a\": 1}");
        var count = 0;
        editor.Changed += () => count++;

        editor.SetValue(editor.Document.Root.Children[0], "2");
        editor.Undo();
        editor.Redo();
        editor.SetValue(editor.Document.Root.Children[0], "2");

        Assert.Equal(3, count);
    }

    private static string Minify(string json)
    {
        var doc = Document.Parse(json, DocumentFormat.Json);
        return Flat(doc.Root);

        static string Flat(DocNode n) => n.Kind switch
        {
            NodeKind.Object => "{" + string.Join(", ", n.Children.Select(c => $"\"{c.Key}\": {Flat(c)}")) + "}",
            NodeKind.Array => "[" + string.Join(", ", n.Children.Select(Flat)) + "]",
            _ => n.ValueKind == ValueKind.String ? $"\"{n.Text}\"" : n.Text,
        };
    }
}
