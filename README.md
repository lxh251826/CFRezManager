# CF Rez Manager

![主界面](image-1.png)
![预览界面](image.png)

CF Rez Manager 是一个 Windows WPF 工具，用于浏览、搜索、解包和重新打包 LithTech / CrossFire 的 `.rez` 资源包，也可以直接预览扫描目录里的散文件资源。

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
bin\Debug\net8.0-windows7.0\CFRezManager.exe
```

## 发布

项目包含 GitHub Actions 发布流程。推送 `v*` 标签后会自动构建 Windows x64 自包含单文件包，并创建 GitHub Release。

```powershell
git tag v1.1.4
git push origin main
git push origin v1.1.4
```

## 浏览 REZ 资源包

1. 启动程序。
2. 选择包含 `.rez` 文件或散文件资源的文件夹。
3. 双击普通文件夹、REZ 包、REZ 内部目录或支持预览的文件进入或打开。
4. 使用顶部面包屑导航返回上级或跳转到任意父级位置。

顶部语言选择框可以在 `中文` 和 `English` 之间切换。程序会记住语言、视图大小、扫描目录、打包目录、导出目录和保存位置。

搜索框会在首次输入时建立内存索引，之后可以快速筛选已经扫描到的文件、目录和 REZ 内部路径。多个关键词用空格分隔时，需要全部命中才会显示结果。

右下角 `大小` 滑条可以切换显示方式：较小时使用列表视图并显示路径和大小，较大时切回平铺图标视图。鼠标悬停在文件、目录或 REZ 项目上时，会显示类型、路径、大小、来源、MD5、偏移等信息。

在搜索结果或普通目录里选中文件后，可以通过右键菜单 `定位到文件` 跳回其所在目录并选中该文件；右键 `复制名称` 可复制单个或多个选中项名称。

## 预览能力

- PNG、JPG、BMP、GIF、TIFF、DDS、TGA、DTX 以及支持的 CrossFire BIN 图片会懒加载缩略图。
- DDS 支持 DXT1/DXT3/DXT5 块压缩纹理，也支持常见未压缩 RGB、RGBA 和亮度纹理。
- DTX 和 TGA 支持普通纹理、CrossFire 常见 LZMA 压缩纹理，以及部分缺失或错位 TGA 头的原始像素纹理。
- 常见图片格式支持普通文件和 LZMA 压缩文件预览。
- CrossFire BIN 图片支持新版 `16 字节头 + Zstandard + BGRA32 像素` 容器，也保留旧的 CF10/XOR 图片外壳兜底。已验证的 UI 样本会识别为 `CrossFireImageBinZstd`。
- DDS、DTX、TGA 和常见图片格式可以打开原始尺寸预览窗口。图片不会被强制拉伸，过大时可以滚动查看。
- 图片预览窗口支持同目录/同列表内的上一张、下一张导航，可用按钮或左右方向键切换。
- WAV、OGG、MP3 音频支持元数据解析、波形缩略图和双击预览。
- 音频预览窗口支持曲目清单快速切换、上一首/下一首、播放/暂停/停止、进度拖动、音量调节，以及接近 PotPlayer 风格的动态频谱。
- OGG 会通过内置 Vorbis 解码路径转换为临时 WAV 后播放，避免 WPF 直接播放失败。
- FMOD `.bank` 支持 LZMA 外壳解码、RIFF/FEV 元数据预览、内嵌 FSB5 音频块列表、内置 Fmod5Sharp 播放预览、音频缩略图、播放器曲目清单，以及右键 `解码 BANK...` 导出 decoded bank 和原始 FSB5 块；少数内置解码不支持的流可回退到 `vgmstream-cli.exe`。大型 BANK 会优先加载首条可播放音频，后续流在后台递进追加。
- 音频和资源缩略图会在角标显示 `RAW`、`LZMA`、`DXT`、`TXT` 等存储/解码状态。
- CFG 支持批量扫描和分类。文本 CFG 会提取贴图引用，二进制 RGB 条带型 CFG 会生成缩略图/预览，并与启动器或保护组件配置分开报告。
- SPR 支持 LithTech 动态精灵解析和动画预览。程序会读取 SPR 中记录的帧率和 DTX 帧路径，从同一个 REZ 包或已解包目录中加载 DTX 帧并自动播放；找不到帧时会回退到帧路径文本预览。
- LTC 支持缩略图和双击预览。内置解码器会处理普通 LTC、LZMA 压缩 LTC，以及 CrossFire 常见的 `54 83 B2 E1` 头和外层 XOR。解码后的 LTA 文本可以直接查看；如果内容是 LithTech 模型，会尝试渲染模型缩略图并打开独立模型预览窗口。
- LTB 支持更多二进制 mesh 布局、mesh 表偏移和顶点布局变体，能直接预览更多 CrossFire 导出的模型。
- LTA 支持 `lt-model` 模型文本，也支持 `world` 地图文本，`polyhedron` 的点表和面索引会被转换为可预览网格。
- DAT 支持常见 CrossFire 地图和对象预览。LithTech world DAT v85 可以渲染地图模型缩略图并打开模型预览窗口；CrossFire 对象 DAT 会解码为文本预览，当前支持 `Zoneman`、`EnvSound`、`MovePath` 和 `CameraAnimation`。
- CFT、FCF、FXF、FXO、NAV、APF、REF、TXT 以及部分 WAVE 资源可以解码为可读文本或元数据预览，包含常见 LZMA 压缩形式。
- CrossFire UI 脚本 `.bin` 文件支持结构化二进制配置表预览，包括 `WeaponPreview` 条目和 `WeaponModel` 表。
- LZMA 压缩资源会在缩略图角标显示 `LZMA`。
- 生成过的缩略图会缓存在当前 Windows 用户目录下。再次打开相同散文件或未变化的 REZ 内部资源时，可以直接复用缓存 PNG，避免重复解码和渲染。资源格式变化后，可以使用 `清缩略图` 清除过期缓存 PNG。

## 模型预览操作

- 鼠标左键点击模型窗口：进入自由视角。
- 鼠标移动：调整视角方向。
- `W` / `A` / `S` / `D`：前后左右移动。
- `Shift`：加速移动。
- 鼠标滚轮：沿当前视线方向前进或后退。
- 鼠标右键或 `Esc`：退出自由视角。
- `Reset View`：重置相机位置和方向。

## 鼠标快捷操作

- 鼠标后退键：回到上一个浏览位置。
- 鼠标前进键：前进到下一个浏览位置。
- 按住鼠标左键拖动：框选可见文件或目录。
- 拖选时按住 `Ctrl`：切换选区。
- 拖选时按住 `Shift`：追加选区。

## 解包文件

点击 `全部导出...` 可以导出当前扫描范围内所有 REZ 包中的全部文件。

只导出指定文件或目录：

1. 选中一个文件、文件夹、REZ 包或 REZ 内部目录。
2. 需要多选时，使用 `Ctrl` 或 `Shift` 选择多个项目。
3. 右键选中的项目。
4. 选择 `导出此项...` 或 `导出 N 个选中项...`。
5. 选择输出文件夹。

导出的文件会保留 REZ 内部目录结构。

## 重新打包为 REZ

点击 `打包文件夹...` 可以把普通 Windows 文件夹打包为新的 `.rez` 文件。

1. 准备一个包含目标文件和子目录的文件夹。
2. 点击 `打包文件夹...`。
3. 选择源文件夹。
4. 选择输出 `.rez` 文件路径。

被选中文件夹的内容会成为新 REZ 包的根目录内容。程序会加密目录表，并重新计算每个文件的 MD5。

## 当前格式说明

- 文件数据在 REZ 中直接存放。
- REZ 目录表由 `RezCrypto` 负责解密和加密。
- 目录表中的文件 MD5 与原始文件数据的 MD5 一致。
- 重新打包时，文件名和目录名目前要求为 ASCII。
- 重新打包时，文件扩展名需要为 1 到 4 个字符。
- 从文件夹创建新 REZ 会保留内容和目录结构，但不会复制原包的字节级布局、偏移、时间戳或整包 MD5。

## 命令行工具

WPF 程序也提供几个面向资产流程的批处理入口：

```powershell
dotnet .\bin\Release\net8.0-windows7.0\CFRezManager.dll --export-obj --root "F:\Game\CrossFire" --model "PV-AK47_Balance" --output ".\out\PV-AK47_Balance.obj"
dotnet .\bin\Release\net8.0-windows7.0\CFRezManager.dll --scan-cfg --root "C:\Extracted\cfg"
dotnet .\bin\Release\net8.0-windows7.0\CFRezManager.dll --decode-cfg --root "C:\Extracted\cfg"
```

- `--export-obj` 会把 LithTech 模型部件导出为 OBJ/MTL，自动合并带序号的同组模型，按 CFG 映射解析贴图候选，并在 OBJ 旁写出贴图和映射报告。
- `--scan-cfg` 会扫描 CFG，识别可读文本和 LZMA 文本，提取贴图引用，并输出 TXT/CSV 报告。
- `--decode-cfg` 会重试失败 CFG，支持可还原文本落盘、二进制 RGB 条带预览图导出，以及高熵启动器/保护组件配置分类。

## v1.1.4 更新

- 新增 CrossFire BIN 图片预览，支持更新后 UI 资源使用的 `16 字节头 + Zstandard + BGRA32 像素` 容器。
- 保留 CF10/XOR 外壳图片 BIN 的兜底解码路径。
- 新增 CrossFire UI 脚本 BIN 配置表结构化预览，包括 `WeaponPreview` 条目和 `WeaponModel` 表。
- 模型贴图引用和 CFG 贴图映射流程现在也会把 BIN 作为贴图候选。
- 新增缩略图缓存清理按钮，资源格式变化后可以移除旧的缓存预览图。
- 新版 BIN 图片解码器已用 2,671 个 UI 样本全量验证，2,671 个全部成功生成预览，失败 0 个。
- 更新中英文说明书和 GitHub Release 文案，版本号提升到 `v1.1.4`，并关闭 #1。

## v1.1.3 更新

- 新增 LithTech 模型 OBJ/MTL 导出，支持同组模型部件合并、贴图复制/报告生成，以及 Blender 导入所需的映射候选报告。
- 新增 CFG 贴图索引和扫描流程，可用可读 CFG 建立模型到贴图的关联，并批量输出贴图引用报告。
- 新增 CFG 失败解码分类：二进制 RGB 条带型 CFG 现在可生成缩略图/预览，高熵启动器/保护组件配置会与模型材质 CFG 分开标记。
- 改进 CrossFire LTA/LTB/LTC/DAT 模型的贴图加载、预览和导出流程。
- 更新中英文说明书和 GitHub Release 文案，版本号提升到 `v1.1.3`。

## v1.1.2 更新

- 右键菜单新增 `定位到文件`，可从搜索结果或当前列表跳转到选中项所在目录并聚焦目标文件。
- 右键菜单新增 `复制名称`，支持复制单个选中项名称，或将多个选中项名称按行复制到剪贴板，并增加剪贴板繁忙重试提示。
- BANK 预览和压缩资源前缀读取改为复用共享 `LzmaAloneDecoder`，统一普通资源与 BANK 的 LZMA 解码、前缀解码和流式解码路径。
- 更新中英文说明书和 GitHub Release 文案，版本号提升到 `v1.1.2`。

## v1.1.1 更新

- 优化大型 FMOD `.bank` 预览：首条流准备好后即可打开播放器，剩余 FSB5 音频流会在后台递进追加。
- 为大型压缩 BANK 增加更快的 LZMA 前缀解码路径，减少音频预览弹出前的等待。
- 模型缩略图增加几何降采样，大型 DAT/LTB/LTA/LTC 模型会用受控网格生成缩略图，避免每次绘制全部三角面。
- 新增缩略图磁盘缓存，使用源文件或 REZ 内部文件元数据作为缓存键，未变化资源可以跨目录重扫复用上次生成的预览图。
- 优化音频曲目列表样式、加载状态显示、点击即播行为和深色滚动条颜色。
- 版本号提升到 `v1.1.1`。

## v1.1.0 更新

- 新增 FMOD `.bank` 支持：可解开常见 LZMA 外壳，预览 RIFF/FEV 元数据和内嵌 FSB5 块，并通过右键 `解码 BANK...` 导出 decoded bank 与原始 FSB5 文件。
- `.bank` 内含音频时会优先走内置 Fmod5Sharp 解码路径，支持 Vorbis、PCM、FADPCM 等常见 FSB5 流；不支持的流再尝试 `vgmstream-cli.exe` 兜底。
- `.bank` 音频缩略图复用普通音频波形样式，双击可打开播放器，并在播放器右侧显示曲目清单，方便在几百条内部音频流之间快速切换。
- 音频播放器增加曲目列表、保存音量和循环模式，并修正列表切换时的白底闪烁和控制栏不贴底问题。
- 模型预览增加纹理路径、UV 坐标和本地/REZ 内贴图解析能力，带贴图的 LTB/LTA/world 模型可以显示更接近原始资源的外观。
- 更新中英文说明书和 GitHub Release 文案，版本号提升到 `v1.1.0`。

## v1.0.0 正式版更新

- 发布首个正式版 `v1.0.0`，将当前 REZ 浏览、搜索、解包、重新打包和多格式预览能力整理为稳定版本。
- SPR 动态精灵预览支持从 REZ 包和本地解包目录加载 DTX 帧，缩略图会优先使用真实帧画面，双击预览可自动播放动画。
- 图片/SPR 预览窗口支持上一项/下一项导航、播放/暂停和帧选择控件的中英文切换，语言变化后窗口文案会同步刷新。
- 本地散文件 SPR 也可以解析并查找相邻资源树中的帧文件，方便直接查看已解包资源。
- 音频预览窗口改为无系统标题栏样式，并调整频谱峰值下落动画，显示更贴近播放器视觉效果。
- 为程序加入应用图标，并更新中英文说明书与 GitHub Release 双语文案。

## v0.11.0 更新

- 新增 DDS 缩略图和原始尺寸预览支持，覆盖 DXT1/DXT3/DXT5 块压缩纹理以及常见未压缩 RGB、RGBA、亮度纹理。
- 改进 DDS 纹理读取，能识别 mipmap 和 cubemap 数据长度，缩略图会按最大边长缩放以减少内存占用。
- 常见栅格图片新增 LZMA 压缩资源预览路径，并在缩略图角标和悬停信息中区分 `RAW`、`LZMA`、`DXT` 等存储状态。
- 独立预览工具和模型/LTC 错误提示跟随保存的中英文语言设置。
- 模型预览自由视角改用 WPF 渲染帧驱动，移动更平滑，关闭窗口时也会正确释放刷新订阅。
- 更新中英文说明书和 GitHub Release 文案，并加入支持项目二维码。

## v0.10.0 更新

- 新增散文件浏览支持，扫描目录里的资源也可以直接预览。
- 新增 WAV、OGG、MP3 音频元数据解析、波形缩略图、上一首/下一首导航和 PotPlayer 风格音频预览窗口。
- 新增 OGG 内置转换播放路径，并使用 NAudio/MediaFoundation 支持 MP3 频谱分析。
- 优化音频频谱绘制，改用 `WriteableBitmap` 像素缓冲提升细格子频谱性能，并修正播放结束后悬浮峰值停在空中的问题。
- 新增更多 CrossFire 资源文本解码，包括 CFT/FCF/FXF/FXO/NAV/APF/REF 和 WAVE 头信息。
- 更新中英文说明书和 GitHub Release 文案。

## v0.9.0 更新

- 扩展 LTB 二进制模型解析，支持更多 mesh 表位置、顶点布局、后置数据和 mesh 类型变体。
- 新增 LTA world/map 文本解析，可以把 `polyhedron`、`pointlist` 和 `editpoly/f` 面索引转换为地图网格预览。
- 改进模型预览窗口，新增自由视角、WASD 移动、Shift 加速、滚轮移动和更稳定的视角重置。
- 改进图片预览窗口，支持上一张/下一张导航，并优化缩略图解码尺寸以保留原始比例。
- 修订中英文说明书和 GitHub Release 文案。

## 项目文件

- `MainWindow.xaml` / `MainWindow.xaml.cs`：WPF 界面和用户操作逻辑。
- `RezArchiveReader.cs`：读取和解包 REZ。
- `RezArchiveWriter.cs`：从文件夹打包生成新 REZ。
- `RezCrypto.cs`：REZ 目录表解密和加密逻辑。
- `ExplorerItem.cs`：程序内的文件夹、散文件和资源项目模型。
- `LocalizedText.cs`：独立预览工具和解码错误提示的中英文文案。
- `PreviewTool.cs`：独立预览工具入口。
- `AudioMetadataDecoder.cs` / `AudioThumbnailRenderer.cs`：音频元数据和波形缩略图渲染。
- `AudioPreviewWindow.xaml` / `AudioPreviewWindow.xaml.cs`：独立音频预览窗口和频谱绘制。
- `AudioSpectrumAnalyzer.cs` / `OggVorbisWaveDecoder.cs`：音频频谱分析和 OGG 转 WAV 播放转换。
- `FmodBankDecoder.cs` / `FmodBankAudioPreviewDocumentFactory.cs`：FMOD BANK 解码、导出和内嵌 FSB5 音频预览。
- `CfgScanCommand.cs` / `CfgDecodeCommand.cs` / `CfgBinaryStripDecoder.cs`：CFG 批量扫描、失败解码分类和二进制 RGB 条带预览。
- `LzmaAloneDecoder.cs`：全局 LZMA 解码、前缀解码和流式解码路径，BANK 预览也复用这一套实现。
- `BankLzmaAloneDecoder.cs`：保留的旧 BANK LZMA 解码实现，用于回退和对比。
- `ResourceTextDecoder.cs`：更多文本类 CrossFire 资源解码。
- `DtxThumbnailDecoder.cs` / `TgaThumbnailDecoder.cs` / `DdsThumbnailDecoder.cs`：图片和纹理预览解码。
- `CrossFireImageBinDecoder.cs`：CrossFire BIN 图片解码，支持 Zstandard 压缩 BGRA32 图片和 CF10/XOR 图片外壳。
- `CrossFireScriptBinDecoder.cs`：CrossFire UI 脚本 BIN 表解码，支持 WeaponPreview 和 WeaponModel 资源。
- `LithTechSpriteDecoder.cs` / `LithTechSpritePreviewLoader.cs`：SPR 动态精灵解析和动画帧加载。
- `CrossFireLtcDecoder.cs` / `LithTechLtcNativeDecoder.cs`：LTC 文本和模型预览解码。
- `LithTechModelDecoder.cs` / `LithTechModelThumbnailRenderer.cs` / `LithTechModelSceneBuilder.cs` / `LithTechModelTextureLoader.cs`：LithTech 模型、LTB/LTA world 解析、贴图查找和渲染。
- `LithTechObjExporter.cs` / `LithTechObjExportCommand.cs` / `LithTechModelTextureConfigIndex.cs` / `LithTechModelPartGrouper.cs` / `LithTechTextureMappingScanner.cs`：OBJ 导出、模型部件合并、CFG 贴图映射和映射报告生成。
- `LithTechThumbnailGeometryReducer.cs`：为大型模型和地图网格生成更快的缩略图与预览启动几何。
- `CrossFireDatDecoder.cs` / `LithTechWorldDatDecoder.cs`：DAT 对象文本和 LithTech world 地图预览解码。
- `TextThumbnailRenderer.cs`：文本类资源的缩略图渲染。
- `ThumbnailDiskCache.cs`：保存已生成的缩略图 PNG，未变化文件可跳过重复解码。
- `VirtualizingWrapPanel.cs`：虚拟化图标网格布局。


## 制作不易，鼓励一下

如果这个工具帮到了你，可以请我喝杯咖啡。

![支持项目二维码](afc08a3298aeb1fa378e9d89ca34e35a.jpg)
