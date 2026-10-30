using System;
using System.IO;
using System.Linq;
using DataSquared.Core.Editing;
using DataSquared.Core.Model;
using Godot;

namespace DataSquared;

/// <summary>The main window: toolbar, card canvas, a live preview of the file, and a status bar.</summary>
public partial class Main : Control
{
    private const string FileFilters = "*.json, *.cfg, *.ini ; Data files";

    private GraphCanvas _canvas = null!;
    private CodeEdit _preview = null!;
    private Label _status = null!;
    private FileDialog _openDialog = null!;
    private FileDialog _saveDialog = null!;
    private ConfirmationDialog _discardDialog = null!;
    private AcceptDialog _errorDialog = null!;

    private DocumentEditor? _editor;
    private string? _path;
    private Button _undoButton = null!;
    private Button _redoButton = null!;
    private Action? _afterDiscard;

    public override void _Ready()
    {
        BuildUi();
        GetTree().AutoAcceptQuit = false;
        GetWindow().FilesDropped += files => ConfirmDiscard(() => Open(files[0]));

        var args = OS.GetCmdlineUserArgs();
        var file = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (file != null)
            Open(file);
        else
            UpdateChrome();

        var shot = args.FirstOrDefault(a => a.StartsWith("--screenshot="));
        if (shot != null)
            CaptureAndQuit(shot["--screenshot=".Length..]);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
            ConfirmDiscard(() => GetTree().Quit());
    }

    private void BuildUi()
    {
        var root = new VBoxContainer();
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        var toolbar = new HBoxContainer();
        toolbar.AddThemeConstantOverride("separation", 4);
        var toolbarPanel = new PanelContainer();
        toolbarPanel.AddChild(toolbar);
        root.AddChild(toolbarPanel);

        toolbar.AddChild(ToolButton("Open", Key.O, false, () => ConfirmDiscard(() => _openDialog.PopupCentered(new Vector2I(900, 600)))));
        toolbar.AddChild(ToolButton("Save", Key.S, false, Save));
        toolbar.AddChild(ToolButton("Save As", Key.S, true, () => _saveDialog.PopupCentered(new Vector2I(900, 600))));
        toolbar.AddChild(new VSeparator());
        // Undo and redo are handled in _Input, ahead of text boxes, so they always act on the document.
        _undoButton = new Button { Text = "Undo", Flat = true };
        _undoButton.Pressed += Undo;
        toolbar.AddChild(_undoButton);
        _redoButton = new Button { Text = "Redo", Flat = true };
        _redoButton.Pressed += Redo;
        toolbar.AddChild(_redoButton);
        toolbar.AddChild(new VSeparator());
        toolbar.AddChild(ToolButton("Auto layout", Key.L, false, () => _canvas.AutoLayout()));

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(split);

        _canvas = new GraphCanvas { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 3 };
        _canvas.Problem += ShowError;
        split.AddChild(_canvas);

        _preview = new CodeEdit
        {
            Editable = false,
            GuttersDrawLineNumbers = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(360, 0),
        };
        split.AddChild(_preview);

        var statusPanel = new PanelContainer();
        _status = new Label { Text = "" };
        statusPanel.AddChild(_status);
        root.AddChild(statusPanel);

        _openDialog = FileDialogFor(FileDialog.FileModeEnum.OpenFile);
        _openDialog.FileSelected += Open;
        _saveDialog = FileDialogFor(FileDialog.FileModeEnum.SaveFile);
        _saveDialog.FileSelected += SaveTo;

        _discardDialog = new ConfirmationDialog { Title = "Unsaved changes", DialogText = "Discard your unsaved changes?", OkButtonText = "Discard" };
        _discardDialog.Confirmed += () => _afterDiscard?.Invoke();
        AddChild(_discardDialog);

        _errorDialog = new AcceptDialog { Title = "DataSquared" };
        AddChild(_errorDialog);
    }

    private FileDialog FileDialogFor(FileDialog.FileModeEnum mode)
    {
        var dialog = new FileDialog
        {
            FileMode = mode,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Filters = new[] { FileFilters, "* ; All files" },
        };
        AddChild(dialog);
        return dialog;
    }

    private static Button ToolButton(string text, Key key, bool shift, Action pressed)
    {
        var shortcut = new InputEventKey { Keycode = key, CommandOrControlAutoremap = true, ShiftPressed = shift };
        var button = new Button { Text = text, Flat = true, Shortcut = new Shortcut { Events = new() { shortcut } } };
        button.Pressed += pressed;
        return button;
    }

    private void Open(string path)
    {
        try
        {
            var doc = Document.Load(path);
            _editor = new DocumentEditor(doc);
            _editor.Changed += OnChanged;
            _path = path;
            _canvas.Editor = _editor;
            _canvas.ShowDocument(doc, Path.GetFileName(path), LayoutStore.Load(path));
            RefreshPreview();
            UpdateChrome();
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
        {
            ShowError($"Couldn't open {Path.GetFileName(path)}.\n\n{e.Message}");
        }
    }

    private void Save()
    {
        if (_editor == null)
            return;
        if (_path == null)
            _saveDialog.PopupCentered(new Vector2I(900, 600));
        else
            SaveTo(_path);
    }

    private void SaveTo(string path)
    {
        if (_editor == null)
            return;
        try
        {
            _editor.Document.Save(path);
            _editor.MarkSaved();
            _path = path;
            LayoutStore.Save(path, _canvas.CardPositions(), _canvas.Zoom, _canvas.ScrollOffset);
            UpdateChrome();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowError($"Couldn't save {Path.GetFileName(path)}.\n\n{e.Message}");
        }
    }

    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key || !key.IsCommandOrControlPressed())
            return;
        var redo = key.Keycode == Key.Y || (key.Keycode == Key.Z && key.ShiftPressed);
        if (key.Keycode != Key.Z && !redo)
            return;

        // Leave the text box first, so it doesn't keep showing a value the undo just replaced.
        GetViewport().GuiReleaseFocus();
        if (redo) Redo(); else Undo();
        GetViewport().SetInputAsHandled();
    }

    private void Undo()
    {
        if (_editor?.Undo() == true)
            _canvas.Refresh();
    }

    private void Redo()
    {
        if (_editor?.Redo() == true)
            _canvas.Refresh();
    }

    private void OnChanged()
    {
        RefreshPreview();
        UpdateChrome();
    }

    private void RefreshPreview()
    {
        if (_editor == null)
            return;
        var line = _preview.GetFirstVisibleLine();
        _preview.Text = _editor.Document.Write();
        _preview.SetLineAsFirstVisible(Math.Min(line, _preview.GetLineCount() - 1));
    }

    private void UpdateChrome()
    {
        var name = _path == null ? "No file" : Path.GetFileName(_path);
        var dirty = _editor?.IsDirty == true;
        GetWindow().Title = $"DataSquared — {name}{(dirty ? " •" : "")}";

        _undoButton.Disabled = _editor?.CanUndo != true;
        _undoButton.TooltipText = _editor?.UndoDescription is { } undo ? $"Undo {undo} (Ctrl+Z)" : "Nothing to undo";
        _redoButton.Disabled = _editor?.CanRedo != true;
        _redoButton.TooltipText = _editor?.RedoDescription is { } redo ? $"Redo {redo} (Ctrl+Y)" : "Nothing to redo";

        var doc = _editor?.Document;
        _status.Text = doc == null
            ? "  Open a .json, .cfg or .ini file, or drop one on the window."
            : $"  {doc.Format.ToString().ToUpperInvariant()}  ·  {_canvas.CardCount} cards  ·  {doc.Root.Descendants().Count()} nodes  ·  {_path}  ·  {(dirty ? "Unsaved" : "Saved")}";
    }

    private void ConfirmDiscard(Action then)
    {
        if (_editor?.IsDirty != true)
        {
            then();
            return;
        }
        _afterDiscard = then;
        _discardDialog.PopupCentered();
    }

    private void ShowError(string message)
    {
        _errorDialog.DialogText = message;
        _errorDialog.PopupCentered();
    }

    private async void CaptureAndQuit(string outPath)
    {
        // Developer aid: renders a few frames, saves the window to a PNG, and exits.
        for (var i = 0; i < 10; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GetViewport().GetTexture().GetImage().SavePng(outPath);
        GetTree().Quit();
    }
}
