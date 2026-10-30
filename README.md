# DataSquared

A visual editor for structured data files. Each object, array or INI section is a card on a
canvas: edit values right on the cards, drag cards wherever you like, and save back to the
original format with comments and key order intact.

Built with Godot 4.7.2 (.NET) and C#. Runs on Windows and Linux.

## Status

Prototype, with the core editing model in place. It opens and saves:

- **JSON**, including `//` and `/* */` comments. Saving keeps the file's indentation (tabs or
  spaces) and key order.
- **INI / .cfg**, such as Fleetborne's `server.cfg`. An unedited file saves back byte for byte,
  and editing a value only changes that line. Repeated sections stay separate.

XML is planned. The document model (`DocNode`) was designed to take it.

All changes go through `DocumentEditor` in the core library: set a value, rename, add, delete,
move, duplicate, change type and replace-all, each checked against the format's rules and undoable.

## Using it

The window has three panes: an outline of every node on the left, the card canvas in the middle,
and the file as text on the right.

- **Open or start a file**: Ctrl+O, drag a file onto the window, or pass it on the command line.
  Ctrl+N starts an empty JSON or INI file.
- **Edit values** right on the cards: type into a value or tick a checkbox. Hover a row to see its
  comment from the file. A value that doesn't fit its type turns red and isn't applied.
- **Change structure**: right-click a card or row to add, rename, change type, duplicate, move or
  delete. Double-click a name to rename it. Drag a row by its grip (⠿) onto another row or a card's
  header to move it. Only what the format allows is offered (INI can't nest, for example).
- **Edit as text**: type in the text pane, then Apply (Ctrl+Enter). If it doesn't parse, it says
  where. Refresh throws the typing away.
- **Find**: Ctrl+F searches keys and values. Enter and Shift+Enter step through matches.
- **Undo**: Ctrl+Z, and Ctrl+Y or Ctrl+Shift+Z to redo. Typing into one value undoes as one step.
- **Layout**: drag cards anywhere, collapse a card with its ▾ button, *Auto layout* (Ctrl+L) to
  tidy, *Fit* (Ctrl+0) to see everything. Card positions are saved per file in Godot's user data
  folder, so your data files aren't touched.
- **Save**: Ctrl+S, or Ctrl+Shift+S to save as.

## Layout

```
src/DataSquared.Core/    Document model and format readers/writers. Plain .NET 8, no Godot.
tests/                   xUnit tests for the core, including server.cfg round-trips.
app/                     The Godot project (open app/project.godot).
```

The core library has no Godot dependency, so it's tested with plain `dotnet test` and could
back a command-line tool later.

## Building

Needs the .NET 8 SDK and Godot 4.7.2 .NET.

```sh
dotnet test                              # core tests
dotnet build app/DataSquared.csproj      # app assembly
godot --path app                         # run
godot --path app -- path/to/file.json    # run and open a file
```

Or open `DataSquared.sln` in Visual Studio, and `app/project.godot` in the Godot editor.

To export, install the Godot 4.7.2 export templates, then use the *Windows Desktop* or *Linux*
preset (Project › Export). CI builds and tests on both Windows and Linux.
