using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using MediaColor = System.Windows.Media.Color;
using WpfSize = System.Windows.Size;

namespace CFRezManager;

internal static class LithTechModelThumbnailRenderer
{
    private const int ThumbnailSize = 192;

    public static ImageSource? TryRender(LithTechModelDocument document)
    {
        ImageSource? thumbnail = null;
        var renderThread = new Thread(() =>
        {
            try
            {
                thumbnail = RenderOnCurrentThread(document);
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

    private static ImageSource RenderOnCurrentThread(LithTechModelDocument document)
    {
        var root = new Grid
        {
            Width = ThumbnailSize,
            Height = ThumbnailSize,
            Background = new SolidColorBrush(MediaColor.FromRgb(0x11, 0x18, 0x27))
        };

        var viewport = new Viewport3D
        {
            Width = ThumbnailSize,
            Height = ThumbnailSize,
            ClipToBounds = true,
            Camera = CreateCamera()
        };
        viewport.Children.Add(new ModelVisual3D
        {
            Content = LithTechModelSceneBuilder.CreateScene(document)
        });
        root.Children.Add(viewport);

        WpfSize renderSize = new(ThumbnailSize, ThumbnailSize);
        root.Measure(renderSize);
        root.Arrange(new Rect(renderSize));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(ThumbnailSize, ThumbnailSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    private static PerspectiveCamera CreateCamera()
    {
        const double yaw = -35;
        const double pitch = 22;
        const double distance = 8;

        double pitchRadians = pitch * Math.PI / 180;
        double yawRadians = yaw * Math.PI / 180;
        double horizontal = distance * Math.Cos(pitchRadians);
        var position = new Point3D(
            horizontal * Math.Sin(yawRadians),
            distance * Math.Sin(pitchRadians),
            horizontal * Math.Cos(yawRadians));

        return new PerspectiveCamera
        {
            Position = position,
            LookDirection = new Vector3D(-position.X, -position.Y, -position.Z),
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = 45,
            NearPlaneDistance = 0.01,
            FarPlaneDistance = 1000
        };
    }
}
