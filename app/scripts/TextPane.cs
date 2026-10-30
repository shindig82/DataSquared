using System;
using DataSquared.Core.Editing;
using DataSquared.Core.Model;
using Godot;

namespace DataSquared;

/// <summary>
/// The file as text, kept in step with the canvas. It can be edited directly: Apply turns the text into
/// the document (one undo step), Refresh throws the typing away.
/// </summary>
public partial class TextPane : VBoxContainer
{
    private CodeEdit _code = null!;
    private Button _apply = null!;
    private Button _refresh = null!;
    private Label _status = null!;
    private DocumentEditor? _editor;
    private bool _modified;
    private bool _settingText;

    /// <summary>Raised after typed text has been applied to the document.</summary>
    public event Action? Applied;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 0);

        var header = new HBoxContainer();
        header.AddChild(new Label { Text = "  TEXT", SizeFlagsHorizontal = SizeFlags.ExpandFill, Modulate = new Color(1, 1, 1, 0.6f) });
        _apply = new Button { Text = "Apply", Flat = true, TooltipText = "Use this text as the document (Ctrl+Enter)" };
        _apply.Pressed += Apply;
        header.AddChild(_apply);
        _refresh = new Button { Text = "Refresh", Flat = true, TooltipText = "Throw away typed changes and show the document again" };
        _refresh.Pressed += Reload;
        header.AddChild(_refresh);
        AddChild(header);

        _code = new CodeEdit
        {
            GuttersDrawLineNumbers = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            IndentUseSpaces = true,
            IndentAutomatic = true,
            AutoBraceCompletionEnabled = true,
        };
        _code.TextChanged += () =>
        {
            if (_settingText)
                return;
            _modified = true;
            _status.Text = "Edited. Apply to use this text, or Refresh to discard it.";
            _status.Modulate = new Color(1, 1, 1, 0.7f);
            UpdateButtons();
        };
        _code.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter } key && key.IsCommandOrControlPressed())
            {
                Apply();
                _code.AcceptEvent();
            }
        };
        AddChild(_code);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        AddChild(_status);
        UpdateButtons();
    }

    public bool HasTextFocus => _code.HasFocus();

    public void ShowDocument(DocumentEditor editor)
    {
        _editor = editor;
        _code.SyntaxHighlighter = Highlighter(editor.Document.Format);
        Reload();
    }

    /// <summary>Call after any change to the document. Typed text isn't overwritten until it's applied or refreshed.</summary>
    public void DocumentChanged()
    {
        if (!_modified)
        {
            Reload();
            return;
        }
        _status.Text = "The document changed on the canvas too. Apply overwrites those changes; Refresh keeps them.";
        _status.Modulate = Palette.Number;
    }

    private void Reload()
    {
        if (_editor == null)
            return;
        var line = _code.GetFirstVisibleLine();
        _settingText = true;
        _code.Text = _editor.Document.Write();
        _settingText = false;
        _code.SetLineAsFirstVisible(Math.Min(line, _code.GetLineCount() - 1));
        _modified = false;
        _status.Text = "";
        UpdateButtons();
    }

    private void Apply()
    {
        if (_editor == null || !_modified)
            return;
        try
        {
            _modified = false;
            _editor.ReplaceAll(_code.Text);
            Reload();
            Applied?.Invoke();
        }
        catch (FormatException e)
        {
            _modified = true;
            _status.Text = e.Message;
            _status.Modulate = Palette.Error;
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        _apply.Disabled = !_modified;
        _refresh.Disabled = !_modified;
    }

    private static CodeHighlighter Highlighter(DocumentFormat format)
    {
        var h = new CodeHighlighter
        {
            NumberColor = Palette.Number,
            SymbolColor = new Color(1, 1, 1, 0.55f),
            FunctionColor = Colors.White,
            MemberVariableColor = Colors.White,
        };
        foreach (var word in new[] { "true", "false" })
            h.AddKeywordColor(word, Palette.Boolean);

        if (format == DocumentFormat.Json)
        {
            h.AddKeywordColor("null", Palette.Null);
            h.AddColorRegion("\"", "\"", Palette.String);
            h.AddColorRegion("//", "", Palette.Comment, true);
            h.AddColorRegion("/*", "*/", Palette.Comment);
        }
        else if (format == DocumentFormat.Xml)
        {
            h.AddColorRegion("<!--", "-->", Palette.Comment);
            h.AddColorRegion("<?", "?>", Palette.Comment);
            h.AddColorRegion("\"", "\"", Palette.String);
        }
        else
        {
            h.AddColorRegion(";", "", Palette.Comment, true);
            h.AddColorRegion("#", "", Palette.Comment, true);
            h.AddColorRegion("[", "]", Palette.Object);
        }
        return h;
    }
}
