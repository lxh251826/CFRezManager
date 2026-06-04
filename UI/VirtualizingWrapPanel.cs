using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace CFRezManager;

public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(
            nameof(ItemWidth),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(144.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(
            nameof(ItemHeight),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(138.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CacheRowsProperty =
        DependencyProperty.Register(
            nameof(CacheRows),
            typeof(int),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private WpfSize _extent;
    private WpfSize _viewport;
    private WpfPoint _offset;

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public int CacheRows
    {
        get => (int)GetValue(CacheRowsProperty);
        set => SetValue(CacheRowsProperty, value);
    }

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public ScrollViewer? ScrollOwner { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        ItemsControl? owner = ItemsControl.GetItemsOwner(this);
        int itemCount = owner?.Items.Count ?? 0;
        WpfSize viewport = GetFiniteViewport(availableSize);
        int columns = GetColumnCount(viewport.Width);
        int totalRows = itemCount == 0 ? 0 : (int)Math.Ceiling(itemCount / (double)columns);

        UpdateScrollInfo(viewport, new WpfSize(viewport.Width, totalRows * ItemHeight));

        if (itemCount == 0)
        {
            RemoveAllChildren();
            return availableSize;
        }

        int cacheRows = Math.Max(0, CacheRows);
        int firstVisibleRow = Math.Max(0, (int)Math.Floor(_offset.Y / ItemHeight) - cacheRows);
        int lastVisibleRow = Math.Min(
            totalRows - 1,
            (int)Math.Ceiling((_offset.Y + viewport.Height) / ItemHeight) + cacheRows);

        int firstIndex = Math.Min(itemCount - 1, firstVisibleRow * columns);
        int lastIndex = Math.Min(itemCount - 1, ((lastVisibleRow + 1) * columns) - 1);

        CleanupItemsOutsideRange(firstIndex, lastIndex);
        RealizeItems(firstIndex, lastIndex);

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new WpfSize(ItemWidth, ItemHeight));
        }

        return availableSize;
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        int columns = GetColumnCount(finalSize.Width);

        for (int childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            UIElement child = InternalChildren[childIndex];
            int itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (itemIndex < 0)
            {
                continue;
            }

            int row = itemIndex / columns;
            int column = itemIndex % columns;
            var bounds = new Rect(
                (column * ItemWidth) - _offset.X,
                (row * ItemHeight) - _offset.Y,
                ItemWidth,
                ItemHeight);

            child.Arrange(bounds);
        }

        return finalSize;
    }

    protected override void BringIndexIntoView(int index)
    {
        int columns = GetColumnCount(_viewport.Width);
        int row = index / columns;
        SetVerticalOffset(row * ItemHeight);
    }

    public void LineUp() => SetVerticalOffset(VerticalOffset - ItemHeight);
    public void LineDown() => SetVerticalOffset(VerticalOffset + ItemHeight);
    public void LineLeft() => SetHorizontalOffset(HorizontalOffset - ItemWidth);
    public void LineRight() => SetHorizontalOffset(HorizontalOffset + ItemWidth);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void PageLeft() => SetHorizontalOffset(HorizontalOffset - ViewportWidth);
    public void PageRight() => SetHorizontalOffset(HorizontalOffset + ViewportWidth);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - ItemHeight * 3);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + ItemHeight * 3);
    public void MouseWheelLeft() => SetHorizontalOffset(HorizontalOffset - ItemWidth);
    public void MouseWheelRight() => SetHorizontalOffset(HorizontalOffset + ItemWidth);

    public void SetHorizontalOffset(double offset)
    {
        double value = ClampOffset(offset, ExtentWidth, ViewportWidth);
        if (AreClose(value, _offset.X))
        {
            return;
        }

        _offset.X = value;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetVerticalOffset(double offset)
    {
        double value = ClampOffset(offset, ExtentHeight, ViewportHeight);
        if (AreClose(value, _offset.Y))
        {
            return;
        }

        _offset.Y = value;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        int childIndex = InternalChildren.IndexOf((UIElement)visual);
        if (childIndex < 0)
        {
            return Rect.Empty;
        }

        int itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
        if (itemIndex < 0)
        {
            return Rect.Empty;
        }

        int columns = GetColumnCount(ViewportWidth);
        int row = itemIndex / columns;
        double itemTop = row * ItemHeight;
        double itemBottom = itemTop + ItemHeight;

        if (itemTop < VerticalOffset)
        {
            SetVerticalOffset(itemTop);
        }
        else if (itemBottom > VerticalOffset + ViewportHeight)
        {
            SetVerticalOffset(itemBottom - ViewportHeight);
        }

        return new Rect(0, itemTop, ItemWidth, ItemHeight);
    }

    private void RealizeItems(int firstIndex, int lastIndex)
    {
        IItemContainerGenerator generator = ItemContainerGenerator;
        GeneratorPosition startPosition = generator.GeneratorPositionFromIndex(firstIndex);
        int childIndex = startPosition.Offset == 0 ? startPosition.Index : startPosition.Index + 1;

        using IDisposable generation = generator.StartAt(startPosition, GeneratorDirection.Forward, true);
        for (int itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
        {
            bool newlyRealized;
            var child = (UIElement)generator.GenerateNext(out newlyRealized);

            if (!newlyRealized)
            {
                continue;
            }

            if (childIndex >= InternalChildren.Count)
            {
                AddInternalChild(child);
            }
            else
            {
                InsertInternalChild(childIndex, child);
            }

            generator.PrepareItemContainer(child);
        }
    }

    private void CleanupItemsOutsideRange(int firstIndex, int lastIndex)
    {
        IItemContainerGenerator generator = ItemContainerGenerator;

        for (int childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var position = new GeneratorPosition(childIndex, 0);
            int itemIndex = generator.IndexFromGeneratorPosition(position);
            if (itemIndex >= firstIndex && itemIndex <= lastIndex)
            {
                continue;
            }

            generator.Remove(position, 1);
            RemoveInternalChildRange(childIndex, 1);
        }
    }

    private void RemoveAllChildren()
    {
        if (InternalChildren.Count == 0)
        {
            return;
        }

        ItemContainerGenerator.Remove(new GeneratorPosition(0, 0), InternalChildren.Count);
        RemoveInternalChildRange(0, InternalChildren.Count);
    }

    private void UpdateScrollInfo(WpfSize viewport, WpfSize extent)
    {
        bool changed = !AreClose(viewport.Width, _viewport.Width) ||
                       !AreClose(viewport.Height, _viewport.Height) ||
                       !AreClose(extent.Width, _extent.Width) ||
                       !AreClose(extent.Height, _extent.Height);

        _viewport = viewport;
        _extent = extent;
        _offset = new WpfPoint(
            ClampOffset(_offset.X, _extent.Width, _viewport.Width),
            ClampOffset(_offset.Y, _extent.Height, _viewport.Height));

        if (changed)
        {
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    private WpfSize GetFiniteViewport(WpfSize availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? ActualWidth : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? ActualHeight : availableSize.Height;

        if (double.IsNaN(width) || width <= 0)
        {
            width = ItemWidth;
        }

        if (double.IsNaN(height) || height <= 0)
        {
            height = ItemHeight;
        }

        return new WpfSize(width, height);
    }

    private int GetColumnCount(double width)
    {
        if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0)
        {
            width = ItemWidth;
        }

        return Math.Max(1, (int)Math.Floor(width / ItemWidth));
    }

    private static double ClampOffset(double offset, double extent, double viewport)
    {
        double max = Math.Max(0, extent - viewport);
        if (double.IsNaN(offset) || offset < 0)
        {
            return 0;
        }

        return offset > max ? max : offset;
    }

    private static bool AreClose(double left, double right)
    {
        return Math.Abs(left - right) < 0.1;
    }
}
