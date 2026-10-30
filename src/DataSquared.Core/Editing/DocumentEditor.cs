using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DataSquared.Core.Model;

namespace DataSquared.Core.Editing;

/// <summary>
/// The only way the app changes a document. Every change is checked against the format's rules,
/// applied, and kept for undo and redo.
/// </summary>
public sealed class DocumentEditor
{
    /// <summary>Keystrokes on one value closer together than this undo as one step.</summary>
    public static readonly TimeSpan TypingPause = TimeSpan.FromSeconds(1.5);

    private readonly List<Edit> _undo = new();
    private readonly List<Edit> _redo = new();
    private readonly Func<DateTime> _clock;
    private Edit? _saved;

    public DocumentEditor(Document document, Func<DateTime>? clock = null)
    {
        Document = document;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public Document Document { get; }

    /// <summary>Raised after every change, undo and redo.</summary>
    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoDescription => _undo.LastOrDefault()?.Description;

    public string? RedoDescription => _redo.LastOrDefault()?.Description;

    /// <summary>True when the document differs from when it was loaded or last saved.</summary>
    public bool IsDirty => _undo.LastOrDefault() != _saved;

    public void MarkSaved() => _saved = _undo.LastOrDefault();

    public bool Undo()
    {
        if (!CanUndo)
            return false;
        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        edit.Revert();
        _redo.Add(edit);
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo)
            return false;
        var edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        edit.Apply();
        _undo.Add(edit);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Why <paramref name="text"/> can't be this value, or null if it can.</summary>
    public static string? ValidateValue(DocNode node, string text) => node.ValueKind switch
    {
        _ when node.Kind != NodeKind.Value => "Only values have text.",
        ValueKind.Number when !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) => "Not a number.",
        ValueKind.Boolean when text.Trim() is not ("true" or "false") => "Must be true or false.",
        ValueKind.Null when text != "null" => "Change the type to give this a value.",
        _ => null,
    };

    public void SetValue(DocNode node, string text)
    {
        if (ValidateValue(node, text) is { } error)
            throw new ArgumentException(error, nameof(text));
        if (node.Text == text)
            return;

        var edit = new SetValueEdit(node, text) { At = _clock() };
        if (_undo.LastOrDefault() is SetValueEdit last && last != _saved && last.Node == node && edit.At - last.At < TypingPause && _redo.Count == 0)
        {
            edit.Apply();
            last.Absorb(edit);
            Changed?.Invoke();
            return;
        }
        Do(edit);
    }

    /// <summary>Why <paramref name="key"/> can't name this node, or null if it can.</summary>
    public string? ValidateKey(DocNode node, string key) =>
        node.Parent == null || node.Parent.Kind == NodeKind.Array
            ? "Array items and the root have no name."
            : KeyProblem(node.Parent, node, node.Kind, key);

    public void Rename(DocNode node, string key)
    {
        if (ValidateKey(node, key) is { } error)
            throw new ArgumentException(error, nameof(key));
        if (node.Key != key)
            Do(new RenameEdit(node, key));
    }

    /// <summary>Adds a new, empty node to <paramref name="parent"/>. Returns it.</summary>
    public DocNode Add(DocNode parent, NodeKind kind, ValueKind valueKind = ValueKind.String, int? index = null, string? key = null)
    {
        if (FormatRules.CannotHold(Document.Format, parent, kind, valueKind) is { } error)
            throw new InvalidOperationException(error);

        var node = kind == NodeKind.Value
            ? DocNode.Scalar(null, valueKind, DefaultText(valueKind))
            : DocNode.Container(kind);
        if (Document.Format == DocumentFormat.Ini && kind == NodeKind.Value)
            node.Separator = " = ";

        var newKey = parent.Kind == NodeKind.Array ? null : key ?? UniqueKey(parent, kind == NodeKind.Section ? "section" : "key");
        if (newKey != null && KeyProblem(parent, null, kind, newKey) is { } keyError)
            throw new ArgumentException(keyError, nameof(key));

        var at = FormatRules.ClampIndex(Document.Format, parent, kind, index ?? parent.Children.Count);
        Do(new PlaceEdit($"Add {newKey ?? "item"}", node, parent, at, newKey));
        return node;
    }

    public void Remove(DocNode node)
    {
        if (node.Parent == null)
            throw new InvalidOperationException("The root can't be removed.");
        Do(new PlaceEdit($"Delete {node.Key ?? "item"}", node, null, 0, node.Key));
    }

    /// <summary>
    /// Moves a node to <paramref name="index"/> in <paramref name="newParent"/>, counted as if the node
    /// were already taken out. Moving into an array drops the name; moving into an object keeps it,
    /// or makes it unique if it clashes.
    /// </summary>
    public void Move(DocNode node, DocNode newParent, int index)
    {
        if (node.Parent == null)
            throw new InvalidOperationException("The root can't be moved.");
        if (node == newParent || node.IsAncestorOf(newParent))
            throw new InvalidOperationException("A node can't be moved inside itself.");
        if (FormatRules.CannotHold(Document.Format, newParent, node.Kind, node.ValueKind) is { } error)
            throw new InvalidOperationException(error);

        string? key = null;
        if (newParent.Kind != NodeKind.Array)
        {
            var wanted = node.Key ?? "key";
            var clash = node.Kind != NodeKind.Section && newParent.Children.Any(c => c != node && c.Key == wanted && c.Kind != NodeKind.Section);
            key = clash ? UniqueKey(newParent, wanted) : wanted;
        }

        var count = newParent.Children.Count - (node.Parent == newParent ? 1 : 0);
        var at = Math.Clamp(index, 0, count);
        if (node.Parent == newParent && at == node.IndexInParent && key == node.Key)
            return;

        // Clamp against the parent as it will be with the node taken out.
        var oldParent = node.Parent;
        var oldIndex = node.IndexInParent;
        oldParent.Remove(node);
        at = FormatRules.ClampIndex(Document.Format, newParent, node.Kind, at);
        oldParent.Insert(oldIndex, node);

        Do(new PlaceEdit($"Move {node.Key ?? "item"}", node, newParent, at, key));
    }

    /// <summary>
    /// Changes a node's type, converting what it can: "1" stays 1 as a number, an object's values become
    /// an array's items, and so on. Anything that can't carry over is dropped, and comes back on undo.
    /// </summary>
    public void ChangeType(DocNode node, NodeKind kind, ValueKind valueKind = ValueKind.None)
    {
        if (node.Parent == null)
            throw new InvalidOperationException("The root's type can't be changed.");
        if (kind == NodeKind.Value && valueKind == ValueKind.None)
            throw new ArgumentException("Say which kind of value.", nameof(valueKind));
        if (node.Kind == kind && node.ValueKind == valueKind)
            return;
        if (node.Kind == NodeKind.Section || kind == NodeKind.Section)
            throw new InvalidOperationException("Sections can't change type.");
        if (FormatRules.CannotHold(Document.Format, node.Parent, kind, valueKind) is { } error)
            throw new InvalidOperationException(error);

        var children = new List<(DocNode, string?)>();
        var text = "";
        if (kind == NodeKind.Value)
        {
            text = ConvertText(node, valueKind);
        }
        else if (node.IsContainer)
        {
            var i = 0;
            foreach (var child in node.Children)
                children.Add((child, kind == NodeKind.Array ? null : child.Key ?? (i++).ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            valueKind = ValueKind.None;
        }

        Do(new ChangeTypeEdit(node, kind, kind == NodeKind.Value ? valueKind : ValueKind.None, text, children));
    }

    /// <summary>Why <paramref name="key"/> can't name a <paramref name="kind"/> in <paramref name="parent"/>, ignoring <paramref name="self"/>.</summary>
    private string? KeyProblem(DocNode parent, DocNode? self, NodeKind kind, string key)
    {
        if (key.Length == 0)
            return "Names can't be empty.";
        if (Document.Format == DocumentFormat.Ini && FormatRules.BadIniName(key) is { } iniError)
            return iniError;
        // A file may repeat an INI section, but no other name.
        if (kind != NodeKind.Section && parent.Children.Any(c => c != self && c.Key == key && c.Kind != NodeKind.Section))
            return $"There's already a {key} here.";
        return null;
    }

    private void Do(Edit edit)
    {
        edit.Apply();
        _undo.Add(edit);
        _redo.Clear();
        Changed?.Invoke();
    }

    private static string ConvertText(DocNode node, ValueKind to)
    {
        var text = node.Kind == NodeKind.Value && node.ValueKind != ValueKind.Null ? node.Text.Trim() : "";
        return to switch
        {
            ValueKind.Number => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? text
                : text == "true" ? "1" : "0",
            ValueKind.Boolean => text is "true" or "1" ? "true" : "false",
            ValueKind.Null => "null",
            _ => node.Kind == NodeKind.Value && node.ValueKind != ValueKind.Null ? node.Text : "",
        };
    }

    private static string DefaultText(ValueKind kind) => kind switch
    {
        ValueKind.Number => "0",
        ValueKind.Boolean => "false",
        ValueKind.Null => "null",
        _ => "",
    };

    private static string UniqueKey(DocNode parent, string stem)
    {
        if (parent.Children.All(c => c.Key != stem))
            return stem;
        for (var n = 2; ; n++)
        {
            var key = $"{stem}_{n}";
            if (parent.Children.All(c => c.Key != key))
                return key;
        }
    }
}
