using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SysMgr.Modules.Visualization;

namespace SysMgr.App.Controls;

/// <summary>
/// Treemap 可视化控件（FR-VIS-01）。
/// Squarified 布局（Bruls 算法，面积∝大小），颜色按 VisFileKind：
/// 视频=橙、图片=蓝、文档=绿、安装包=红、数据库=紫、其他=灰；目录为中性深色块。
/// 交互：单击选中、双击下钻（目录）/打开（文件）、滚轮缩放（上滚下钻、下滚返回）、右键菜单。
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    // FR-VIS-01 类型着色
    private static readonly Color VideoColor = (Color)ColorConverter.ConvertFromString("#F59E0B");
    private static readonly Color ImageColor = (Color)ColorConverter.ConvertFromString("#38BDF8");
    private static readonly Color DocumentColor = (Color)ColorConverter.ConvertFromString("#34D399");
    private static readonly Color InstallerColor = (Color)ColorConverter.ConvertFromString("#F43F5E");
    private static readonly Color DatabaseColor = (Color)ColorConverter.ConvertFromString("#A78BFA");
    private static readonly Color OtherColor = (Color)ColorConverter.ConvertFromString("#8A93A6");

    private static readonly Brush DirFill = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#141D31")));
    private static readonly Brush DirBorder = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2C3B5C")));
    private static readonly Brush AggregateFill = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#232833")));
    private static readonly Brush AggregateBorder = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3A4152")));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E5EAF3")));
    private static readonly Brush SubTextBrush = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9AA4B8")));
    private static readonly Brush HoverBorderBrush = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7DD3FC")));
    private static readonly Brush SelectedBorderBrush = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8")));

    private readonly Dictionary<VisFileKind, Brush> _kindFills = new();
    private readonly Dictionary<VisFileKind, Brush> _kindBorders = new();

    private List<(TreemapNode Node, Rect Rect)> _hit = new();
    private TreemapNode? _hover;

    public TreemapControl()
    {
        SnapsToDevicePixels = true;
        foreach (VisFileKind kind in Enum.GetValues<VisFileKind>())
        {
            var color = KindColor(kind);
            _kindFills[kind] = Freeze(new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B)));
            _kindBorders[kind] = Freeze(new SolidColorBrush(Color.FromArgb(0xDD, color.R, color.G, color.B)));
        }
    }

    // ---------------- 依赖属性 ----------------

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IReadOnlyList<TreemapNode>),
            typeof(TreemapControl),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => ((TreemapControl)d)._layoutDirty = true));

    public IReadOnlyList<TreemapNode>? ItemsSource
    {
        get => (IReadOnlyList<TreemapNode>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty SelectedNodeProperty =
        DependencyProperty.Register(
            nameof(SelectedNode),
            typeof(TreemapNode),
            typeof(TreemapControl),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public TreemapNode? SelectedNode
    {
        get => (TreemapNode?)GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    // ---------------- 事件 ----------------

    /// <summary>双击目录或滚轮上滚请求下钻。</summary>
    public event Action<TreemapNode>? DrillDownRequested;

    /// <summary>双击文件请求在资源管理器中打开。</summary>
    public event Action<TreemapNode>? OpenRequested;

    /// <summary>滚轮下滚请求返回上级。</summary>
    public event Action? NavigateUpRequested;

    /// <summary>右键节点请求上下文菜单（由页面构建并打开）。</summary>
    public event Action<TreemapNode>? NodeContextMenuRequested;

    // ---------------- 布局 ----------------

    private bool _layoutDirty = true;
    private Size _layoutSize;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var items = ItemsSource;
        if (items is null || items.Count == 0 || RenderSize.Width < 4 || RenderSize.Height < 4)
        {
            return;
        }

        if (_layoutDirty || _layoutSize != RenderSize)
        {
            var bounds = new Rect(0, 0, RenderSize.Width, RenderSize.Height);
            var rects = Squarify(items.Select(i => Math.Max(1, i.TotalBytes)).ToList(), bounds);
            _hit = items.Zip(rects, (n, r) => (n, r)).ToList();
            _layoutSize = RenderSize;
            _layoutDirty = false;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        foreach (var (node, rect) in _hit)
        {
            if (rect.Width < 2 || rect.Height < 2)
            {
                continue;
            }

            var box = new Rect(rect.X + 0.75, rect.Y + 0.75, Math.Max(0, rect.Width - 1.5), Math.Max(0, rect.Height - 1.5));
            Brush fill, border;
            double borderThickness = 1;
            if (node.IsOtherAggregate)
            {
                fill = AggregateFill;
                border = AggregateBorder;
            }
            else if (node.IsDirectory)
            {
                fill = DirFill;
                border = DirBorder;
            }
            else
            {
                fill = _kindFills[node.DominantKind];
                border = _kindBorders[node.DominantKind];
            }

            dc.DrawRectangle(fill, new Pen(border, borderThickness), box);

            if (ReferenceEquals(node, SelectedNode))
            {
                dc.DrawRectangle(null, new Pen(SelectedBorderBrush, 2), box);
            }
            else if (ReferenceEquals(node, _hover))
            {
                dc.DrawRectangle(null, new Pen(HoverBorderBrush, 1.4), box);
            }

            // 标签：空间足够时画名称 + 大小
            if (box.Width >= 52 && box.Height >= 18)
            {
                var name = node.IsDirectory ? node.Name + " \\" : node.Name;
                var nameText = new FormattedText(
                    name,
                    System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    bold,
                    11.5,
                    TextBrush,
                    dpi)
                {
                    MaxTextWidth = box.Width - 8,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis
                };
                dc.DrawText(nameText, new Point(box.X + 5, box.Y + 3));

                if (box.Height >= 36)
                {
                    var sizeText = new FormattedText(
                        Converters.BytesFormatter.Format(node.TotalBytes),
                        System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        10,
                        SubTextBrush,
                        dpi)
                    {
                        MaxTextWidth = box.Width - 8,
                        MaxLineCount = 1
                    };
                    dc.DrawText(sizeText, new Point(box.X + 5, box.Y + 18));
                }
            }
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _layoutDirty = true;
    }

    // ---------------- 交互 ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var node = Hit(e.GetPosition(this));
        if (!ReferenceEquals(node, _hover))
        {
            _hover = node;
            Cursor = node is { IsDirectory: true } ? Cursors.Hand : Cursors.Arrow;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        Cursor = Cursors.Arrow;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var node = Hit(e.GetPosition(this));
        if (node is null)
        {
            return;
        }

        SelectedNode = node;
        if (e.ClickCount == 2)
        {
            if (node.IsDirectory && node.Children.Count > 0)
            {
                DrillDownRequested?.Invoke(node);
            }
            else if (!node.IsDirectory && !node.IsOtherAggregate)
            {
                OpenRequested?.Invoke(node);
            }
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var node = Hit(e.GetPosition(this));
        if (node is null)
        {
            return;
        }

        SelectedNode = node;
        NodeContextMenuRequested?.Invoke(node);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Delta > 0)
        {
            var node = Hit(e.GetPosition(this));
            if (node is { IsDirectory: true } && node.Children.Count > 0)
            {
                DrillDownRequested?.Invoke(node);
                e.Handled = true;
            }
        }
        else
        {
            NavigateUpRequested?.Invoke();
            e.Handled = true;
        }
    }

    private TreemapNode? Hit(Point p)
    {
        // 后绘制者优先（小矩形可能与大矩形边缘重叠）
        for (var i = _hit.Count - 1; i >= 0; i--)
        {
            if (_hit[i].Rect.Contains(p))
            {
                return _hit[i].Node;
            }
        }
        return null;
    }

    private static Color KindColor(VisFileKind kind) => kind switch
    {
        VisFileKind.Video => VideoColor,
        VisFileKind.Image => ImageColor,
        VisFileKind.Document => DocumentColor,
        VisFileKind.Installer => InstallerColor,
        VisFileKind.Database => DatabaseColor,
        _ => OtherColor
    };

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    // ---------------- Squarified 布局（Bruls et al.） ----------------

    private static List<Rect> Squarify(IReadOnlyList<long> sizes, Rect bounds)
    {
        var rects = Enumerable.Repeat(Rect.Empty, sizes.Count).ToList();
        if (sizes.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return rects;
        }

        double total = sizes.Sum(s => (double)s);
        if (total <= 0)
        {
            return rects;
        }

        var scale = bounds.Width * bounds.Height / total;
        var areas = sizes.Select(s => s * scale).ToList();

        var remaining = bounds;
        var row = new List<int>();
        double rowArea = 0;
        var i = 0;
        while (i < areas.Count)
        {
            var side = Math.Min(remaining.Width, remaining.Height);
            if (row.Count == 0
                || side <= 0
                || WorstWith(row, areas, rowArea, i, side) <= Worst(row, areas, rowArea, side))
            {
                row.Add(i);
                rowArea += areas[i];
                i++;
            }
            else
            {
                remaining = LayoutRow(row, areas, rowArea, remaining, rects);
                row.Clear();
                rowArea = 0;
            }
        }
        if (row.Count > 0)
        {
            LayoutRow(row, areas, rowArea, remaining, rects);
        }
        return rects;
    }

    private static double Worst(List<int> row, IReadOnlyList<double> areas, double rowArea, double side)
    {
        if (rowArea <= 0 || side <= 0)
        {
            return double.MaxValue;
        }
        double max = row.Max(i => areas[i]);
        double min = row.Min(i => areas[i]);
        var s2 = rowArea * rowArea;
        var w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    private static double WorstWith(List<int> row, IReadOnlyList<double> areas, double rowArea, int next, double side)
    {
        var newArea = rowArea + areas[next];
        if (newArea <= 0 || side <= 0)
        {
            return double.MaxValue;
        }
        double max = row.Count == 0 ? areas[next] : Math.Max(row.Max(i => areas[i]), areas[next]);
        double min = row.Count == 0 ? areas[next] : Math.Min(row.Min(i => areas[i]), areas[next]);
        var s2 = newArea * newArea;
        var w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    private static Rect LayoutRow(
        List<int> row,
        IReadOnlyList<double> areas,
        double rowArea,
        Rect remaining,
        List<Rect> rects)
    {
        if (remaining.Width >= remaining.Height)
        {
            var stripWidth = remaining.Height > 0 ? rowArea / remaining.Height : 0;
            var y = remaining.Y;
            foreach (var i in row)
            {
                var h = stripWidth > 0 ? areas[i] / stripWidth : 0;
                rects[i] = new Rect(remaining.X, y, stripWidth, h);
                y += h;
            }
            return new Rect(
                remaining.X + stripWidth,
                remaining.Y,
                Math.Max(0, remaining.Width - stripWidth),
                remaining.Height);
        }
        else
        {
            var stripHeight = remaining.Width > 0 ? rowArea / remaining.Width : 0;
            var x = remaining.X;
            foreach (var i in row)
            {
                var w = stripHeight > 0 ? areas[i] / stripHeight : 0;
                rects[i] = new Rect(x, remaining.Y, w, stripHeight);
                x += w;
            }
            return new Rect(
                remaining.X,
                remaining.Y + stripHeight,
                remaining.Width,
                Math.Max(0, remaining.Height - stripHeight));
        }
    }
}
