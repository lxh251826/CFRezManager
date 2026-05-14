using System.Buffers.Binary;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MediaColor = System.Windows.Media.Color;
using WpfSize = System.Windows.Size;

namespace CFRezManager;

internal static class AudioThumbnailRenderer
{
    private const int ThumbnailSize = 192;
    private const int WaveformColumns = 92;
    private const int SamplesPerColumnLimit = 256;

    public static ImageSource? TryRender(string title, byte[] audioData)
    {
        ImageSource? thumbnail = null;
        var renderThread = new Thread(() =>
        {
            try
            {
                TryBuildPeaks(audioData, WaveformColumns, title, out float[] peaks, out string info);
                thumbnail = RenderOnCurrentThread(title, peaks, info);
            }
            catch
            {
                thumbnail = null;
            }
        });

        renderThread.SetApartmentState(ApartmentState.STA);
        renderThread.IsBackground = true;
        renderThread.Start();
        renderThread.Join();
        return thumbnail;
    }

    public static bool TryBuildPeaks(byte[] audioData, int columns, string title, out float[] peaks, out string info)
    {
        info = AudioMetadataDecoder.TryRead(audioData, title, out AudioMetadata metadata)
            ? metadata.Description
            : "Audio";
        if (!TryReadWaveform(audioData, columns, out peaks, out string waveformInfo))
        {
            peaks = BuildCompressedPeaks(audioData, columns);
            return false;
        }

        info = waveformInfo;
        return true;
    }

    private static ImageSource RenderOnCurrentThread(string title, float[] peaks, string info)
    {
        var root = new Grid
        {
            Width = ThumbnailSize,
            Height = ThumbnailSize,
            Background = System.Windows.Media.Brushes.Transparent
        };

        var border = new Border
        {
            Width = ThumbnailSize,
            Height = ThumbnailSize,
            Background = new SolidColorBrush(MediaColor.FromRgb(0xF8, 0xFA, 0xFC)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0xCB, 0xD5, 0xE1)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10)
        };
        root.Children.Add(border);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        border.Child = layout;

        var titleBlock = new TextBlock
        {
            Text = Shorten(title, 24),
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x11, 0x18, 0x27)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(titleBlock, 0);
        layout.Children.Add(titleBlock);

        FrameworkElement waveform = peaks.Length > 0
            ? CreateWaveformCanvas(peaks)
            : CreateFallbackWaveform();
        waveform.Margin = new Thickness(0, 10, 0, 8);
        Grid.SetRow(waveform, 1);
        layout.Children.Add(waveform);

        var infoBlock = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(info) ? "WAV audio" : info,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x47, 0x55, 0x69)),
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(infoBlock, 2);
        layout.Children.Add(infoBlock);

        WpfSize renderSize = new(ThumbnailSize, ThumbnailSize);
        root.Measure(renderSize);
        root.Arrange(new Rect(renderSize));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(ThumbnailSize, ThumbnailSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    private static FrameworkElement CreateWaveformCanvas(float[] peaks)
    {
        const double width = 168;
        const double height = 104;
        const double centerY = height / 2;
        double columnWidth = width / peaks.Length;
        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(MediaColor.FromRgb(0xEC, 0xF5, 0xFF))
        };

        canvas.Children.Add(new Line
        {
            X1 = 0,
            X2 = width,
            Y1 = centerY,
            Y2 = centerY,
            Stroke = new SolidColorBrush(MediaColor.FromRgb(0xBF, 0xDB, 0xFE)),
            StrokeThickness = 1
        });

        System.Windows.Media.Brush stroke = new SolidColorBrush(MediaColor.FromRgb(0x25, 0x62, 0xB3));
        for (int i = 0; i < peaks.Length; i++)
        {
            double amplitude = Math.Clamp(peaks[i], 0, 1) * (height / 2 - 6);
            double x = i * columnWidth + columnWidth / 2;
            canvas.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = centerY - Math.Max(1, amplitude),
                Y2 = centerY + Math.Max(1, amplitude),
                Stroke = stroke,
                StrokeThickness = Math.Max(1, columnWidth * 0.55),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        return canvas;
    }

    private static FrameworkElement CreateFallbackWaveform()
    {
        var grid = new Grid
        {
            Width = 168,
            Height = 104,
            Background = new SolidColorBrush(MediaColor.FromRgb(0xEC, 0xF5, 0xFF))
        };
        grid.Children.Add(new TextBlock
        {
            Text = "WAV",
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x25, 0x62, 0xB3)),
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return grid;
    }

    private static bool TryReadWaveform(byte[] wavData, int columns, out float[] peaks, out string info)
    {
        peaks = [];
        info = "WAV audio";
        if (!AudioMetadataDecoder.IsWaveData(wavData))
        {
            return false;
        }

        if (!TryFindChunk(wavData, "fmt "u8, out int fmtOffset, out int fmtSize) ||
            !TryFindChunk(wavData, "data"u8, out int dataOffset, out int dataSize) ||
            fmtSize < 16)
        {
            return false;
        }

        ushort format = BinaryPrimitives.ReadUInt16LittleEndian(wavData.AsSpan(fmtOffset, sizeof(ushort)));
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(wavData.AsSpan(fmtOffset + 2, sizeof(ushort)));
        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(wavData.AsSpan(fmtOffset + 4, sizeof(uint)));
        ushort blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(wavData.AsSpan(fmtOffset + 12, sizeof(ushort)));
        ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(wavData.AsSpan(fmtOffset + 14, sizeof(ushort)));

        int bytesPerSample = Math.Max(1, bitsPerSample / 8);
        if (channels == 0 ||
            blockAlign == 0 ||
            dataSize <= 0 ||
            dataOffset + dataSize > wavData.Length ||
            blockAlign < channels * bytesPerSample ||
            !IsSupportedFormat(format, bitsPerSample))
        {
            info = FormatBasicInfo(channels, sampleRate, bitsPerSample);
            return false;
        }

        int frameCount = dataSize / blockAlign;
        if (frameCount <= 0)
        {
            info = FormatBasicInfo(channels, sampleRate, bitsPerSample);
            return false;
        }

        peaks = new float[columns];
        for (int column = 0; column < columns; column++)
        {
            int startFrame = (int)((long)column * frameCount / columns);
            int endFrame = (int)((long)(column + 1) * frameCount / columns);
            int span = Math.Max(1, endFrame - startFrame);
            int stride = Math.Max(1, span / SamplesPerColumnLimit);
            float peak = 0;

            for (int frame = startFrame; frame < endFrame; frame += stride)
            {
                int frameOffset = dataOffset + frame * blockAlign;
                for (int channel = 0; channel < channels; channel++)
                {
                    int sampleOffset = frameOffset + channel * bytesPerSample;
                    if (sampleOffset + bytesPerSample > wavData.Length)
                    {
                        continue;
                    }

                    peak = Math.Max(peak, ReadSampleAmplitude(wavData, sampleOffset, format, bitsPerSample));
                }
            }

            peaks[column] = peak;
        }

        info = FormatBasicInfo(channels, sampleRate, bitsPerSample);
        return true;
    }

    private static float[] BuildCompressedPeaks(byte[] data, int columns)
    {
        var peaks = new float[columns];
        if (data.Length == 0)
        {
            return peaks;
        }

        int start = 0;
        if (data.Length >= 10 && data.AsSpan(0, 3).SequenceEqual("ID3"u8))
        {
            int id3Size = (data[6] & 0x7F) << 21 |
                          (data[7] & 0x7F) << 14 |
                          (data[8] & 0x7F) << 7 |
                          (data[9] & 0x7F);
            start = Math.Min(data.Length, 10 + id3Size);
        }

        int length = Math.Max(1, data.Length - start);
        for (int column = 0; column < columns; column++)
        {
            int sampleStart = start + (int)((long)column * length / columns);
            int sampleEnd = start + (int)((long)(column + 1) * length / columns);
            int count = Math.Max(1, sampleEnd - sampleStart);
            int stride = Math.Max(1, count / SamplesPerColumnLimit);
            double total = 0;
            int samples = 0;

            for (int i = sampleStart; i < sampleEnd && i < data.Length; i += stride)
            {
                total += Math.Abs(data[i] - 128) / 128.0;
                samples++;
            }

            peaks[column] = samples == 0
                ? 0
                : (float)Math.Clamp(Math.Sqrt(total / samples), 0.05, 1.0);
        }

        return peaks;
    }

    private static bool TryFindChunk(ReadOnlySpan<byte> data, ReadOnlySpan<byte> chunkId, out int dataOffset, out int dataSize)
    {
        dataOffset = 0;
        dataSize = 0;
        int offset = 12;
        while (offset + 8 <= data.Length)
        {
            ReadOnlySpan<byte> id = data.Slice(offset, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, sizeof(uint)));
            long payloadOffset = offset + 8L;
            long nextOffset = payloadOffset + size + (size & 1);
            if (nextOffset > data.Length + 1L || nextOffset <= offset)
            {
                return false;
            }

            if (id.SequenceEqual(chunkId))
            {
                dataOffset = checked((int)payloadOffset);
                dataSize = checked((int)Math.Min(size, data.Length - payloadOffset));
                return true;
            }

            offset = checked((int)nextOffset);
        }

        return false;
    }

    private static bool IsSupportedFormat(ushort format, ushort bitsPerSample)
    {
        return format switch
        {
            1 => bitsPerSample is 8 or 16 or 24 or 32,
            3 => bitsPerSample == 32,
            65534 => bitsPerSample is 8 or 16 or 24 or 32,
            _ => false
        };
    }

    private static float ReadSampleAmplitude(byte[] data, int offset, ushort format, ushort bitsPerSample)
    {
        if (format == 3 && bitsPerSample == 32)
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, sizeof(float)));
            return float.IsFinite(value) ? Math.Clamp(Math.Abs(value), 0, 1) : 0;
        }

        return bitsPerSample switch
        {
            8 => Math.Abs((data[offset] - 128) / 128f),
            16 => Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, sizeof(short))) / 32768f),
            24 => Math.Abs(ReadInt24(data, offset) / 8_388_608f),
            32 => Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(int))) / 2_147_483_648f),
            _ => 0
        };
    }

    private static int ReadInt24(byte[] data, int offset)
    {
        int value = data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16;
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    private static string FormatBasicInfo(ushort channels, uint sampleRate, ushort bitsPerSample)
    {
        string channelText = channels switch
        {
            1 => "mono",
            2 => "stereo",
            _ => $"{channels:N0} ch"
        };
        return sampleRate > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{sampleRate / 1000.0:0.#} kHz, {bitsPerSample:N0}-bit, {channelText}")
            : $"{bitsPerSample:N0}-bit, {channelText}";
    }

    private static string Shorten(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(0, maxLength - 3)] + "...";
    }
}
