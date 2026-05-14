using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;
using WpfSize = System.Windows.Size;

namespace CFRezManager;

internal static class TextThumbnailRenderer
{
    private const int ThumbnailSize = 192;
    private const int PreviewLineCount = 8;

    public static ImageSource? TryRender(string title, string text, string badge)
    {
        ImageSource? thumbnail = null;
        var renderThread = new Thread(() =>
        {
            try
            {
                thumbnail = RenderOnCurrentThread(title, text, badge);
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

    private static ImageSource RenderOnCurrentThread(string title, string text, string badge)
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
            Background = new SolidColorBrush(MediaColor.FromRgb(0xFA, 0xFB, 0xFC)),
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
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x1F, 0x29, 0x37)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(titleBlock, 0);
        layout.Children.Add(titleBlock);

        var previewBlock = new TextBlock
        {
            Text = BuildPreview(text),
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x47, 0x55, 0x69)),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 10,
            LineHeight = 14,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 9, 0, 8)
        };
        Grid.SetRow(previewBlock, 1);
        layout.Children.Add(previewBlock);

        var badgeBlock = new TextBlock
        {
            Text = badge,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x25, 0x44, 0x6B)),
            Background = new SolidColorBrush(MediaColor.FromRgb(0xE0, 0xEA, 0xF6)),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        Grid.SetRow(badgeBlock, 2);
        layout.Children.Add(badgeBlock);

        WpfSize renderSize = new(ThumbnailSize, ThumbnailSize);
        root.Measure(renderSize);
        root.Arrange(new Rect(renderSize));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(ThumbnailSize, ThumbnailSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    private static string BuildPreview(string text)
    {
        string[] lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0)
        {
            return "(empty)";
        }

        return string.Join(Environment.NewLine, lines.Take(PreviewLineCount).Select(line => Shorten(line, 34)));
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
