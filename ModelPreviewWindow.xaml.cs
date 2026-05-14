using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using WpfPoint = System.Windows.Point;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace CFRezManager;

public partial class ModelPreviewWindow : Window
{
    private readonly PerspectiveCamera _camera = new();
    private readonly double _initialDistance;
    private WpfPoint _lastMousePosition;
    private double _yaw = -35;
    private double _pitch = 22;
    private double _distance;
    private bool _isDragging;

    public ModelPreviewWindow(string fileName, LithTechModelDocument document, string? modelInfo = null)
    {
        InitializeComponent();

        Rect workArea = SystemParameters.WorkArea;
        MaxWidth = Math.Max(MinWidth, workArea.Width - 80);
        MaxHeight = Math.Max(MinHeight, workArea.Height - 80);

        ModelViewport.Camera = _camera;
        BuildScene(document);

        _initialDistance = 8;
        _distance = _initialDistance;
        UpdateCamera();

        PreviewInfoText.Text = modelInfo ?? FormatDocumentInfo(document);
        Title = $"{fileName} - {document.Name}";
    }

    private void BuildScene(LithTechModelDocument document)
    {
        ModelViewport.Children.Add(new ModelVisual3D { Content = LithTechModelSceneBuilder.CreateScene(document) });
    }

    private void UpdateCamera()
    {
        double pitchRadians = _pitch * Math.PI / 180;
        double yawRadians = _yaw * Math.PI / 180;
        double horizontal = _distance * Math.Cos(pitchRadians);
        var position = new Point3D(
            horizontal * Math.Sin(yawRadians),
            _distance * Math.Sin(pitchRadians),
            horizontal * Math.Cos(yawRadians));
        _camera.Position = position;
        _camera.LookDirection = new Vector3D(-position.X, -position.Y, -position.Z);
        _camera.UpDirection = new Vector3D(0, 1, 0);
        _camera.FieldOfView = 45;
        _camera.NearPlaneDistance = 0.01;
        _camera.FarPlaneDistance = 1000;
    }

    private void ModelViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _lastMousePosition = e.GetPosition(ModelViewport);
        ModelViewport.CaptureMouse();
    }

    private void ModelViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        ModelViewport.ReleaseMouseCapture();
    }

    private void ModelViewport_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        WpfPoint current = e.GetPosition(ModelViewport);
        Vector delta = current - _lastMousePosition;
        _lastMousePosition = current;
        _yaw += delta.X * 0.45;
        _pitch = Math.Clamp(_pitch - delta.Y * 0.45, -85, 85);
        UpdateCamera();
    }

    private void ModelViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 0.88 : 1.14;
        _distance = Math.Clamp(_distance * factor, 1.2, 80);
        UpdateCamera();
    }

    private void ResetViewButton_Click(object sender, RoutedEventArgs e)
    {
        _yaw = -35;
        _pitch = 22;
        _distance = _initialDistance;
        UpdateCamera();
    }

    private static string FormatDocumentInfo(LithTechModelDocument document)
    {
        return $"{document.StorageDescription} | {document.Meshes.Count:N0} mesh | {document.VertexCount:N0} vertices | {document.TriangleCount:N0} triangles";
    }

}
