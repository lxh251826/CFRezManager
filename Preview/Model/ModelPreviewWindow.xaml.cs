using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using WpfPoint = System.Windows.Point;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace CFRezManager;

public partial class ModelPreviewWindow : Window
{
    private const double InitialOrbitYaw = -35;
    private const double InitialOrbitPitch = 22;
    private const double InitialDistance = 8;
    private const double InitialViewYaw = 145;
    private const double InitialViewPitch = -22;
    private const double MouseSensitivity = 0.16;
    private const double MoveSpeed = 3.6;
    private const double FastMoveMultiplier = 3.0;
    private const double WheelMoveStep = 0.55;

    private readonly PerspectiveCamera _camera = new();
    private readonly HashSet<Key> _pressedKeys = new();
    private Point3D _cameraPosition;
    private TimeSpan? _lastMovementRenderingTime;
    private double _yaw = InitialViewYaw;
    private double _pitch = InitialViewPitch;
    private bool _ignoreNextMouseMove;
    private bool _isRenderingSubscribed;
    private bool _isFreeLookActive;

    public ModelPreviewWindow(
        string fileName,
        LithTechModelDocument document,
        string? modelInfo = null,
        Func<string, ImageSource?>? textureResolver = null)
    {
        InitializeComponent();
        WindowThemeHelper.Apply(this, ThemeManager.Parse(UserSettings.Load().Theme));

        Rect workArea = SystemParameters.WorkArea;
        MaxWidth = Math.Max(MinWidth, workArea.Width - 80);
        MaxHeight = Math.Max(MinHeight, workArea.Height - 80);

        LithTechModelDocument renderDocument = LithTechThumbnailGeometryReducer.ReduceForInteractivePreview(document);
        ModelViewport.Camera = _camera;
        BuildScene(renderDocument, textureResolver);

        ResetCameraState();
        UpdateCamera();

        PreviewInfoText.Text = FormatDisplayInfo(document, renderDocument, modelInfo);
        Title = $"{fileName} - {document.Name}";
    }

    private void BuildScene(LithTechModelDocument document, Func<string, ImageSource?>? textureResolver)
    {
        ModelViewport.Children.Add(new ModelVisual3D { Content = LithTechModelSceneBuilder.CreateScene(document, textureResolver) });
    }

    private void UpdateCamera()
    {
        _camera.Position = _cameraPosition;
        _camera.LookDirection = GetForwardDirection();
        _camera.UpDirection = new Vector3D(0, 1, 0);
        _camera.FieldOfView = 45;
        _camera.NearPlaneDistance = 0.01;
        _camera.FarPlaneDistance = 1000;
    }

    private void ModelViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        BeginFreeLook();
        e.Handled = true;
    }

    private void ModelViewport_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        EndFreeLook();
        e.Handled = true;
    }

    private void ModelViewport_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_isFreeLookActive)
        {
            return;
        }

        WpfPoint current = e.GetPosition(ModelViewport);
        WpfPoint center = GetViewportCenter();
        Vector delta = current - center;
        if (_ignoreNextMouseMove)
        {
            _ignoreNextMouseMove = false;
            if (Math.Abs(delta.X) < 1 && Math.Abs(delta.Y) < 1)
            {
                return;
            }
        }

        if (Math.Abs(delta.X) < 0.5 && Math.Abs(delta.Y) < 0.5)
        {
            return;
        }

        _yaw -= delta.X * MouseSensitivity;
        _pitch = Math.Clamp(_pitch - delta.Y * MouseSensitivity, -89, 89);
        UpdateCamera();
        CenterMouseInViewport();
    }

    private void ModelViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double direction = e.Delta > 0 ? 1 : -1;
        _cameraPosition += GetForwardDirection() * (direction * WheelMoveStep);
        UpdateCamera();
    }

    private void ModelViewport_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EndFreeLook();
            e.Handled = true;
            return;
        }

        Key key = e.Key;
        if (IsMovementKey(key))
        {
            BeginFreeLook();
            _pressedKeys.Add(key);
            e.Handled = true;
        }
    }

    private void ModelViewport_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_pressedKeys.Remove(e.Key))
        {
            e.Handled = true;
        }
    }

    private void ModelViewport_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        EndFreeLook();
    }

    private void ModelViewport_LostMouseCapture(object sender, WpfMouseEventArgs e)
    {
        if (_isFreeLookActive)
        {
            EndFreeLook();
        }
    }

    private void ResetViewButton_Click(object sender, RoutedEventArgs e)
    {
        ResetCameraState();
        UpdateCamera();
        if (_isFreeLookActive)
        {
            CenterMouseInViewport();
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        EndFreeLook();
        base.OnDeactivated(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        EndFreeLook();
        StopMovementRendering();
        base.OnClosed(e);
    }

    private void BeginFreeLook()
    {
        if (_isFreeLookActive)
        {
            return;
        }

        _isFreeLookActive = true;
        _lastMovementRenderingTime = null;
        ModelViewport.Focus();
        ModelViewport.CaptureMouse();
        Mouse.OverrideCursor = System.Windows.Input.Cursors.None;
        CenterMouseInViewport();
        StartMovementRendering();
    }

    private void EndFreeLook()
    {
        if (!_isFreeLookActive)
        {
            return;
        }

        _isFreeLookActive = false;
        _ignoreNextMouseMove = false;
        _lastMovementRenderingTime = null;
        _pressedKeys.Clear();
        StopMovementRendering();
        if (ModelViewport.IsMouseCaptured)
        {
            ModelViewport.ReleaseMouseCapture();
        }

        Mouse.OverrideCursor = null;
    }

    private void StartMovementRendering()
    {
        if (_isRenderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering += CompositionTarget_Rendering;
        _isRenderingSubscribed = true;
    }

    private void StopMovementRendering()
    {
        if (!_isRenderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        _isRenderingSubscribed = false;
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        TimeSpan renderingTime = e is RenderingEventArgs renderingEventArgs
            ? renderingEventArgs.RenderingTime
            : TimeSpan.Zero;

        double elapsedSeconds = 0;
        if (_lastMovementRenderingTime is TimeSpan lastRenderingTime)
        {
            elapsedSeconds = Math.Clamp((renderingTime - lastRenderingTime).TotalSeconds, 0, 0.05);
        }

        _lastMovementRenderingTime = renderingTime;
        if (!_isFreeLookActive || _pressedKeys.Count == 0 || elapsedSeconds <= 0)
        {
            return;
        }

        Vector3D movement = new();
        Vector3D forward = GetForwardDirection();
        Vector3D right = Vector3D.CrossProduct(forward, new Vector3D(0, 1, 0));
        if (right.LengthSquared > 0)
        {
            right.Normalize();
        }

        if (_pressedKeys.Contains(Key.W))
        {
            movement += forward;
        }

        if (_pressedKeys.Contains(Key.S))
        {
            movement -= forward;
        }

        if (_pressedKeys.Contains(Key.D))
        {
            movement += right;
        }

        if (_pressedKeys.Contains(Key.A))
        {
            movement -= right;
        }

        if (movement.LengthSquared <= 0)
        {
            return;
        }

        movement.Normalize();
        double speed = IsFastMovePressed() ? MoveSpeed * FastMoveMultiplier : MoveSpeed;
        _cameraPosition += movement * (speed * elapsedSeconds);
        UpdateCamera();
    }

    private void ResetCameraState()
    {
        _yaw = InitialViewYaw;
        _pitch = InitialViewPitch;
        _cameraPosition = CreateInitialCameraPosition();
    }

    private Vector3D GetForwardDirection()
    {
        double yawRadians = _yaw * Math.PI / 180;
        double pitchRadians = _pitch * Math.PI / 180;
        var forward = new Vector3D(
            Math.Cos(pitchRadians) * Math.Sin(yawRadians),
            Math.Sin(pitchRadians),
            Math.Cos(pitchRadians) * Math.Cos(yawRadians));
        forward.Normalize();
        return forward;
    }

    private void CenterMouseInViewport()
    {
        if (ModelViewport.ActualWidth <= 0 || ModelViewport.ActualHeight <= 0)
        {
            return;
        }

        WpfPoint screenCenter = ModelViewport.PointToScreen(GetViewportCenter());
        _ignoreNextMouseMove = true;
        System.Windows.Forms.Cursor.Position = new System.Drawing.Point(
            (int)Math.Round(screenCenter.X),
            (int)Math.Round(screenCenter.Y));
    }

    private WpfPoint GetViewportCenter()
    {
        return new WpfPoint(ModelViewport.ActualWidth / 2, ModelViewport.ActualHeight / 2);
    }

    private static Point3D CreateInitialCameraPosition()
    {
        double pitchRadians = InitialOrbitPitch * Math.PI / 180;
        double yawRadians = InitialOrbitYaw * Math.PI / 180;
        double horizontal = InitialDistance * Math.Cos(pitchRadians);
        return new Point3D(
            horizontal * Math.Sin(yawRadians),
            InitialDistance * Math.Sin(pitchRadians),
            horizontal * Math.Cos(yawRadians));
    }

    private static bool IsMovementKey(Key key)
    {
        return key is Key.W or Key.A or Key.S or Key.D;
    }

    private static bool IsFastMovePressed()
    {
        return Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
    }

    private static string FormatDocumentInfo(LithTechModelDocument document)
    {
        return $"{document.StorageDescription} | {document.Meshes.Count:N0} mesh | {document.VertexCount:N0} vertices | {document.TriangleCount:N0} triangles";
    }

    private static string FormatDisplayInfo(
        LithTechModelDocument originalDocument,
        LithTechModelDocument renderDocument,
        string? modelInfo)
    {
        string info = modelInfo ?? FormatDocumentInfo(originalDocument);
        if (ReferenceEquals(originalDocument, renderDocument) ||
            originalDocument.Meshes.Count == renderDocument.Meshes.Count &&
            originalDocument.VertexCount == renderDocument.VertexCount &&
            originalDocument.TriangleCount == renderDocument.TriangleCount)
        {
            return info;
        }

        return $"{info} | rendering {renderDocument.Meshes.Count:N0} mesh / {renderDocument.VertexCount:N0} vertices / {renderDocument.TriangleCount:N0} triangles";
    }

}
