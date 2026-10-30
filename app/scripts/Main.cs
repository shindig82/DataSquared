using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DataSquared.Core.Editing;
using DataSquared.Core.Model;
using DataSquared.Core.Schema;
using Godot;

namespace DataSquared;

/// <summary>The main window: toolbar, node tree, card canvas, the file as text, and a status bar.</summary>
public partial class Main : Control
{
    private const string FileFilters = "*.json, *.xml, *.cfg, *.ini ; Data files";

    private NodeTree _tree = null!;
    private GraphCanvas _canvas = null!;
    private TextPane _text = null!;
    private SearchBar _search = null!;
    private PopupMenu _newMenu = null!;
    private Label _status = null!;
    private FileDialog _openDialog = null!;
    private FileDialog _saveDialog = null!;
    private ConfirmationDialog _discardDialog = null!;
    private AcceptDialog _errorDialog = null!;
    private PopupMenu _schemaMenu = null!;
    private FileDialog _schemaDialog = null!;
    private Button _problemsButton = null!;
    private JsonSchema? _schema;
    private string? _schemaPath;
    private IReadOnlyList<SchemaProblem> _problems = Array.Empty<SchemaProblem>();
    private int _nextProblem;

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
        Theme = AppTheme.Build();
        var background = new ColorRect { Color = AppTheme.Panel };
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(background);

        var root = new VBoxContainer();
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        var toolbar = new HBoxContainer();
        toolbar.AddThemeConstantOverride("separation", 4);
        var toolbarPanel = new PanelContainer();
        toolbarPanel.AddChild(toolbar);
        root.AddChild(toolbarPanel);

        _newMenu = new PopupMenu();
        _newMenu.AddItem("New JSON file", 0);
        _newMenu.AddItem("New INI file", 1);
        _newMenu.AddItem("New XML file", 2);
        _newMenu.IdPressed += id => ConfirmDiscard(() => New((DocumentFormat)id));
        AddChild(_newMenu);
        toolbar.AddChild(ToolButton("New", Key.N, false, () =>
        {
            _newMenu.Position = DisplayServer.MouseGetPosition();
            _newMenu.Popup();
        }));
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
        toolbar.AddChild(ToolButton("Fit", Key.Key0, false, () => _canvas.FitToView()));
        var comments = new CheckButton { Text = "Comments", ButtonPressed = true, Flat = true, TooltipText = "Show comments from the file under their rows" };
        comments.Toggled += on => _canvas.ShowComments = on;
        toolbar.AddChild(comments);

        _schemaMenu = new PopupMenu();
        _schemaMenu.AddItem("Choose schema…", 0);
        _schemaMenu.AddItem("Reload schema", 1);
        _schemaMenu.AddItem("Remove schema", 2);
        _schemaMenu.IdPressed += id =>
        {
            switch (id)
            {
                case 0:
                    _schemaDialog.PopupCentered(new Vector2I(900, 600));
                    break;
                case 1:
                    UseSchema(_schemaPath, remember: false);
                    break;
                case 2:
                    UseSchema(null, remember: true);
                    break;
            }
        };
        AddChild(_schemaMenu);
        toolbar.AddChild(ToolButton("Schema", Key.None, false, () =>
        {
            _schemaMenu.SetItemDisabled(1, _schemaPath == null);
            _schemaMenu.SetItemDisabled(2, _schemaPath == null);
            _schemaMenu.Position = DisplayServer.MouseGetPosition();
            _schemaMenu.Popup();
        }));
        toolbar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _search = new SearchBar();
        _search.Jump += node =>
        {
            _canvas.FocusNode(node);
            _tree.SelectNode(node);
        };
        toolbar.AddChild(_search);

        var outer = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(outer);

        _tree = new NodeTree { CustomMinimumSize = new Vector2(280, 0) };
        _tree.NodePicked += node => _canvas.FocusNode(node);
        outer.AddChild(_tree);

        var inner = new HSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        outer.AddChild(inner);

        _canvas = new GraphCanvas { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 3 };
        _canvas.Problem += ShowError;
        _canvas.CardSelected += node => _tree.SelectNode(node);
        inner.AddChild(_canvas);

        _text = new TextPane { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(360, 0) };
        _text.Applied += () => _canvas.Refresh();
        inner.AddChild(_text);

        var statusPanel = new PanelContainer();
        var statusLine = new HBoxContainer();
        _status = new Label { Text = "", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        statusLine.AddChild(_status);
        _problemsButton = ToolButton("", Key.F8, false, NextProblem);
        _problemsButton.Visible = false;
        _problemsButton.AddThemeColorOverride("font_color", Palette.Error);
        statusLine.AddChild(_problemsButton);
        statusPanel.AddChild(statusLine);
        root.AddChild(statusPanel);

        _openDialog = FileDialogFor(FileDialog.FileModeEnum.OpenFile);
        _openDialog.FileSelected += Open;
        _saveDialog = FileDialogFor(FileDialog.FileModeEnum.SaveFile);
        _saveDialog.FileSelected += SaveTo;
        _schemaDialog = FileDialogFor(FileDialog.FileModeEnum.OpenFile);
        _schemaDialog.Filters = new[] { "*.json ; JSON Schema", "* ; All files" };
        _schemaDialog.FileSelected += path => UseSchema(path, remember: true);

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
        var button = new Button { Text = text, Flat = true };
        if (key != Key.None)
        {
            // Function keys work alone; letters need Ctrl (Cmd on macOS).
            var shortcut = new InputEventKey { Keycode = key, CommandOrControlAutoremap = key is < Key.F1 or > Key.F12, ShiftPressed = shift };
            button.Shortcut = new Shortcut { Events = new() { shortcut } };
        }
        button.Pressed += pressed;
        return button;
    }

    private void New(DocumentFormat format)
    {
        var (text, name) = format switch
        {
            DocumentFormat.Json => ("{}\n", "Untitled.json"),
            DocumentFormat.Xml => ("<root/>\n", "Untitled.xml"),
            _ => ("", "Untitled.ini"),
        };
        Show(Document.Parse(text, format), null, name, null);
    }

    private void Open(string path)
    {
        try
        {
            Show(Document.Load(path), path, Path.GetFileName(path), LayoutStore.Load(path));
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
        {
            ShowError($"Couldn't open {Path.GetFileName(path)}.\n\n{e.Message}");
        }
    }

    private void Show(Document doc, string? path, string title, LayoutStore.Saved? layout)
    {
        _editor = new DocumentEditor(doc);
        _editor.Changed += OnChanged;
        _path = path;
        _canvas.Editor = _editor;
        _canvas.ShowDocument(doc, title, layout);
        _tree.ShowDocument(doc, title);
        _text.ShowDocument(_editor);
        _search.ShowDocument(doc);
        UseSchema(FindSchema(path, doc), remember: false);
    }

    /// <summary>
    /// The schema for a file: the one picked for it before, else a $schema path in a JSON file, else one
    /// beside it named like server.schema.json or server.cfg.schema.json.
    /// </summary>
    private static string? FindSchema(string? path, Document doc)
    {
        if (path == null)
            return null;
        if (LayoutStore.LoadSchema(path) is { } picked && File.Exists(picked))
            return picked;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        var named = doc.Format == DocumentFormat.Json
            ? doc.Root.Children.FirstOrDefault(c => c.Key == "$schema" && c.Kind == NodeKind.Value)?.Text
            : null;
        var candidates = new[]
        {
            named != null && !named.Contains("://") ? Path.Combine(dir, named) : null,
            Path.ChangeExtension(path, ".schema.json"),
            path + ".schema.json",
        };
        return candidates.FirstOrDefault(c => c != null && File.Exists(c) && Path.GetFullPath(c) != Path.GetFullPath(path));
    }

    private void UseSchema(string? schemaPath, bool remember)
    {
        _schema = null;
        _schemaPath = null;
        if (schemaPath != null)
        {
            try
            {
                _schema = JsonSchema.Parse(File.ReadAllText(schemaPath));
                _schemaPath = schemaPath;
            }
            catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
            {
                ShowError($"Couldn't use {Path.GetFileName(schemaPath)} as a schema.\n\n{e.Message}");
            }
        }
        if (remember && _path != null)
            LayoutStore.SaveSchema(_path, _schemaPath == null ? null : Path.GetFullPath(_schemaPath));

        _canvas.Schema = _schema;
        _canvas.Refresh();
        Validate();
    }

    private void Validate()
    {
        _problems = _schema != null && _editor != null ? _schema.Validate(_editor.Document) : Array.Empty<SchemaProblem>();
        _nextProblem = Math.Min(_nextProblem, Math.Max(0, _problems.Count - 1));
        _canvas.ShowProblems(_problems);
        // After the canvas has rebuilt for this change, so the card count is current.
        Callable.From(UpdateChrome).CallDeferred();
    }

    /// <summary>Shows the next schema problem on the canvas, going round to the first after the last.</summary>
    private void NextProblem()
    {
        if (_problems.Count == 0)
            return;
        var problem = _problems[_nextProblem++ % _problems.Count];
        _nextProblem %= _problems.Count;
        _canvas.FocusNode(problem.Node);
        _tree.SelectNode(problem.Node);
        _status.Text = $"  {problem.Message}";
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
        if (key.Keycode == Key.F)
        {
            _search.FocusQuery();
            GetViewport().SetInputAsHandled();
            return;
        }
        var redo = key.Keycode == Key.Y || (key.Keycode == Key.Z && key.ShiftPressed);
        if (key.Keycode != Key.Z && !redo)
            return;
        // While typing in the text pane, undo belongs to the text.
        if (_text.HasTextFocus)
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
        _tree.QueueRebuild();
        _text.DocumentChanged();
        _search.DocumentChanged();
        Validate();
    }

    private void UpdateChrome()
    {
        var name = _path == null ? (_editor == null ? "No file" : "Untitled") : Path.GetFileName(_path);
        var dirty = _editor?.IsDirty == true;
        GetWindow().Title = $"DataSquared — {name}{(dirty ? " •" : "")}";

        _undoButton.Disabled = _editor?.CanUndo != true;
        _undoButton.TooltipText = _editor?.UndoDescription is { } undo ? $"Undo {undo} (Ctrl+Z)" : "Nothing to undo";
        _redoButton.Disabled = _editor?.CanRedo != true;
        _redoButton.TooltipText = _editor?.RedoDescription is { } redo ? $"Redo {redo} (Ctrl+Y)" : "Nothing to redo";

        var doc = _editor?.Document;
        _status.Text = doc == null
            ? "  Start a new file, open a .json, .xml, .cfg or .ini file, or drop one on the window."
            : $"  {doc.Format.ToString().ToUpperInvariant()}  ·  {_canvas.CardCount} cards  ·  {doc.Root.Descendants().Count()} nodes  ·  {_path ?? "not saved yet"}  ·  {(dirty ? "Unsaved" : "Saved")}"
              + (_schemaPath == null ? "" : $"  ·  Schema: {Path.GetFileName(_schemaPath)}");
        _problemsButton.Visible = _schema != null;
        _problemsButton.Text = _problems.Count switch
        {
            0 => "No problems",
            1 => "1 problem ▸",
            var n => $"{n} problems ▸",
        };
        _problemsButton.TooltipText = _problems.Count == 0 ? "The file matches its schema" : "Show the next problem (F8)";
        _problemsButton.RemoveThemeColorOverride("font_color");
        if (_problems.Count > 0)
            _problemsButton.AddThemeColorOverride("font_color", Palette.Error);
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
