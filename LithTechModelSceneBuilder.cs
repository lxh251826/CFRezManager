using System.Windows.Media;
using System.Windows.Media.Media3D;
using MediaColor = System.Windows.Media.Color;

namespace CFRezManager;

internal static class LithTechModelSceneBuilder
{
    private static readonly MediaColor[] MeshColors =
    [
        MediaColor.FromRgb(0x7D, 0xB7, 0xF0),
        MediaColor.FromRgb(0xF5, 0xA6, 0x5B),
        MediaColor.FromRgb(0x86, 0xD3, 0x91),
        MediaColor.FromRgb(0xD8, 0x8C, 0xD8),
        MediaColor.FromRgb(0xF1, 0xD0, 0x6A),
        MediaColor.FromRgb(0x78, 0xD3, 0xD5)
    ];

    public static Model3DGroup CreateScene(LithTechModelDocument document, Func<string, ImageSource?>? textureResolver = null)
    {
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(MediaColor.FromRgb(0x64, 0x6C, 0x78)));
        scene.Children.Add(new DirectionalLight(MediaColor.FromRgb(0xFF, 0xFF, 0xFF), new Vector3D(-0.35, -0.55, -0.75)));
        scene.Children.Add(new DirectionalLight(MediaColor.FromRgb(0x88, 0xA0, 0xC0), new Vector3D(0.55, 0.25, 0.65)));

        Bounds bounds = CalculateBounds(document);
        double scale = bounds.MaxDimension <= 0 ? 1 : 4.5 / bounds.MaxDimension;
        int meshIndex = 0;
        var textureCache = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);
        foreach (LithTechMesh sourceMesh in document.Meshes)
        {
            MeshGeometry3D mesh = CreateMeshGeometry(sourceMesh, bounds, scale);
            if (mesh.Positions.Count == 0 || mesh.TriangleIndices.Count == 0)
            {
                continue;
            }

            Material material = CreateMaterial(sourceMesh, meshIndex, textureResolver, textureCache);
            material.Freeze();
            var geometry = new GeometryModel3D(mesh, material)
            {
                BackMaterial = material
            };
            if (geometry.CanFreeze)
            {
                geometry.Freeze();
            }

            scene.Children.Add(geometry);
            meshIndex++;
        }

        if (scene.CanFreeze)
        {
            scene.Freeze();
        }

        return scene;
    }

    private static Material CreateMaterial(
        LithTechMesh sourceMesh,
        int meshIndex,
        Func<string, ImageSource?>? textureResolver,
        Dictionary<string, ImageSource?> textureCache)
    {
        ImageSource? texture = null;
        if (sourceMesh.HasTextureCoordinates &&
            !string.IsNullOrWhiteSpace(sourceMesh.TexturePath) &&
            textureResolver is not null)
        {
            string texturePath = sourceMesh.TexturePath.Trim();
            if (!textureCache.TryGetValue(texturePath, out texture))
            {
                texture = textureResolver(texturePath);
                textureCache[texturePath] = texture;
            }
        }

        System.Windows.Media.Brush brush;
        if (texture is not null)
        {
            brush = new ImageBrush(texture)
            {
                Stretch = Stretch.Fill,
                TileMode = TileMode.Tile,
                Viewport = new System.Windows.Rect(0, 0, 1, 1),
                ViewportUnits = BrushMappingMode.Absolute
            };
        }
        else
        {
            brush = new SolidColorBrush(MeshColors[meshIndex % MeshColors.Length]);
        }

        brush.Freeze();
        return new DiffuseMaterial(brush);
    }

    private static MeshGeometry3D CreateMeshGeometry(LithTechMesh sourceMesh, Bounds bounds, double scale)
    {
        var mesh = new MeshGeometry3D();
        foreach (LithTechVector3 vertex in sourceMesh.Vertices)
        {
            mesh.Positions.Add(new Point3D(
                (vertex.X - bounds.CenterX) * scale,
                (vertex.Y - bounds.CenterY) * scale,
                (vertex.Z - bounds.CenterZ) * scale));
        }

        if (sourceMesh.HasTextureCoordinates && sourceMesh.TextureCoordinates is not null)
        {
            foreach (LithTechVector2 coordinate in sourceMesh.TextureCoordinates)
            {
                mesh.TextureCoordinates.Add(new System.Windows.Point(coordinate.X, coordinate.Y));
            }
        }

        foreach (int index in sourceMesh.TriangleIndices)
        {
            mesh.TriangleIndices.Add(index);
        }

        mesh.Freeze();
        return mesh;
    }

    private static Bounds CalculateBounds(LithTechModelDocument document)
    {
        bool hasVertex = false;
        double minX = 0;
        double minY = 0;
        double minZ = 0;
        double maxX = 0;
        double maxY = 0;
        double maxZ = 0;

        foreach (LithTechVector3 vertex in document.Meshes.SelectMany(mesh => mesh.Vertices))
        {
            if (!hasVertex)
            {
                minX = maxX = vertex.X;
                minY = maxY = vertex.Y;
                minZ = maxZ = vertex.Z;
                hasVertex = true;
                continue;
            }

            minX = Math.Min(minX, vertex.X);
            minY = Math.Min(minY, vertex.Y);
            minZ = Math.Min(minZ, vertex.Z);
            maxX = Math.Max(maxX, vertex.X);
            maxY = Math.Max(maxY, vertex.Y);
            maxZ = Math.Max(maxZ, vertex.Z);
        }

        return new Bounds(minX, minY, minZ, maxX, maxY, maxZ);
    }

    private readonly record struct Bounds(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
    {
        public double CenterX => (MinX + MaxX) / 2;
        public double CenterY => (MinY + MaxY) / 2;
        public double CenterZ => (MinZ + MaxZ) / 2;
        public double MaxDimension => Math.Max(MaxX - MinX, Math.Max(MaxY - MinY, MaxZ - MinZ));
    }
}
