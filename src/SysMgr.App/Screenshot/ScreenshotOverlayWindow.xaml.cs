using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SysMgr.App.Screenshot;

/// <summary>
/// 全屏冻结画面上的选区窗口：区域模式拖拽框选，窗口模式高亮并点击拾取。
/// 关闭后通过 <see cref="Result"/> / <see cref="SelectedWindow"/> 把结果交回调用方。
/// </summary>
public partial class ScreenshotOverlayWindow : Window
{
    private readonly BitmapSource _screen;
    private readonly bool _windowMode;
    private readonly List<IntPtr> _candidateWindows = new();
    private readonly Int32Rect _virtualBounds;

    private Point _startPoint;
    private bool _dragging;
    private Rect _selection;
    private IntPtr _hoverWindow;

    public ScreenshotOverlayWindow(BitmapSource screen, bool windowMode)
    {
        InitializeComponent();

        _screen = screen;
        _windowMode = windowMode;
        _virtualBounds = ScreenCapture.VirtualScreenBounds();

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        ScreenImage.Source = screen;

        if (windowMode)
        {
            Cursor = Cursors.Hand;
            HintText.Text = "移动鼠标高亮窗口，点击左键截取 · Esc 取消";
        }
        else
        {
            Cursor = Cursors.Cross;
            HintText.Text = "拖动鼠标选择截图区域 · 右键 / Esc 取消";
        }

        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
    }

    /// <summary>区域截图结果（已按屏幕原始物理像素裁剪）。窗口模式为 null。</summary>
    public BitmapSource? Result { get; private set; }

    /// <summary>窗口截图选中的窗口句柄（区域模式为 IntPtr.Zero）。</summary>
    public IntPtr SelectedWindow { get; private set; }

    /// <summary>整屏位图物理像素与窗口 DIP 的比例。</summary>
    private double Scale => _screen.PixelWidth / Math.Max(ActualWidth, 1);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Focus();

        if (!_windowMode)
        {
            return;
        }

        var self = new WindowInteropHelper(this).Handle;
        foreach (var hwnd in ScreenCapture.EnumerateTopLevelWindows())
        {
            if (hwnd != self)
            {
                _candidateWindows.Add(hwnd);
            }
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (_windowMode)
        {
            if (_hoverWindow != IntPtr.Zero)
            {
                SelectedWindow = _hoverWindow;
                DialogResult = true;
            }

            return;
        }

        _startPoint = e.GetPosition(Root);
        _dragging = true;
        _selection = new Rect(_startPoint, _startPoint);
        CaptureMouse();
        UpdateRegionVisuals(_startPoint);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_windowMode)
        {
            UpdateWindowHover();
            return;
        }

        var current = e.GetPosition(Root);
        if (!_dragging)
        {
            UpdateMagnifier(current);
            return;
        }

        _selection = Normalize(_startPoint, current);
        UpdateSelectionVisuals();
        UpdateMagnifier(current);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();

        var scale = Scale;
        var rect = new Int32Rect(
            (int)Math.Round(_selection.X * scale),
            (int)Math.Round(_selection.Y * scale),
            (int)Math.Round(_selection.Width * scale),
            (int)Math.Round(_selection.Height * scale));

        if (rect.Width < 2 || rect.Height < 2)
        {
            DialogResult = false;
            return;
        }

        Result = ScreenCapture.Crop(_screen, rect);
        DialogResult = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        DialogResult = false;
    }

    private void UpdateWindowHover()
    {
        var (px, py) = ScreenCapture.GetCursorPosition();
        var self = new WindowInteropHelper(this).Handle;

        var found = IntPtr.Zero;
        foreach (var hwnd in _candidateWindows)
        {
            if (hwnd == self)
            {
                continue;
            }

            var bounds = ScreenCapture.GetWindowRect(hwnd);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            if (px >= bounds.X && px < bounds.X + bounds.Width
                && py >= bounds.Y && py < bounds.Y + bounds.Height)
            {
                found = hwnd;
                break;
            }
        }

        if (found == _hoverWindow)
        {
            return;
        }

        _hoverWindow = found;
        if (found == IntPtr.Zero)
        {
            WindowHighlight.Visibility = Visibility.Collapsed;
            SizeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var scale = Scale;
        var rect = ScreenCapture.GetWindowRect(found);
        var dip = new Rect(
            (rect.X - _virtualBounds.X) / scale,
            (rect.Y - _virtualBounds.Y) / scale,
            rect.Width / scale,
            rect.Height / scale);

        Canvas.SetLeft(WindowHighlight, dip.X);
        Canvas.SetTop(WindowHighlight, dip.Y);
        WindowHighlight.Width = dip.Width;
        WindowHighlight.Height = dip.Height;
        WindowHighlight.Visibility = Visibility.Visible;

        SizeText.Text = $"{rect.Width} × {rect.Height}";
        PlaceBadge(dip.X, dip.Y - 34);
        SizeBadge.Visibility = Visibility.Visible;
    }

    private void UpdateSelectionVisuals()
    {
        var s = _selection;
        Canvas.SetLeft(SelectionBorder, s.X);
        Canvas.SetTop(SelectionBorder, s.Y);
        SelectionBorder.Width = s.Width;
        SelectionBorder.Height = s.Height;
        SelectionBorder.Visibility = Visibility.Visible;

        Canvas.SetLeft(SelectionFill, s.X);
        Canvas.SetTop(SelectionFill, s.Y);
        SelectionFill.Width = s.Width;
        SelectionFill.Height = s.Height;
        SelectionFill.Visibility = Visibility.Visible;

        var scale = Scale;
        SizeText.Text = $"{(int)Math.Round(s.Width * scale)} × {(int)Math.Round(s.Height * scale)}";
        PlaceBadge(s.X, s.Y - 34);
        SizeBadge.Visibility = Visibility.Visible;

        UpdateDim(s);
        HintBar.Visibility = Visibility.Collapsed;
    }

    private void UpdateRegionVisuals(Point start)
    {
        _selection = new Rect(start, start);
        UpdateSelectionVisuals();
        UpdateMagnifier(start);
    }

    /// <summary>把选区之外的画面压暗。</summary>
    private void UpdateDim(Rect selection)
    {
        var w = ActualWidth;
        var h = ActualHeight;

        SetRect(DimTop, 0, 0, w, Math.Max(selection.Y, 0));
        SetRect(DimBottom, 0, selection.Bottom, w, Math.Max(h - selection.Bottom, 0));
        SetRect(DimLeft, 0, selection.Y, Math.Max(selection.X, 0), selection.Height);
        SetRect(DimRight, selection.Right, selection.Y, Math.Max(w - selection.Right, 0), selection.Height);
    }

    private static void SetRect(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(width, 0);
        element.Height = Math.Max(height, 0);
    }

    private void PlaceBadge(double x, double y)
    {
        Canvas.SetLeft(SizeBadge, Math.Clamp(x, 0, Math.Max(ActualWidth - 120, 0)));
        Canvas.SetTop(SizeBadge, Math.Clamp(y, 6, Math.Max(ActualHeight - 30, 6)));
    }

    private void UpdateMagnifier(Point position)
    {
        var scale = Scale;
        var rx = (int)Math.Round(position.X * scale);
        var ry = (int)Math.Round(position.Y * scale);

        const int sampleSize = 21;
        var region = new Int32Rect(rx - sampleSize / 2, ry - sampleSize / 2, sampleSize, sampleSize);

        MagnifierImage.Source = ScreenCapture.Magnify(_screen, region);
        MagnifierText.Text = $"{_virtualBounds.X + rx}, {_virtualBounds.Y + ry}";
        Magnifier.Visibility = Visibility.Visible;

        var offset = 26.0;
        var left = position.X + offset;
        var top = position.Y + offset;
        if (left + Magnifier.Width > ActualWidth)
        {
            left = position.X - Magnifier.Width - offset;
        }

        if (top + Magnifier.Height > ActualHeight)
        {
            top = position.Y - Magnifier.Height - offset;
        }

        Canvas.SetLeft(Magnifier, Math.Max(left, 0));
        Canvas.SetTop(Magnifier, Math.Max(top, 0));
    }

    private static Rect Normalize(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
