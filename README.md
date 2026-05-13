# CF Rez Manager
![alt text](image-1.png)

CF Rez Manager 是一个 Windows WPF 工具，用于浏览、解包和重新打包 LithTech / CF 的 `.rez` 资源包。

英文说明见 [README.en.md](README.en.md)。

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

## 浏览 REZ 资源包

1. 启动程序。
2. 选择包含 `.rez` 文件的文件夹。
3. 双击普通文件夹、REZ 包或 REZ 内部目录进入。
4. 使用顶部面包屑跳转回上级或任意父级位置。

顶部语言选择框可以在 `中文` 和 `English` 之间切换，按钮、右键菜单、状态栏和常用对话框提示会随之更新。

程序会记住上次选择的语言、右下角大小滑条，以及扫描、打包、导出、保存 REZ 时选择过的目录。下次启动或再次打开对应弹窗时，会优先恢复上次使用的设置和位置。

顶部搜索框会在首次输入时建立内存索引，之后会像 Everything 一样在已扫描的文件、目录和 REZ 内部路径中快速筛选。多个关键字用空格分隔时，需要全部命中才会显示结果。

右下角 `大小` 滑条可以切换显示方式：缩小时使用列表视图并显示路径和大小，放大时切回平铺图标视图。鼠标悬停在文件、目录或 REZ 项上时，会显示类型、路径、大小、来源、MD5、偏移等可用信息。

鼠标快捷操作：

- 鼠标后退键：回到上一个浏览位置。
- 鼠标前进键：前进到下一个浏览位置。
- 按住鼠标左键拖动：框选可见文件或目录；按住 `Ctrl` 可切换选区，按住 `Shift` 可追加选区。

## 解包文件

点击 `Extract All...` 可以导出当前扫描范围内所有 REZ 包中的全部文件。

只导出指定文件或目录：

1. 选中一个文件、文件夹、REZ 包或 REZ 内部目录。
2. 需要多选时，使用 `Ctrl` 或 `Shift` 选择多个项目。
3. 右键选中项。
4. 选择 `Extract This Item...` 或 `Extract N Selected Items...`。
5. 选择输出文件夹。

导出的文件会保留 REZ 内部目录结构。

## 重新打包为 REZ

点击 `Pack Folder...` 可以把一个普通 Windows 文件夹打包成新的 `.rez` 文件。

1. 准备一个文件夹，里面放入需要写入 REZ 的文件和子目录。
2. 点击 `Pack Folder...`。
3. 选择源文件夹。
4. 选择输出 `.rez` 文件路径。

被选中文件夹的内部内容会成为新 REZ 包的根目录内容。程序会加密目录表，并重新计算每个文件的 MD5。

## 当前格式说明

- 文件数据在 REZ 中直接存放。
- REZ 目录表由 `RezCrypto` 负责解密和加密。
- 目录表中的文件 MD5 与原始文件数据的 MD5 一致。
- 重新打包时，文件名和目录名目前要求为 ASCII。
- 重新打包时，文件扩展名需要为 1 到 4 个字符。
- 从文件夹创建新 REZ 会保留内容和目录结构，但不会尝试复刻原包的字节级布局、偏移、时间戳或整包 MD5。

## 项目文件

- `MainWindow.xaml` / `MainWindow.xaml.cs`：WPF 界面和用户操作逻辑。
- `RezArchiveReader.cs`：读取和解包 REZ。
- `RezArchiveWriter.cs`：从文件夹打包生成新 REZ。
- `RezCrypto.cs`：REZ 目录表解密和加密逻辑。
- `ExplorerItem.cs`：程序内的文件夹和资源项模型。
- `VirtualizingWrapPanel.cs`：虚拟化图标网格布局。
