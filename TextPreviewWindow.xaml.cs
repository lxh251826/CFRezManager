using System.Windows;

namespace CFRezManager;

public partial class TextPreviewWindow : Window
{
    public TextPreviewWindow(string fileName, string text, string? textInfo = null)
    {
        InitializeComponent();

        Rect workArea = SystemParameters.WorkArea;
        MaxWidth = Math.Max(MinWidth, workArea.Width - 80);
        MaxHeight = Math.Max(MinHeight, workArea.Height - 80);

        ContentTextBox.Text = text;

        if (!string.IsNullOrWhiteSpace(textInfo))
        {
            PreviewInfoText.Text = textInfo;
            PreviewInfoBar.Visibility = Visibility.Visible;
        }

        Title = string.IsNullOrWhiteSpace(textInfo)
            ? fileName
            : $"{fileName} - {textInfo}";
    }
}
