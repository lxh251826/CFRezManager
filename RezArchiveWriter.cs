using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CFRezManager;

public sealed record RezArchiveWriteResult(int FileCount, int DirectoryCount, long BytesWritten);

public readonly record struct RezArchiveWriteProgress(int CompletedFiles, int TotalFiles, string FileName);

public static class RezArchiveWriter
{
    private const int HeaderSize = 168;
    private const string FileType = "RezMgr Version 1 Copyright (C) 1995 MONOLITH INC.";
    private const string UserTitle = "LithTech Resource File v1.0";
    private static readonly Encoding TextEncoding = Encoding.ASCII;

    public static RezArchiveWriteResult WriteFromDirectory(
        string sourceDirectory,
        string outputPath,
        IProgress<RezArchiveWriteProgress>? progress = null)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Source folder does not exist: {sourceDirectory}");
        }

        string sourceFullPath = Path.GetFullPath(sourceDirectory);
        string outputFullPath = Path.GetFullPath(outputPath);
        PackDirectory root = BuildDirectory(sourceFullPath, outputFullPath, isRoot: true);
        List<PackFile> files = root.EnumerateFiles().ToList();
        int directoryCount = root.CountChildDirectories();

        if (files.Count == 0 && directoryCount == 0)
        {
            throw new InvalidOperationException("The selected folder does not contain anything to pack.");
        }

        string? outputDirectory = Path.GetDirectoryName(outputFullPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        string tempPath = Path.Combine(
            string.IsNullOrEmpty(outputDirectory) ? Environment.CurrentDirectory : outputDirectory,
            $"{Path.GetFileName(outputFullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                output.Write(new byte[HeaderSize]);

                int completed = 0;
                foreach (PackFile file in files)
                {
                    file.DataOffset = CheckedInt32(output.Position, "The REZ archive is too large.");
                    file.Md5 = CopyFileWithMd5(file.FullPath, output);
                    file.Size = CheckedInt32(output.Position - file.DataOffset, $"File is too large: {file.FullPath}");
                    completed++;
                    progress?.Report(new RezArchiveWriteProgress(completed, files.Count, file.NameWithExtension));
                }

                int nextWritePos = CheckedInt32(output.Position, "The REZ archive is too large.");
                int tableOffset = nextWritePos;
                var tables = new List<TableBlob>();
                WriteDirectoryTables(root, ref tableOffset, tables);

                foreach (TableBlob table in tables.OrderBy(table => table.Offset))
                {
                    output.Position = table.Offset;
                    output.Write(table.Bytes);
                }

                output.Position = 0;
                byte[] header = CreateHeader(root, nextWritePos);
                output.Write(header);
            }

            File.Move(tempPath, outputFullPath, overwrite: true);
            long bytesWritten = new FileInfo(outputFullPath).Length;
            return new RezArchiveWriteResult(files.Count, directoryCount, bytesWritten);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static PackDirectory BuildDirectory(string sourceDirectory, string outputFullPath, bool isRoot = false)
    {
        var directory = new PackDirectory(
            Path.GetFileName(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            validateName: !isRoot);

        foreach (string childDirectory in Directory.EnumerateDirectories(sourceDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            directory.Directories.Add(BuildDirectory(childDirectory, outputFullPath));
        }

        foreach (string filePath in Directory.EnumerateFiles(sourceDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string fileFullPath = Path.GetFullPath(filePath);
            if (string.Equals(fileFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            directory.Files.Add(CreatePackFile(fileFullPath));
        }

        return directory;
    }

    private static PackFile CreatePackFile(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        string baseName = Path.GetFileNameWithoutExtension(filePath);
        string extension = Path.GetExtension(filePath).TrimStart('.');

        ValidateName(baseName, filePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            throw new InvalidOperationException($"REZ files require a file extension: {filePath}");
        }

        ValidateName(extension, filePath);
        if (extension.Length > 4)
        {
            throw new InvalidOperationException($"REZ file extensions can be at most 4 characters: {filePath}");
        }

        long length = new FileInfo(filePath).Length;
        CheckedInt32(length, $"File is too large: {filePath}");

        return new PackFile(filePath, baseName, extension, fileName);
    }

    private static void WriteDirectoryTables(PackDirectory directory, ref int tableOffset, List<TableBlob> tables)
    {
        foreach (PackDirectory child in directory.Directories)
        {
            WriteDirectoryTables(child, ref tableOffset, tables);
        }

        byte[] table = SerializeDirectoryTable(directory);
        directory.TableOffset = tableOffset;
        directory.TableSize = table.Length;

        if (table.Length > 0)
        {
            RezCrypto.Encode(table, directory.TableOffset);
            tables.Add(new TableBlob(directory.TableOffset, table));
        }

        tableOffset = CheckedInt32(tableOffset + table.Length, "The REZ archive is too large.");
    }

    private static byte[] SerializeDirectoryTable(PackDirectory directory)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, TextEncoding, leaveOpen: true);

        foreach (PackDirectory child in directory.Directories)
        {
            writer.Write(1);
            writer.Write(child.TableOffset);
            writer.Write(child.TableSize);
            writer.Write(0);
            WriteAsciiName(writer, child.Name);
            writer.Write((byte)0);
        }

        foreach (PackFile file in directory.Files)
        {
            writer.Write(0);
            writer.Write(file.DataOffset);
            writer.Write(file.Size);
            writer.Write(0);
            writer.Write(0);
            writer.Write(EncodeExtension(file.Extension));
            writer.Write(0);
            WriteAsciiName(writer, file.BaseName);
            writer.Write((short)0);
            writer.Write(TextEncoding.GetBytes(file.Md5));
        }

        return stream.ToArray();
    }

    private static byte[] CreateHeader(PackDirectory root, int nextWritePos)
    {
        byte[] header = new byte[HeaderSize];

        header[0] = 0x0D;
        header[1] = 0x0A;
        WriteFixedString(header, 2, 60, FileType);
        header[62] = 0x0D;
        header[63] = 0x0A;
        WriteFixedString(header, 64, 60, UserTitle);
        header[124] = 0x0D;
        header[125] = 0x0A;
        header[126] = 0x1A;

        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(127, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(131, 4), root.TableOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(135, 4), root.TableSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(139, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(143, 4), nextWritePos);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(147, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(151, 4), 0);
        int maxDirectoryNameLength = root.MaxDirectoryNameLength();
        int maxFileBaseNameLength = root.MaxFileBaseNameLength();
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(155, 4), maxDirectoryNameLength == 0 ? 0 : maxDirectoryNameLength + 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(159, 4), maxFileBaseNameLength == 0 ? 0 : maxFileBaseNameLength + 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(163, 4), 0);
        header[167] = 1;

        return header;
    }

    private static string CopyFileWithMd5(string sourcePath, Stream destination)
    {
        using var source = File.OpenRead(sourcePath);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        byte[] buffer = new byte[1024 * 128];

        while (true)
        {
            int read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            md5.AppendData(buffer.AsSpan(0, read));
            destination.Write(buffer, 0, read);
        }

        return Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
    }

    private static void WriteAsciiName(BinaryWriter writer, string value)
    {
        byte[] bytes = TextEncoding.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteFixedString(byte[] destination, int offset, int length, string value)
    {
        destination.AsSpan(offset, length).Fill(0x20);
        byte[] bytes = TextEncoding.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, length)).CopyTo(destination.AsSpan(offset, length));
    }

    private static byte[] EncodeExtension(string extension)
    {
        byte[] bytes = new byte[4];
        for (int i = 0; i < extension.Length; i++)
        {
            bytes[i] = (byte)extension[extension.Length - i - 1];
        }

        return bytes;
    }

    private static void ValidateName(string value, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(ch => ch > 0x7F || char.IsControl(ch) || ch is '/' or '\\'))
        {
            throw new InvalidOperationException($"REZ names must be non-empty ASCII text: {sourcePath}");
        }
    }

    private static int CheckedInt32(long value, string message)
    {
        if (value is < 0 or > int.MaxValue)
        {
            throw new InvalidOperationException(message);
        }

        return (int)value;
    }

    private sealed class PackDirectory
    {
        public PackDirectory(string name, bool validateName = true)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "_" : name;
            if (validateName)
            {
                ValidateName(Name, Name);
            }
        }

        public string Name { get; }
        public int TableOffset { get; set; }
        public int TableSize { get; set; }
        public List<PackDirectory> Directories { get; } = new();
        public List<PackFile> Files { get; } = new();

        public IEnumerable<PackFile> EnumerateFiles()
        {
            foreach (PackFile file in Files)
            {
                yield return file;
            }

            foreach (PackDirectory directory in Directories)
            {
                foreach (PackFile file in directory.EnumerateFiles())
                {
                    yield return file;
                }
            }
        }

        public int CountChildDirectories()
        {
            int count = Directories.Count;
            foreach (PackDirectory directory in Directories)
            {
                count += directory.CountChildDirectories();
            }

            return count;
        }

        public int MaxDirectoryNameLength()
        {
            int max = Directories.Count == 0 ? 0 : Directories.Max(directory => directory.Name.Length);
            foreach (PackDirectory directory in Directories)
            {
                max = Math.Max(max, directory.MaxDirectoryNameLength());
            }

            return max;
        }

        public int MaxFileBaseNameLength()
        {
            int max = Files.Count == 0 ? 0 : Files.Max(file => file.BaseName.Length);
            foreach (PackDirectory directory in Directories)
            {
                max = Math.Max(max, directory.MaxFileBaseNameLength());
            }

            return max;
        }
    }

    private sealed class PackFile
    {
        public PackFile(string fullPath, string baseName, string extension, string nameWithExtension)
        {
            FullPath = fullPath;
            BaseName = baseName;
            Extension = extension;
            NameWithExtension = nameWithExtension;
        }

        public string FullPath { get; }
        public string BaseName { get; }
        public string Extension { get; }
        public string NameWithExtension { get; }
        public int DataOffset { get; set; }
        public int Size { get; set; }
        public string Md5 { get; set; } = string.Empty;
    }

    private sealed record TableBlob(int Offset, byte[] Bytes);
}
