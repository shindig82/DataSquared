using System;
using System.Collections.Generic;
using System.Linq;
using DataSquared.Core.Model;
using Godot;

namespace DataSquared;

/// <summary>Finds nodes by key or value. Enter jumps to the next match, Shift+Enter to the previous.</summary>
public partial class SearchBar : HBoxContainer
{
    private LineEdit _query = null!;
    private Label _count = null!;
    private Button _previous = null!;
    private Button _next = null!;
    private Document? _document;
    private List<DocNode> _matches = new();
    private int _current = -1;

    /// <summary>Raised with the match to show.</summary>
    public event Action<DocNode>? Jump;

    public override void _Ready()
    {
        _query = new LineEdit
        {
            PlaceholderText = "Search keys and values (Ctrl+F)",
            CustomMinimumSize = new Vector2(260, 0),
            ClearButtonEnabled = true,
        };
        _query.TextChanged += _ =>
        {
            _current = -1;
            Update();
            Step(1);
        };
        _query.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter } key)
            {
                Step(key.ShiftPressed ? -1 : 1);
                _query.AcceptEvent();
            }
            else if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                _query.Clear();
                _query.ReleaseFocus();
            }
        };
        AddChild(_query);

        _count = new Label { CustomMinimumSize = new Vector2(64, 0), HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.6f) };
        AddChild(_count);
        _previous = new Button { Text = "↑", Flat = true, TooltipText = "Previous match (Shift+Enter)" };
        _previous.Pressed += () => Step(-1);
        AddChild(_previous);
        _next = new Button { Text = "↓", Flat = true, TooltipText = "Next match (Enter)" };
        _next.Pressed += () => Step(1);
        AddChild(_next);
        Update();
    }

    public void ShowDocument(Document document)
    {
        _document = document;
        _current = -1;
        Update();
    }

    public void FocusQuery()
    {
        _query.GrabFocus();
        _query.SelectAll();
    }

    /// <summary>Call after any change to the document, so the matches stay current.</summary>
    public void DocumentChanged()
    {
        var at = _current >= 0 && _current < _matches.Count ? _matches[_current] : null;
        Update();
        _current = at == null ? -1 : _matches.IndexOf(at);
        UpdateCount();
    }

    private void Update()
    {
        var q = _query.Text.Trim();
        _matches = _document == null || q.Length == 0
            ? new List<DocNode>()
            : _document.Root.Descendants().Where(n => Matches(n, q)).ToList();
        UpdateCount();
    }

    private static bool Matches(DocNode node, string q) =>
        (node.Key?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
        || (node.Kind == NodeKind.Value && node.Text.Contains(q, StringComparison.OrdinalIgnoreCase));

    private void Step(int direction)
    {
        if (_matches.Count == 0)
            return;
        _current = _current < 0
            ? (direction > 0 ? 0 : _matches.Count - 1)
            : (_current + direction + _matches.Count) % _matches.Count;
        UpdateCount();
        Jump?.Invoke(_matches[_current]);
    }

    private void UpdateCount()
    {
        _count.Text = _query.Text.Trim().Length == 0 ? ""
            : _matches.Count == 0 ? "none"
            : _current < 0 ? $"{_matches.Count}" : $"{_current + 1} of {_matches.Count}";
        _previous.Disabled = _next.Disabled = _matches.Count == 0;
    }
}
