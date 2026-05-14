using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace CFRezManager;

public sealed record LithTechModelDocument(
    string Name,
    IReadOnlyList<LithTechMesh> Meshes,
    string StorageDescription,
    int SourceByteCount,
    int DecodedByteCount)
{
    public int VertexCount => Meshes.Sum(mesh => mesh.Vertices.Count);

    public int TriangleCount => Meshes.Sum(mesh => mesh.TriangleIndices.Count / 3);
}

public sealed record LithTechMesh(
    string Name,
    IReadOnlyList<LithTechVector3> Vertices,
    IReadOnlyList<int> TriangleIndices);

public readonly record struct LithTechVector3(double X, double Y, double Z);

internal static class LithTechModelDecoder
{
    private const int MaxParseDepth = 256;
    private const int MaxLtbMeshCount = 4096;

    // Offsets follow Cote-Duke's LTB2X loader notes for LithTech Jupiter LTB meshes.
    private const int LtbCommandLineLengthOffset = 84;
    private const int LtbMeshCountOffset = 86 + 8;
    private const int LtbFirstMeshOffset = 86 + 12;

    private const int LtbMeshVertexCountOffset = 49;
    private const int LtbMeshFaceCountOffset = 53;
    private const int LtbMeshTypeHeadOffset = 57;
    private const int LtbMeshTypeOffset = 61;
    private const int LtbFirstVertexOffset = 83;
    private const int LtbFirstVertexDoubleStartOffset = 85;

    private const ushort LtbMeshTypeNotSkinned = 1;
    private const ushort LtbMeshTypeExtraFloat = 2;
    private const ushort LtbMeshTypeSkinnedAlt = 3;
    private const ushort LtbMeshTypeSkinned = 4;
    private const ushort LtbMeshTypeTwoExtraFloats = 5;

    static LithTechModelDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static bool IsCandidate(string extension)
    {
        return string.Equals(extension, "lta", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, "ltb", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryDecode(byte[] data, string fallbackName, out LithTechModelDocument? document)
    {
        return TryDecode(data, fallbackName, "lta", out document, out _);
    }

    public static bool TryDecode(
        byte[] data,
        string fallbackName,
        string extension,
        out LithTechModelDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data);
        if (prepared is null)
        {
            errorMessage = "无法解开模型外层压缩。";
            return false;
        }

        if (string.Equals(extension, "ltb", StringComparison.OrdinalIgnoreCase))
        {
            string nativeStorageDescription = ReferenceEquals(prepared, data) ? "LTB binary" : "LZMA-compressed LTB";
            if (TryParseLtbBinary(prepared, fallbackName, nativeStorageDescription, data.Length, prepared.Length, out document, out errorMessage))
            {
                return true;
            }

            string? nativeError = errorMessage;
            string? ltaText = TryConvertLtbToLtaText(prepared, fallbackName, out string? converterError);
            if (ltaText is null)
            {
                errorMessage = string.IsNullOrWhiteSpace(converterError)
                    ? nativeError
                    : $"{nativeError} {converterError}";
                return false;
            }

            string ltbStorageDescription = ReferenceEquals(prepared, data)
                ? "LTB -> LTA"
                : "LZMA-compressed LTB -> LTA";
            return TryParseLtaText(ltaText, fallbackName, ltbStorageDescription, data.Length, prepared.Length, out document, out errorMessage);
        }

        string storageDescription = ReferenceEquals(prepared, data) ? "LTA text" : "LZMA-compressed LTA";
        string text = Encoding.ASCII.GetString(prepared);
        return TryParseLtaText(text, fallbackName, storageDescription, data.Length, prepared.Length, out document, out errorMessage);
    }

    private static bool TryParseLtbBinary(
        byte[] rawLtb,
        string fallbackName,
        string storageDescription,
        int sourceByteCount,
        int decodedByteCount,
        out LithTechModelDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;
        ReadOnlySpan<byte> data = rawLtb;

        if (!TryReadUInt16(data, LtbCommandLineLengthOffset, out ushort commandLineLength))
        {
            errorMessage = "LTB 文件太短，无法读取文件头。";
            return false;
        }

        int meshCountOffset = LtbMeshCountOffset + commandLineLength;
        int position = LtbFirstMeshOffset + commandLineLength;
        if (!TryReadUInt32(data, meshCountOffset, out uint meshCount))
        {
            errorMessage = "LTB 文件头不完整，无法读取 mesh 数量。";
            return false;
        }

        if (meshCount == 0 || meshCount > MaxLtbMeshCount)
        {
            errorMessage = $"LTB 不是可识别的模型数据，mesh 数量异常: {meshCount}。";
            return false;
        }

        var meshes = new List<LithTechMesh>((int)Math.Min(meshCount, 64));
        for (int meshIndex = 0; meshIndex < meshCount; meshIndex++)
        {
            if (!TryReadLtbString(data, ref position, out string meshName))
            {
                errorMessage = $"LTB mesh #{meshIndex + 1} 名称数据不完整。";
                return false;
            }

            int meshBaseOffset = position;
            if (!TryReadUInt16(data, meshBaseOffset + LtbMeshVertexCountOffset, out ushort vertexCount) ||
                !TryReadUInt16(data, meshBaseOffset + LtbMeshFaceCountOffset, out ushort faceCount))
            {
                errorMessage = $"LTB mesh '{meshName}' 头部不完整。";
                return false;
            }

            if (!TryGetLtbMeshLayout(data, meshBaseOffset, out LtbMeshLayout layout, out ushort meshType))
            {
                errorMessage = $"LTB mesh '{meshName}' 使用了暂不支持的类型: {meshType}。";
                return false;
            }

            if (!TryGetLtbVertexDataOffset(data, meshBaseOffset, out int vertexDataOffset))
            {
                errorMessage = $"LTB mesh '{meshName}' 顶点数据不完整。";
                return false;
            }

            int indexCount = faceCount * 3;
            long vertexByteCount = (long)vertexCount * layout.VertexStride;
            long indexByteCount = (long)indexCount * sizeof(ushort);
            if (vertexByteCount > int.MaxValue ||
                indexByteCount > int.MaxValue ||
                vertexDataOffset < 0 ||
                vertexDataOffset + vertexByteCount + indexByteCount > data.Length)
            {
                errorMessage = $"LTB mesh '{meshName}' 几何数据越界。";
                return false;
            }

            var vertices = new List<LithTechVector3>(vertexCount);
            int readPosition = vertexDataOffset;
            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                if (!TryReadSingle(data, readPosition, out float x) ||
                    !TryReadSingle(data, readPosition + 4, out float y) ||
                    !TryReadSingle(data, readPosition + 8, out float z))
                {
                    errorMessage = $"LTB mesh '{meshName}' 顶点 #{vertexIndex + 1} 数据不完整。";
                    return false;
                }

                vertices.Add(new LithTechVector3(x, y, z));
                readPosition += 12;

                if (layout.IncludeWeights)
                {
                    readPosition += 12;
                }

                readPosition += 12; // normal
                readPosition += layout.PostNormalByteCount;
                readPosition += 8; // texture coordinates
            }

            var triangleIndices = new List<int>(indexCount);
            for (int index = 0; index < indexCount; index++)
            {
                if (!TryReadUInt16(data, readPosition, out ushort triangleIndex))
                {
                    errorMessage = $"LTB mesh '{meshName}' 索引数据不完整。";
                    return false;
                }

                if (triangleIndex >= vertexCount)
                {
                    errorMessage = $"LTB mesh '{meshName}' 索引 {triangleIndex} 超出顶点范围。";
                    return false;
                }

                triangleIndices.Add(triangleIndex);
                readPosition += sizeof(ushort);
            }

            if (!TrySkipLtbMeshPostData(data, layout, ref readPosition))
            {
                errorMessage = $"LTB mesh '{meshName}' 后置数据不完整。";
                return false;
            }

            position = readPosition;
            if (vertices.Count > 0 && triangleIndices.Count >= 3)
            {
                string displayName = string.IsNullOrWhiteSpace(meshName) ? $"Mesh {meshIndex + 1}" : meshName;
                meshes.Add(new LithTechMesh(displayName, vertices, triangleIndices));
            }
        }

        if (meshes.Count == 0)
        {
            errorMessage = "LTB 中没有可预览的 mesh 几何数据。";
            return false;
        }

        document = new LithTechModelDocument(fallbackName, meshes, storageDescription, sourceByteCount, decodedByteCount);
        return true;
    }

    private static bool TryGetLtbMeshLayout(
        ReadOnlySpan<byte> data,
        int meshBaseOffset,
        out LtbMeshLayout layout,
        out ushort meshType)
    {
        layout = default;
        meshType = 0;

        if (!TryReadUInt16(data, meshBaseOffset + LtbMeshTypeHeadOffset, out ushort meshTypeHead))
        {
            return false;
        }

        if (meshTypeHead == LtbMeshTypeSkinnedAlt)
        {
            meshType = LtbMeshTypeTwoExtraFloats;
        }
        else if (!TryReadUInt16(data, meshBaseOffset + LtbMeshTypeOffset, out meshType))
        {
            return false;
        }

        layout = meshType switch
        {
            LtbMeshTypeNotSkinned => new LtbMeshLayout(IncludeWeights: false, IncludePostData: false, PostNormalByteCount: 0),
            LtbMeshTypeExtraFloat => new LtbMeshLayout(IncludeWeights: false, IncludePostData: true, PostNormalByteCount: sizeof(float)),
            LtbMeshTypeSkinned => new LtbMeshLayout(IncludeWeights: true, IncludePostData: true, PostNormalByteCount: 0),
            LtbMeshTypeSkinnedAlt => new LtbMeshLayout(IncludeWeights: true, IncludePostData: true, PostNormalByteCount: 0),
            LtbMeshTypeTwoExtraFloats => new LtbMeshLayout(IncludeWeights: false, IncludePostData: true, PostNormalByteCount: sizeof(float) * 2),
            _ => default
        };

        return meshType is LtbMeshTypeNotSkinned or
               LtbMeshTypeExtraFloat or
               LtbMeshTypeSkinnedAlt or
               LtbMeshTypeSkinned or
               LtbMeshTypeTwoExtraFloats;
    }

    private static bool TryGetLtbVertexDataOffset(ReadOnlySpan<byte> data, int meshBaseOffset, out int vertexDataOffset)
    {
        vertexDataOffset = 0;
        int markerOffset = meshBaseOffset + LtbFirstVertexOffset;
        if (markerOffset < 0 || markerOffset + 4 > data.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> marker = data[markerOffset..(markerOffset + 4)];
        int relativeOffset = marker[0] == 0 && marker[1] == 0 && marker[2] != 0 && marker[3] != 0
            ? LtbFirstVertexDoubleStartOffset
            : LtbFirstVertexOffset;

        vertexDataOffset = meshBaseOffset + relativeOffset;
        return vertexDataOffset >= 0 && vertexDataOffset < data.Length;
    }

    private static bool TrySkipLtbMeshPostData(ReadOnlySpan<byte> data, LtbMeshLayout layout, ref int position)
    {
        if (!layout.IncludePostData)
        {
            position += sizeof(ushort);
            return position <= data.Length;
        }

        if (!TryReadUInt32(data, position, out uint sectionCount))
        {
            return false;
        }

        long nextPosition = (long)position + sizeof(uint) + (long)sectionCount * 12;
        if (nextPosition < 0 || nextPosition >= data.Length)
        {
            return false;
        }

        int finalSectionSize = data[(int)nextPosition];
        nextPosition += 1L + finalSectionSize;
        if (nextPosition > data.Length)
        {
            return false;
        }

        position = (int)nextPosition;
        return true;
    }

    private static bool TryReadLtbString(ReadOnlySpan<byte> data, ref int position, out string value)
    {
        value = string.Empty;
        if (!TryReadUInt16(data, position, out ushort length))
        {
            return false;
        }

        position += sizeof(ushort);
        if (position < 0 || position + length > data.Length)
        {
            return false;
        }

        value = DecodeLtbString(data.Slice(position, length));
        position += length;
        return true;
    }

    private static string DecodeLtbString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        try
        {
            return Encoding.GetEncoding(949).GetString(bytes).TrimEnd('\0');
        }
        catch
        {
            return Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        }
    }

    private static bool TryReadUInt16(ReadOnlySpan<byte> data, int offset, out ushort value)
    {
        value = 0;
        if (offset < 0 || offset + sizeof(ushort) > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, sizeof(ushort)));
        return true;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> data, int offset, out uint value)
    {
        value = 0;
        if (offset < 0 || offset + sizeof(uint) > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));
        return true;
    }

    private static bool TryReadSingle(ReadOnlySpan<byte> data, int offset, out float value)
    {
        value = 0;
        if (offset < 0 || offset + sizeof(float) > data.Length)
        {
            return false;
        }

        int bits = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, sizeof(float)));
        value = BitConverter.Int32BitsToSingle(bits);
        return true;
    }

    private static bool TryParseLtaText(
        string text,
        string fallbackName,
        string storageDescription,
        int sourceByteCount,
        int decodedByteCount,
        out LithTechModelDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        if (!text.Contains("(lt-model", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "模型数据不是可识别的 LTA 文本。";
            return false;
        }

        try
        {
            var parser = new LtaParser(text);
            LtaList root = parser.ParseRoot();
            List<LithTechMesh> meshes = FindListsByHead(root, "mesh")
                .Select(ParseMesh)
                .Where(mesh => mesh is not null)
                .Cast<LithTechMesh>()
                .ToList();

            if (meshes.Count == 0)
            {
                return false;
            }

            string name = GetAtomValue(root.Items.FirstOrDefault()) ?? fallbackName;
            document = new LithTechModelDocument(name, meshes, storageDescription, sourceByteCount, decodedByteCount);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static string? TryConvertLtbToLtaText(byte[] rawLtb, string fallbackName, out string? errorMessage)
    {
        errorMessage = null;

        string? unpackerPath = ResolveModelUnpackerPath();
        if (unpackerPath is null)
        {
            errorMessage = "缺少 LTB 转换器。请把 Model_Unpacker.exe 放到程序目录的 tools 文件夹，或设置 CFREZ_MODEL_UNPACKER 环境变量。";
            return null;
        }

        string workingDirectory = Path.Combine(Path.GetTempPath(), "CFRezManager", "LtbConvert", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        string baseName = Path.GetFileNameWithoutExtension(fallbackName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "model";
        }

        string inputPath = Path.Combine(workingDirectory, $"{baseName}.raw.ltb");
        string outputPath = Path.Combine(workingDirectory, $"{baseName}.lta");
        File.WriteAllBytes(inputPath, rawLtb);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = unpackerPath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("d3d");
            startInfo.ArgumentList.Add("-input");
            startInfo.ArgumentList.Add(inputPath);
            startInfo.ArgumentList.Add("-output");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add("-verbose");

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                errorMessage = "无法启动 Model_Unpacker.exe。";
                return null;
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                errorMessage = string.IsNullOrWhiteSpace(detail)
                    ? $"Model_Unpacker.exe 转换失败，退出码 {process.ExitCode}。"
                    : detail.Trim();
                return null;
            }

            return File.ReadAllText(outputPath, Encoding.ASCII);
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    private static string? ResolveModelUnpackerPath()
    {
        string? configured = Environment.GetEnvironmentVariable("CFREZ_MODEL_UNPACKER");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "tools", "Model_Unpacker.exe"),
            Path.Combine(AppContext.BaseDirectory, "Model_Unpacker.exe"),
            Path.Combine(Environment.CurrentDirectory, "tools", "Model_Unpacker.exe"),
            Path.Combine(Environment.CurrentDirectory, "Model_Unpacker.exe")
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Temporary conversion files are best-effort cleanup only.
        }
    }

    private static LithTechMesh? ParseMesh(LtaList meshNode)
    {
        string name = GetAtomValue(meshNode.Items.Skip(1).FirstOrDefault()) ?? "Mesh";
        LtaList? vertexNode = meshNode.Children.FirstOrDefault(child => IsHead(child, "vertex"));
        LtaList? triNode = meshNode.Children.FirstOrDefault(child => IsHead(child, "tri-fs"));
        if (vertexNode is null || triNode is null)
        {
            return null;
        }

        List<LithTechVector3> vertices = ParseVector3List(vertexNode);
        List<int> triangleIndices = ParseIntList(triNode)
            .Where(index => index >= 0 && index < vertices.Count)
            .ToList();

        int usableIndexCount = triangleIndices.Count - triangleIndices.Count % 3;
        if (vertices.Count == 0 || usableIndexCount < 3)
        {
            return null;
        }

        if (usableIndexCount != triangleIndices.Count)
        {
            triangleIndices.RemoveRange(usableIndexCount, triangleIndices.Count - usableIndexCount);
        }

        return new LithTechMesh(name, vertices, triangleIndices);
    }

    private static List<LithTechVector3> ParseVector3List(LtaList node)
    {
        var result = new List<LithTechVector3>();
        foreach (LtaList child in node.Children.SelectMany(GetChildren))
        {
            if (child.Items.Count < 3)
            {
                continue;
            }

            if (TryReadDouble(child.Items[0], out double x) &&
                TryReadDouble(child.Items[1], out double y) &&
                TryReadDouble(child.Items[2], out double z))
            {
                result.Add(new LithTechVector3(x, y, z));
            }
        }

        return result;
    }

    private static List<int> ParseIntList(LtaList node)
    {
        var values = new List<int>();
        CollectInts(node.Items.Skip(1), values);
        return values;
    }

    private static void CollectInts(IEnumerable<object> items, List<int> values)
    {
        foreach (object item in items)
        {
            if (item is LtaAtom atom &&
                int.TryParse(atom.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                values.Add(value);
            }
            else if (item is LtaList list)
            {
                CollectInts(list.Items, values);
            }
        }
    }

    private static bool TryReadDouble(object item, out double value)
    {
        value = 0;
        return item is LtaAtom atom &&
               double.TryParse(atom.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static IEnumerable<LtaList> FindListsByHead(LtaList node, string head)
    {
        if (IsHead(node, head))
        {
            yield return node;
        }

        foreach (LtaList child in node.Children)
        {
            foreach (LtaList match in FindListsByHead(child, head))
            {
                yield return match;
            }
        }
    }

    private static bool IsHead(LtaList list, string value)
    {
        return string.Equals(GetAtomValue(list.Items.FirstOrDefault()), value, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetAtomValue(object? item)
    {
        return item is LtaAtom atom ? atom.Value : null;
    }

    private static IEnumerable<LtaList> GetChildren(LtaList list)
    {
        return list.Children;
    }

    private readonly record struct LtbMeshLayout(bool IncludeWeights, bool IncludePostData, int PostNormalByteCount)
    {
        public int VertexStride => 12 +
                                   (IncludeWeights ? 12 : 0) +
                                   12 +
                                   PostNormalByteCount +
                                   8;
    }

    private sealed class LtaParser
    {
        private readonly string _text;
        private int _position;

        public LtaParser(string text)
        {
            _text = text;
        }

        public LtaList ParseRoot()
        {
            SkipWhiteSpace();
            return ParseList(0);
        }

        private LtaList ParseList(int depth)
        {
            if (depth > MaxParseDepth)
            {
                throw new FormatException("LTA nesting is too deep.");
            }

            Expect('(');
            var list = new LtaList();
            while (true)
            {
                SkipWhiteSpace();
                if (_position >= _text.Length)
                {
                    throw new FormatException("Unexpected end of LTA data.");
                }

                char current = _text[_position];
                if (current == ')')
                {
                    _position++;
                    return list;
                }

                list.Items.Add(current == '(' ? ParseList(depth + 1) : ParseAtom());
            }
        }

        private LtaAtom ParseAtom()
        {
            if (_text[_position] == '"')
            {
                return new LtaAtom(ParseString());
            }

            int start = _position;
            while (_position < _text.Length &&
                   !char.IsWhiteSpace(_text[_position]) &&
                   _text[_position] is not '(' and not ')')
            {
                _position++;
            }

            return new LtaAtom(_text[start.._position]);
        }

        private string ParseString()
        {
            _position++;
            var builder = new StringBuilder();
            while (_position < _text.Length)
            {
                char current = _text[_position++];
                if (current == '"')
                {
                    return builder.ToString();
                }

                if (current == '\\' && _position < _text.Length)
                {
                    builder.Append(_text[_position++]);
                }
                else
                {
                    builder.Append(current);
                }
            }

            throw new FormatException("Unterminated LTA string.");
        }

        private void Expect(char expected)
        {
            if (_position >= _text.Length || _text[_position] != expected)
            {
                throw new FormatException($"Expected '{expected}'.");
            }

            _position++;
        }

        private void SkipWhiteSpace()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
            {
                _position++;
            }
        }
    }

    private sealed class LtaList
    {
        public List<object> Items { get; } = new();

        public IEnumerable<LtaList> Children => Items.OfType<LtaList>();
    }

    private sealed record LtaAtom(string Value);
}
