using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace CFRezManager;

internal static class LithTechWorldDatDecoder
{
    private const uint SupportedVersion = 85;
    private const int HeaderLength = 60;
    private const int VertexByteCount = 68;
    private const int TriangleByteCount = 16;
    private const long MaxDecodedWorldBytes = 512L * 1024 * 1024;
    private const int MaxNodeCount = 100_000;
    private const int MaxVertexCountPerNode = 1_000_000;
    private const int MaxTriangleCountPerNode = 1_000_000;

    static LithTechWorldDatDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static bool IsCandidate(string extension)
    {
        return string.Equals(extension, "dat", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryDecode(
        byte[] data,
        string fallbackName,
        out LithTechModelDocument? document,
        out string? errorMessage)
    {
        document = null;
        errorMessage = null;

        try
        {
            byte[]? prepared = LzmaAloneDecoder.TryPrepareData(data, MaxDecodedWorldBytes);
            if (prepared is null)
            {
                errorMessage = "DAT world compression could not be decoded.";
                return false;
            }

            if (prepared.Length < HeaderLength)
            {
                errorMessage = "DAT file is too small for a LithTech world header.";
                return false;
            }

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(prepared.AsSpan(0, sizeof(uint)));
            if (version != SupportedVersion)
            {
                errorMessage = $"DAT world version is not supported: {version}.";
                return false;
            }

            uint renderDataPosition = BinaryPrimitives.ReadUInt32LittleEndian(prepared.AsSpan(24, sizeof(uint)));
            if (renderDataPosition < HeaderLength || renderDataPosition >= prepared.Length)
            {
                errorMessage = "DAT render data offset is outside the file.";
                return false;
            }

            var reader = new DatReader(prepared, checked((int)renderDataPosition));
            var meshes = new List<LithTechMesh>();

            uint renderTreeNodeCount = reader.ReadUInt32();
            ValidateCount(renderTreeNodeCount, MaxNodeCount, "render tree node");
            for (int i = 0; i < renderTreeNodeCount; i++)
            {
                ReadRenderNode(reader, $"RenderNode {i + 1}", meshes);
            }

            uint worldModelNodeCount = reader.ReadUInt32();
            ValidateCount(worldModelNodeCount, MaxNodeCount, "world model render node");
            for (int worldModelIndex = 0; worldModelIndex < worldModelNodeCount; worldModelIndex++)
            {
                string worldModelName = reader.ReadString();
                uint nodesInWorldModel = reader.ReadUInt32();
                ValidateCount(nodesInWorldModel, MaxNodeCount, "world model child node");
                for (int nodeIndex = 0; nodeIndex < nodesInWorldModel; nodeIndex++)
                {
                    ReadRenderNode(reader, $"{worldModelName} #{nodeIndex + 1}", meshes);
                }

                reader.Skip(sizeof(uint)); // noChildFlag
            }

            uint lightGroupCount = reader.ReadUInt32();
            ValidateCount(lightGroupCount, MaxNodeCount, "world light group");
            for (int i = 0; i < lightGroupCount; i++)
            {
                SkipWorldLightGroup(reader);
            }

            if (meshes.Count == 0)
            {
                errorMessage = "DAT render data did not contain previewable geometry.";
                return false;
            }

            document = new LithTechModelDocument(
                fallbackName,
                meshes,
                ReferenceEquals(prepared, data)
                    ? $"LithTech DAT world v{version}"
                    : $"LZMA-compressed LithTech DAT world v{version}",
                data.Length,
                prepared.Length);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static void ReadRenderNode(DatReader reader, string meshName, List<LithTechMesh> meshes)
    {
        reader.Skip(24); // center and half dimensions

        uint sectionCount = reader.ReadUInt32();
        ValidateCount(sectionCount, MaxNodeCount, "section");
        for (int i = 0; i < sectionCount; i++)
        {
            SkipSection(reader);
        }

        uint vertexCount = reader.ReadUInt32();
        ValidateCount(vertexCount, MaxVertexCountPerNode, "vertex");
        var vertices = new List<LithTechVector3>((int)Math.Min(vertexCount, 65536));
        for (int i = 0; i < vertexCount; i++)
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            vertices.Add(new LithTechVector3(x, y, z));
            reader.Skip(VertexByteCount - 12);
        }

        uint triangleCount = reader.ReadUInt32();
        ValidateCount(triangleCount, MaxTriangleCountPerNode, "triangle");
        var triangleIndices = new List<int>(checked((int)Math.Min(triangleCount, 65536) * 3));
        for (int i = 0; i < triangleCount; i++)
        {
            uint a = reader.ReadUInt32();
            uint b = reader.ReadUInt32();
            uint c = reader.ReadUInt32();
            reader.Skip(sizeof(uint)); // polyIndex

            if (a < vertexCount && b < vertexCount && c < vertexCount)
            {
                triangleIndices.Add((int)a);
                triangleIndices.Add((int)b);
                triangleIndices.Add((int)c);
            }
        }

        uint skyPortalCount = reader.ReadUInt32();
        ValidateCount(skyPortalCount, MaxNodeCount, "sky portal");
        for (int i = 0; i < skyPortalCount; i++)
        {
            SkipVerticesPos(reader);
            reader.Skip(16); // plane
        }

        uint occluderCount = reader.ReadUInt32();
        ValidateCount(occluderCount, MaxNodeCount, "occluder");
        for (int i = 0; i < occluderCount; i++)
        {
            SkipVerticesPos(reader);
            reader.Skip(20); // plane + other
        }

        uint lightGroupCount = reader.ReadUInt32();
        ValidateCount(lightGroupCount, MaxNodeCount, "light group");
        for (int i = 0; i < lightGroupCount; i++)
        {
            SkipLightGroup(reader);
        }

        reader.Skip(1 + sizeof(uint) * 2); // childFlags + child node indices

        if (vertices.Count > 0 && triangleIndices.Count >= 3)
        {
            meshes.Add(new LithTechMesh(meshName, vertices, triangleIndices));
        }
    }

    private static void SkipSection(DatReader reader)
    {
        reader.ReadString();
        reader.ReadString();
        reader.Skip(1 + sizeof(uint)); // shaderCode + triangleCount
        reader.ReadString();
        reader.Skip(sizeof(uint) * 2); // lightMapWidth + lightMapHeight
        uint lightMapSize = reader.ReadUInt32();
        reader.Skip(checked((int)lightMapSize));
    }

    private static void SkipVerticesPos(DatReader reader)
    {
        int count = reader.ReadByte();
        reader.Skip(checked(count * 12));
    }

    private static void SkipLightGroup(DatReader reader)
    {
        reader.ReadString();
        reader.Skip(12); // color
        uint intensitySize = reader.ReadUInt32();
        reader.Skip(checked((int)intensitySize));

        uint sectionLightMapCount = reader.ReadUInt32();
        ValidateCount(sectionLightMapCount, MaxNodeCount, "section light map");
        for (int i = 0; i < sectionLightMapCount; i++)
        {
            uint subLightMapCount = reader.ReadUInt32();
            ValidateCount(subLightMapCount, MaxNodeCount, "sub light map");
            for (int j = 0; j < subLightMapCount; j++)
            {
                reader.Skip(sizeof(uint) * 4);
                uint dataLength = reader.ReadUInt32();
                reader.Skip(checked((int)dataLength));
            }
        }
    }

    private static void SkipWorldLightGroup(DatReader reader)
    {
        reader.ReadString();
        reader.Skip(12); // color
        reader.Skip(12); // offset
        float sizeX = reader.ReadSingle();
        float sizeY = reader.ReadSingle();
        float sizeZ = reader.ReadSingle();
        int byteCount = checked(ToDimension(sizeX) * ToDimension(sizeY) * ToDimension(sizeZ));
        reader.Skip(byteCount);
    }

    private static int ToDimension(float value)
    {
        if (!float.IsFinite(value) || value < 0 || value > 1_000_000)
        {
            throw new InvalidDataException($"Invalid DAT world light group dimension: {value.ToString(CultureInfo.InvariantCulture)}.");
        }

        return checked((int)MathF.Round(value));
    }

    private static void ValidateCount(uint count, int max, string label)
    {
        if (count > max)
        {
            throw new InvalidDataException($"DAT {label} count is too large: {count}.");
        }
    }

    private sealed class DatReader
    {
        private readonly byte[] _data;

        public DatReader(byte[] data, int position)
        {
            _data = data;
            Position = position;
        }

        public int Position { get; private set; }

        public int ReadByte()
        {
            Ensure(1);
            return _data[Position++];
        }

        public uint ReadUInt32()
        {
            Ensure(sizeof(uint));
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position, sizeof(uint)));
            Position += sizeof(uint);
            return value;
        }

        public float ReadSingle()
        {
            Ensure(sizeof(float));
            int bits = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(Position, sizeof(float)));
            Position += sizeof(float);
            return BitConverter.Int32BitsToSingle(bits);
        }

        public string ReadString()
        {
            Ensure(sizeof(ushort));
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Position, sizeof(ushort)));
            Position += sizeof(ushort);
            Ensure(length);

            string value = DecodeString(_data.AsSpan(Position, length));
            Position += length;
            return value;
        }

        public void Skip(int byteCount)
        {
            if (byteCount < 0)
            {
                throw new InvalidDataException("DAT parser attempted to skip a negative byte count.");
            }

            Ensure(byteCount);
            Position += byteCount;
        }

        private void Ensure(int byteCount)
        {
            if (Position < 0 || byteCount < 0 || Position + byteCount > _data.Length)
            {
                throw new InvalidDataException("DAT data ended before the expected structure was complete.");
            }
        }

        private static string DecodeString(ReadOnlySpan<byte> bytes)
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
    }
}
