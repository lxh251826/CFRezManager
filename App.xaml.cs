namespace CFRezManager;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
        if (PreviewTool.IsPreviewInvocation(e.Args))
        {
            string? errorMessage = null;
            if (PreviewTool.TryGetPreviewPath(e.Args, out string previewPath) &&
                PreviewTool.TryCreateWindow(previewPath, out System.Windows.Window? previewWindow, out errorMessage) &&
                previewWindow is not null)
            {
                MainWindow = previewWindow;
                previewWindow.Show();
            }
            else
            {
                System.Windows.MessageBox.Show(
                    errorMessage ?? "无法预览此文件。",
                    "Preview failed",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                Shutdown(1);
            }

            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
