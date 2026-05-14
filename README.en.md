# CF Rez Manager

![Main window](image-1.png)
![Preview window](image.png)

CF Rez Manager is a Windows WPF tool for browsing, searching, extracting, and packing LithTech / CrossFire `.rez` archives.

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

## Release

The repository includes a GitHub Actions release workflow. Pushing a `v*` tag builds a Windows x64 self-contained single-file package and creates a GitHub Release.

```powershell
git tag v1.0.0
git push origin main
git push origin v1.0.0
```

## Browse REZ Archives

1. Start the program.
2. Select a folder that contains `.rez` files or loose resource files.
3. Double-click normal folders, REZ archives, internal REZ folders, or supported preview files.
4. Use the breadcrumb bar to jump back to parent locations.

Use the language selector in the top toolbar to switch between `Chinese` and `English`. The app remembers the selected language, view size, scan folder, pack folder, extract folder, and save location.

The search box builds an in-memory index the first time you type, then filters scanned files, folders, and internal REZ paths quickly. Separate multiple keywords with spaces to require all terms to match.

Use the bottom-right `Size` slider to switch views: smaller values use a list view with path and size details, while larger values return to the tiled icon view. Hover files, folders, loose files, or REZ items to see metadata such as type, path, size, source, MD5, and data offset when available.

## Preview Support

- PNG, JPG, BMP, GIF, TIFF, DDS, TGA, and DTX images lazy-load thumbnails.
- DDS decoding covers DXT1/DXT3/DXT5 block-compressed textures plus common uncompressed RGB, RGBA, and luminance textures.
- DTX and TGA decoding covers plain textures, LZMA-compressed textures commonly used by CF, and some raw-pixel textures with missing or misplaced TGA headers.
- Common raster image formats can preview both plain files and LZMA-compressed resources.
- DDS, DTX, TGA, and common image formats can open in an original-size preview window. Images are not stretched; if an image is larger than the screen, use the preview window scroll bars.
- The image preview window supports previous/next navigation across the current image list, using either the toolbar buttons or the left/right arrow keys.
- WAV, OGG, and MP3 audio files support waveform thumbnails and double-click audio preview.
- Audio preview supports previous/next navigation across the current audio list, playback controls, seeking, volume adjustment, and a PotPlayer-style spectrum display rendered with a lightweight bitmap buffer.
- OGG files are decoded through the built-in Vorbis path before playback so files that WPF cannot open directly can still preview.
- Audio and resource thumbnails show storage/decode badges such as `RAW`, `LZMA`, `DXT`, and `TXT`.
- SPR files support LithTech sprite parsing and animation preview. The app reads the frame rate and DTX frame paths from the SPR, loads DTX frames from the same REZ archive or extracted resource tree, and plays the animation automatically. If matching DTX frames cannot be found, it falls back to a text preview of the frame table.
- LTC files support thumbnails and double-click previews. Built-in decoders handle plain LTC, LZMA-compressed LTC, and CrossFire-style LTC files with the `54 83 B2 E1` header plus outer XOR. Decoded LTA text opens directly in the text preview window; LithTech model content can render a model thumbnail and open the standalone model preview window.
- LTB files support additional binary mesh-table offsets, vertex layouts, trailing mesh data, and mesh type variants, so more exported CrossFire models can be previewed directly.
- LTA files support both `lt-model` model text and `world` map text. World `polyhedron`, `pointlist`, and `editpoly/f` face indices are converted into previewable map meshes.
- DAT files support common CrossFire map and object previews. LithTech world DAT v85 files can render map-model thumbnails and open the model preview window. CrossFire object DAT files decode into text previews for `Zoneman`, `EnvSound`, `MovePath`, and `CameraAnimation`.
- CFT, FCF, FXF, FXO, NAV, APF, REF, TXT, and selected WAVE resources can decode into readable text or metadata previews, including common LZMA-compressed forms.
- LZMA-compressed resources show an `LZMA` badge in the thumbnail corner.

## Model Preview Controls

- Left-click the model viewport: enter free-look mode.
- Move the mouse: adjust the view direction.
- `W` / `A` / `S` / `D`: move forward, left, backward, and right.
- `Shift`: move faster.
- Mouse wheel: move forward or backward along the current view direction.
- Right-click or `Esc`: leave free-look mode.
- `Reset View`: reset the camera position and direction.

## Mouse Shortcuts

- Mouse back button: go to the previous viewed location.
- Mouse forward button: go to the next viewed location.
- Hold the left mouse button and drag: box-select visible files or folders.
- Hold `Ctrl` while dragging: toggle selection.
- Hold `Shift` while dragging: add to the current selection.

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

## v1.0.0 Official Release

- Shipped the first official `v1.0.0` release, stabilizing the current REZ browsing, search, extraction, repacking, and multi-format preview feature set.
- SPR sprite previews can load referenced DTX frames from both REZ archives and local extracted resource folders; thumbnails now prefer real sprite frames, and double-click previews can autoplay animations.
- Image/SPR preview windows now localize previous/next navigation, play/pause, and frame selection controls, and refresh their text when the app language changes.
- Local loose SPR files can be parsed and matched with frame files in neighboring resource trees, making extracted assets easier to inspect directly.
- Audio preview uses a borderless window style and refined spectrum peak falloff for a more player-like visual feel.
- Added the application icon, refreshed Chinese and English documentation, and made GitHub Release notes bilingual.

## v0.11.0 Changes

- Added DDS thumbnails and original-size preview support for DXT1/DXT3/DXT5 block-compressed textures and common uncompressed RGB, RGBA, and luminance textures.
- Improved DDS texture parsing for mipmap and cubemap byte ranges, with thumbnail scaling to reduce memory use.
- Added LZMA-compressed preview paths for common raster image resources, with clearer `RAW`, `LZMA`, and `DXT` badges and hover metadata.
- Made standalone preview-tool and model/LTC error messages follow the saved Chinese/English language setting.
- Switched model-preview free-look movement to WPF render-frame updates for smoother movement and cleaner shutdown cleanup.
- Updated Chinese and English documentation plus GitHub Release notes, and added the support QR code.

## v0.10.0 Changes

- Added loose-file browsing support so supported resources outside REZ archives can be previewed directly.
- Added WAV, OGG, and MP3 audio metadata parsing, waveform thumbnails, previous/next audio navigation, and a PotPlayer-style audio preview window.
- Added OGG playback conversion through NVorbis and MP3 spectrum analysis through NAudio/MediaFoundation.
- Optimized the audio spectrum renderer with a `WriteableBitmap` pixel buffer and improved end-of-playback peak falloff.
- Added decoded text previews for more CrossFire resource formats, including CFT/FCF/FXF/FXO/NAV/APF/REF and WAVE header data.
- Updated documentation and GitHub Release notes for the audio/resource preview release.

## v0.9.0 Changes

- Expanded native LTB binary model parsing for more mesh-table locations, vertex layouts, trailing mesh data, and mesh type variants.
- Added LTA world/map text parsing, converting `polyhedron`, `pointlist`, and `editpoly/f` face data into previewable map meshes.
- Improved the model preview window with free-look viewing, WASD movement, Shift fast movement, mouse-wheel movement, and steadier reset behavior.
- Improved the image preview window with previous/next navigation and aspect-preserving thumbnail decode sizing.
- Refreshed the Chinese and English documentation and GitHub Release notes.

## Project Files

- `MainWindow.xaml` / `MainWindow.xaml.cs`: WPF interface and user actions.
- `RezArchiveReader.cs`: Reads and extracts REZ archives.
- `RezArchiveWriter.cs`: Packs a folder into a new REZ archive.
- `RezCrypto.cs`: Directory table decode and encode logic.
- `ExplorerItem.cs`: In-app folder/archive item model.
- `LocalizedText.cs`: Chinese and English text for standalone preview-tool and decoder errors.
- `PreviewTool.cs`: Standalone preview-tool entry point.
- `AudioMetadataDecoder.cs` / `AudioThumbnailRenderer.cs`: Audio metadata and waveform thumbnail rendering.
- `AudioPreviewWindow.xaml` / `AudioPreviewWindow.xaml.cs`: Standalone audio preview window and spectrum renderer.
- `AudioSpectrumAnalyzer.cs` / `OggVorbisWaveDecoder.cs`: Audio spectrum analysis and OGG-to-WAV playback conversion.
- `ResourceTextDecoder.cs`: Decodes additional text-like CrossFire resource formats.
- `DtxThumbnailDecoder.cs` / `TgaThumbnailDecoder.cs` / `DdsThumbnailDecoder.cs`: Image and texture preview decoding.
- `LithTechSpriteDecoder.cs` / `LithTechSpritePreviewLoader.cs`: SPR sprite parsing and animation frame loading.
- `CrossFireLtcDecoder.cs` / `LithTechLtcNativeDecoder.cs`: LTC text and model preview decoding.
- `LithTechModelDecoder.cs` / `LithTechModelThumbnailRenderer.cs` / `LithTechModelSceneBuilder.cs`: LithTech model, LTB/LTA world parsing, and rendering.
- `CrossFireDatDecoder.cs` / `LithTechWorldDatDecoder.cs`: DAT object text and LithTech world map preview decoding.
- `TextThumbnailRenderer.cs`: Thumbnail rendering for text-like resources.
- `VirtualizingWrapPanel.cs`: Virtualized icon grid layout.

## Support The Project

Making this tool takes time and care. If it helps you, a small tip is appreciated.

![Support QR code](afc08a3298aeb1fa378e9d89ca34e35a.jpg)
