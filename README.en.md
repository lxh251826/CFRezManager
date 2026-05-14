# CF Rez Manager
![alt text](image-1.png)
![alt text](image.png)

CF Rez Manager is a Windows WPF tool for browsing, extracting, and packing LithTech / CF `.rez` archives.

Chinese documentation is available in [README.md](README.md).

## Requirements

- Windows
- .NET 8 SDK or .NET 8 Runtime

## Build

```powershell
dotnet build .\CFRezManager.csproj
```

Run the app from Visual Studio, or start the built executable:

```text
bin\Debug\net8.0-windows\CFRezManager.exe
```

## Browse REZ Archives

1. Start the program.
2. Select a folder that contains `.rez` files.
3. Double-click normal folders, REZ archives, or internal REZ folders to enter them.
4. Use the breadcrumb bar to jump back to parent locations.

Use the language selector in the top toolbar to switch between `中文` and `English`. Buttons, context menus, status text, and common dialog prompts update with the selected language.

The app remembers the selected language, bottom-right size slider, and the folders used for scanning, packing, extracting, and saving REZ files. On the next launch or the next matching dialog, it restores the last used settings and location.

The search box builds an in-memory index the first time you type, then filters scanned files, folders, and internal REZ paths quickly, similar to Everything. Separate multiple keywords with spaces to require all terms to match.

Use the bottom-right `Size` slider to switch views: smaller values use a list view with path and size details, while larger values return to the tiled icon view. Hover files, folders, or REZ items to see available metadata such as type, path, size, source, MD5, and data offset.

PNG, JPG, BMP, GIF, TIFF, TGA, and DTX image files inside REZ archives lazy-load thumbnails when they become visible. Files that cannot be decoded still use the normal file icon.
DTX and TGA decoding covers plain textures, the LZMA-compressed textures commonly used by CF, and some raw-pixel textures with missing or misplaced TGA headers.
Double-click a decodable image file to open an original-size preview window. The image is not stretched; if it is larger than the screen, use the preview window scroll bars.

LTC files support thumbnails and double-click previews. The app first uses built-in decoders for plain LTC, LZMA-compressed LTC, and the CrossFire-style LTC files with the `54 83 B2 E1` header plus outer XOR; decoded LTA text opens directly in the text preview window. If the content is a LithTech model, the app tries to render a model thumbnail and can open the standalone model preview window. LTC files that still cannot be recognized can fall back to `CFREZ_LTC_TO_LTA` or an external converter placed under the `tools` folder.

DAT files support common CrossFire map and object previews. LithTech world DAT v85 files can render map-model thumbnails and open in the standalone model preview window; both plain DAT and LZMA-compressed DAT files are detected automatically, and LZMA-compressed resources show a `LZMA` badge in the thumbnail corner. CrossFire object DAT files decode into text previews, currently covering `Zoneman` areas, `EnvSound` ambience, `MovePath` paths, and `CameraAnimation` cutscene camera data.

Mouse shortcuts:

- Mouse back button: go to the previous viewed location.
- Mouse forward button: go to the next viewed location.
- Hold the left mouse button and drag: box-select visible files or folders. Hold `Ctrl` to toggle selection, or `Shift` to add to the current selection.

## Extract Files

Use `Extract All...` to export every file from all REZ archives in the scanned folder.

To export only specific items:

1. Select one file, folder, REZ archive, or internal REZ folder.
2. Use `Ctrl` or `Shift` to select multiple items when needed.
3. Right-click the selected item or selection.
4. Choose `Extract This Item...` or `Extract N Selected Items...`.
5. Select an output folder.

Exported files keep their internal REZ folder structure.

## Pack A Folder Into REZ

Use `Pack Folder...` to create a new `.rez` archive from a normal Windows folder.

1. Prepare a folder containing the files and subfolders you want inside the archive.
2. Click `Pack Folder...`.
3. Select the source folder.
4. Choose the output `.rez` path.

The selected folder's contents become the root contents of the new REZ archive. Directory tables are encrypted, and file MD5 values are recalculated.

## Current Format Notes

- File data is stored directly in the archive.
- REZ directory tables are encrypted and decrypted by `RezCrypto`.
- File MD5 values in the directory table match the raw file data MD5.
- Packed file and directory names currently must be ASCII.
- Packed files must have an extension from 1 to 4 characters.
- Creating a new REZ from a folder preserves content and structure, but it does not try to reproduce the original archive's exact byte layout, offsets, timestamps, or whole-file MD5.

## Project Files

- `MainWindow.xaml` / `MainWindow.xaml.cs`: WPF interface and user actions.
- `RezArchiveReader.cs`: Reads and extracts REZ archives.
- `RezArchiveWriter.cs`: Packs a folder into a new REZ archive.
- `RezCrypto.cs`: Directory table decode and encode logic.
- `ExplorerItem.cs`: In-app folder/archive item model.
- `CrossFireLtcDecoder.cs` / `LithTechLtcNativeDecoder.cs`: LTC text and model preview decoding.
- `CrossFireDatDecoder.cs` / `LithTechWorldDatDecoder.cs`: DAT object text and LithTech world map preview decoding.
- `TextThumbnailRenderer.cs`: Thumbnail rendering for text-like resources.
- `VirtualizingWrapPanel.cs`: Virtualized icon grid layout.
