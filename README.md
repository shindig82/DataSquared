# DataSquared

A visual editor for structured data files. Each object, array or INI section is a card on a
canvas: edit values right on the cards, drag cards wherever you like, and save back to the
original format with comments and key order intact.

Built with Godot 4.7.2 (.NET) and C#. Runs on Windows and Linux.

## Status

Step 1 prototype. It opens and saves:

- **JSON**, including `//` and `/* */` comments. Saving re-indents with two spaces.
- **INI / .cfg**, such as Fleetborne's `server.cfg`. An unedited file saves back byte for byte,
  and editing a value only changes that line. Repeated sections stay separate.

XML is planned. The document model (`DocNode`) was designed to take it.

## Using it

- **Open**: Ctrl+O, the toolbar, drag a file onto the window, or pass it on the command line.
- **Edit**: type into a value, or tick a checkbox. Hover a row to see its comment from the file.
  A number that doesn't parse turns red and isn't applied.
- **Layout**: drag cards anywhere. *Auto layout* (Ctrl+L) tidies them into a tree. Card positions
  are saved per file in Godot's user data folder, so your data files aren't touched.
- **Save**: Ctrl+S, or Ctrl+Shift+S to save as. The right pane shows exactly what will be written.

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
