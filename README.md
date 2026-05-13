# CF Rez Manager

CF Rez Manager is a Windows WPF tool for browsing, extracting, and packing LithTech/CF `.rez` archives.

## Requirements

- Windows
- .NET 8 SDK or runtime

## Build

```powershell
dotnet build .\CFRezManager.csproj
```

Run the app from Visual Studio, or start the built executable under:

```text
bin\Debug\net8.0-windows\CFRezManager.exe
```

## Browse REZ Archives

1. Start the program.
2. Select a folder that contains `.rez` files.
3. Double-click folders or REZ archives to enter them.
4. Use the breadcrumb bar to jump back to parent locations.

Mouse shortcuts:

- Mouse back button: go to the previous viewed folder.
- Mouse forward button: go to the next viewed folder.

## Extract Files

Use `Extract All...` to export every file from all scanned REZ archives.

To export only specific items:

1. Select one file, folder, REZ archive, or REZ internal directory.
2. Use `Ctrl` or `Shift` to select multiple items when needed.
3. Right-click the selected item or selection.
4. Choose `Extract This Item...` or `Extract N Selected Items...`.
5. Select an output folder.

The exported files keep their REZ internal folder structure.

## Pack A Folder Into REZ

Use `Pack Folder...` to create a new `.rez` archive from a normal Windows folder.

1. Prepare a folder containing the files and subfolders you want inside the archive.
2. Click `Pack Folder...`.
3. Select the source folder.
4. Choose the output `.rez` path.

The selected folder's contents become the root contents of the new REZ archive. Directory tables are encrypted with the REZ table algorithm, and file MD5 values are recalculated.

## Current Format Notes

- File data is stored directly in the archive.
- REZ directory tables are encrypted and decrypted by `RezCrypto`.
- File MD5 values in the directory table match the raw file data MD5.
- Packed file and directory names must be ASCII.
- Packed files must have an extension from 1 to 4 characters.
- Creating a new REZ from a folder preserves content and structure, but it does not attempt to reproduce the original archive's exact byte layout, offsets, timestamps, or whole-file MD5.

## Project Files

- `MainWindow.xaml` / `MainWindow.xaml.cs`: WPF interface and user actions.
- `RezArchiveReader.cs`: Reads and extracts REZ archives.
- `RezArchiveWriter.cs`: Packs a folder into a new REZ archive.
- `RezCrypto.cs`: Directory table decode and encode logic.
- `ExplorerItem.cs`: In-app folder/archive item model.
- `VirtualizingWrapPanel.cs`: Virtualized icon grid layout.

