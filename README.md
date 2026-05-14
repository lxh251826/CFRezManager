# CF Rez Manager

![主界面](image-1.png)
![预览界面](image.png)

CF Rez Manager 是一个 Windows WPF 工具，用于浏览、搜索、解包和重新打包 LithTech / CrossFire 的 `.rez` 资源包。

English documentation is available in [README.en.md](README.en.md).

## 环境要求

- Windows
- .NET 8 SDK 或 .NET 8 Runtime

## 构建

```powershell
dotnet build .\CFRezManager.csproj
```

可以从 Visual Studio 运行，也可以启动构建后的程序：

```text
bin\Debug\net8.0-windows\CFRezManager.exe
```

## 发布

项目包含 GitHub Actions 发布流程。推送 `v*` 标签后会自动构建 Windows x64 自包含单文件包，并创建 GitHub Release。

```powershell
git tag v0.8.0
git push origin main
git push origin v0.8.0
```

## 浏览 REZ 资源包

1. 启动程序。
2. 选择包含 `.rez` 文件的文件夹。
3. 双击普通文件夹、REZ 包或 REZ 内部目录进入。
4. 使用顶部面包屑导航返回上级或跳转到任意父级位置。

顶部语言选择框可以在 `中文` 和 `English` 之间切换。程序会记住语言、视图大小、扫描目录、打包目录、导出目录和保存位置。

搜索框会在首次输入时建立内存索引，之后可以快速筛选已经扫描到的文件、目录和 REZ 内部路径。多个关键词用空格分隔时，需要全部命中才会显示结果。

右下角 `大小` 滑条可以切换显示方式：较小时使用列表视图并显示路径和大小，较大时切回平铺图标视图。鼠标悬停在文件、目录或 REZ 项目上时，会显示类型、路径、大小、来源、MD5、偏移等信息。

## 预览能力

- PNG、JPG、BMP、GIF、TIFF、TGA、DTX 图片会懒加载缩略图。
- DTX 和 TGA 支持普通纹理、CF 常见 LZMA 压缩纹理，以及部分缺失或错位 TGA 头的原始像素纹理。
- DDS、DTX、TGA 和常见图片格式可以打开原始尺寸预览窗口。图片不会被拉伸，过大时可以滚动查看。
- SPR 支持 LithTech 动态精灵解析和动画预览。程序会读取 SPR 中记录的帧率和 DTX 帧路径，从同一个 REZ 包或已解包目录中加载 DTX 帧并自动播放；找不到帧时会回退到帧路径文本预览。
- LTC 支持缩略图和双击预览。内置解码器会处理普通 LTC、LZMA 压缩 LTC，以及 CrossFire 常见的 `54 83 B2 E1` 头和外层 XOR。解码后的 LTA 文本可以直接查看；如果内容是 LithTech 模型，会尝试渲染模型缩略图并打开独立模型预览窗口。
- DAT 支持常见 CrossFire 地图和对象预览。LithTech world DAT v85 可以渲染地图模型缩略图并打开模型预览窗口；CrossFire 对象 DAT 会解码为文本预览，当前支持 `Zoneman`、`EnvSound`、`MovePath` 和 `CameraAnimation`。
- LZMA 压缩资源会在缩略图角标显示 `LZMA`。

## 鼠标快捷操作

- 鼠标后退键：回到上一个浏览位置。
- 鼠标前进键：前进到下一个浏览位置。
- 按住鼠标左键拖动：框选可见文件或目录。
- 拖选时按住 `Ctrl`：切换选区。
- 拖选时按住 `Shift`：追加选区。

## 解包文件

点击 `Extract All...` 可以导出当前扫描范围内所有 REZ 包中的全部文件。

只导出指定文件或目录：

1. 选中一个文件、文件夹、REZ 包或 REZ 内部目录。
2. 需要多选时，使用 `Ctrl` 或 `Shift` 选择多个项目。
3. 右键选中项目。
4. 选择 `Extract This Item...` 或 `Extract N Selected Items...`。
5. 选择输出文件夹。

导出的文件会保留 REZ 内部目录结构。

## 重新打包为 REZ

点击 `Pack Folder...` 可以把普通 Windows 文件夹打包为新的 `.rez` 文件。

1. 准备一个包含目标文件和子目录的文件夹。
2. 点击 `Pack Folder...`。
3. 选择源文件夹。
4. 选择输出 `.rez` 文件路径。

被选中文件夹的内容会成为新 REZ 包的根目录内容。程序会加密目录表，并重新计算每个文件的 MD5。

## 当前格式说明

- 文件数据在 REZ 中直接存放。
- REZ 目录表由 `RezCrypto` 负责解密和加密。
- 目录表中的文件 MD5 与原始文件数据的 MD5 一致。
- 重新打包时，文件名和目录名目前要求为 ASCII。
- 重新打包时，文件扩展名需要为 1 到 4 个字符。
- 从文件夹创建新 REZ 会保留内容和目录结构，但不会复刻原包的字节级布局、偏移、时间戳或整包 MD5。

## v0.8.0 更新

- 新增 LithTech SPR 解析，支持 LZMA 压缩和未压缩 SPR。
- 新增 SPR 动态精灵动画预览，可按帧率播放 DTX 帧，并支持暂停和手动选帧。
- SPR 找不到对应 DTX 时会回退到帧路径文本预览。
- 修复并重写中文说明书，更新英文说明和 GitHub Release 文案。

## 项目文件

- `MainWindow.xaml` / `MainWindow.xaml.cs`：WPF 界面和用户操作逻辑。
- `RezArchiveReader.cs`：读取和解包 REZ。
- `RezArchiveWriter.cs`：从文件夹打包生成新 REZ。
- `RezCrypto.cs`：REZ 目录表解密和加密逻辑。
- `ExplorerItem.cs`：程序内的文件夹和资源项模型。
- `PreviewTool.cs`：独立预览工具入口。
- `DtxThumbnailDecoder.cs` / `TgaThumbnailDecoder.cs` / `DdsThumbnailDecoder.cs`：图片和纹理预览解码。
- `LithTechSpriteDecoder.cs` / `LithTechSpritePreviewLoader.cs`：SPR 动态精灵解析和动画帧加载。
- `CrossFireLtcDecoder.cs` / `LithTechLtcNativeDecoder.cs`：LTC 文本和模型预览解码。
- `LithTechModelDecoder.cs` / `LithTechModelThumbnailRenderer.cs` / `LithTechModelSceneBuilder.cs`：LithTech 模型解析和渲染。
- `CrossFireDatDecoder.cs` / `LithTechWorldDatDecoder.cs`：DAT 对象文本和 LithTech world 地图预览解码。
- `TextThumbnailRenderer.cs`：文本类资源的缩略图渲染。
- `VirtualizingWrapPanel.cs`：虚拟化图标网格布局。
