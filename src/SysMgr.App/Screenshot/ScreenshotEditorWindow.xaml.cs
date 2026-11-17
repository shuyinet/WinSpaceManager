using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SysMgr.App.Screenshot;

/// <summary>
/// 截图编辑器：矩形 / 椭圆 / 箭头 / 画笔 / 文字 / 马赛克标注，
/// 支持撤销重做、复制到剪贴板、保存为 PNG / JPG。
/// 画布坐标 = 截图原始像素，标注在导出时按原始分辨率合成。
/// </summary>
public partial class ScreenshotEditorWindow : Window
{
    private readonly BitmapSource _source;
    private readonly List<Annotation> _items = new();
    private readonly Stack<Annotation> _redo = new();
    private readonly string _saveDirectory;

    private Annotation? _pending;
    private AnnotationTool _tool = AnnotationTool.Rectangle;
    private Color _color = Color.FromRgb(0xFF, 0x4D, 0x62);
    private double _thickness = 4;
    private Point _startPoint;
    private bool _drawing;
    private TextBox? _textBox;

    public ScreenshotEditorWindow(BitmapSource source, string saveDirectory)
    {
        InitializeComponent();

        _source = source;
        _saveDirectory = string.IsNullOrWhiteSpace(saveDirectory)
            ? ScreenshotService.DefaultSaveDirectory
            : saveDirectory;

        BaseImage.Source = source;
        Layer.Source = source;
        Layer.Items = _items;

        var width = (double)source.PixelWidth;
        var height = (double)source.PixelHeight;

        CanvasHost.Width = width;
        CanvasHost.Height = height;
        Layer.Width = width;
        Layer.Height = height;

        SizeLabel.Text = $"{source.PixelWidth} × {source.PixelHeight} px";
        TitleHint.Text = $"· {source.PixelWidth} × {source.PixelHeight}";
        StatusLabel.Text = "拖动鼠标添加标注 · Ctrl+Z 撤销 · Ctrl+S 保存";

        PreviewKeyDown += OnPreviewKeyDown;
        UpdateActionButtons();
    }

    /// <summary>本次保存的文件路径（未保存为 null）。</summary>
    public string? SavedPath { get; private set; }

    /// <summary>保存成功后触发，参数为文件完整路径。</summary>
    public event Action<string>? Saved;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 拖拽过程中鼠标状态异常，忽略
            }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse<AnnotationTool>(tag, out var tool))
        {
            _tool = tool;
            CanvasHost.Cursor = tool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Cross;
            CommitTextBox();
        }
    }

    private void Color_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            try
            {
                _color = (Color)ColorConverter.ConvertFromString(tag);
            }
            catch (FormatException)
            {
                // 颜色值非法时保持原色
            }
        }
    }

    private void Width_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && double.TryParse(tag, out var width))
        {
            _thickness = width;
        }
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(CanvasHost);
        CommitTextBox();

        if (_tool == AnnotationTool.Text)
        {
            ShowTextBox(position);
            return;
        }

        _startPoint = position;
        _drawing = true;
        CanvasHost.CaptureMouse();

        _pending = new Annotation
        {
            Tool = _tool,
            Start = position,
            End = position,
            Color = _color,
            Thickness = _thickness,
            Preview = true
        };

        if (_tool == AnnotationTool.Pen)
        {
            _pending.Points.Add(position);
        }

        Layer.Pending = _pending;
        Layer.InvalidateVisual();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_drawing || _pending is null)
        {
            return;
        }

        var position = e.GetPosition(CanvasHost);
        _pending.End = position;

        if (_pending.Tool == AnnotationTool.Pen)
        {
            var last = _pending.Points[^1];
            if (Math.Abs(position.X - last.X) + Math.Abs(position.Y - last.Y) > 1.5)
            {
                _pending.Points.Add(position);
            }
        }

        Layer.InvalidateVisual();
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing || _pending is null)
        {
            return;
        }

        _drawing = false;
        CanvasHost.ReleaseMouseCapture();

        var annotation = _pending;
        _pending = null;
        Layer.Pending = null;

        var tooSmall = annotation.Tool switch
        {
            AnnotationTool.Pen => annotation.Points.Count < 2,
            AnnotationTool.Mosaic => annotation.Bounds.Width < 4 || annotation.Bounds.Height < 4,
            _ => annotation.Bounds.Width < 2 && annotation.Bounds.Height < 2
        };

        if (tooSmall)
        {
            Layer.InvalidateVisual();
            return;
        }

        annotation.Preview = false;
        _items.Add(annotation);
        _redo.Clear();
        Layer.InvalidateVisual();
        UpdateActionButtons();
        StatusLabel.Text = $"已添加 {Describe(annotation.Tool)} 标注，共 {_items.Count} 处";
    }

    private void ShowTextBox(Point position)
    {
        var box = new TextBox
        {
            Width = 240,
            MinHeight = 30,
            FontSize = (int)AnnotationRenderer.TextFontSize,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = new SolidColorBrush(_color),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x08, 0x0E, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x18, 0xC0, 0xFF)),
            BorderThickness = new Thickness(1),
            CaretBrush = Brushes.White,
            Padding = new Thickness(4, 2, 4, 2),
            AcceptsReturn = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Math.Max(position.X, 0), Math.Max(position.Y, 0), 0, 0)
        };

        box.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                CommitTextBox();
                args.Handled = true;
            }
            else if (args.Key == Key.Escape)
            {
                RemoveTextBox();
                args.Handled = true;
            }
        };

        box.LostKeyboardFocus += (_, _) => CommitTextBox();

        CanvasHost.Children.Add(box);
        _textBox = box;
        box.Focus();
    }

    private void CommitTextBox()
    {
        if (_textBox is null)
        {
            return;
        }

        var box = _textBox;
        _textBox = null;

        var text = box.Text?.Trim() ?? string.Empty;
        var position = new Point(box.Margin.Left, box.Margin.Top);
        CanvasHost.Children.Remove(box);

        if (text.Length == 0)
        {
            return;
        }

        _items.Add(new Annotation
        {
            Tool = AnnotationTool.Text,
            Start = position,
            End = position,
            Text = text,
            Color = _color,
            Thickness = _thickness
        });

        _redo.Clear();
        Layer.InvalidateVisual();
        UpdateActionButtons();
        StatusLabel.Text = $"已添加文字标注，共 {_items.Count} 处";
    }

    private void RemoveTextBox()
    {
        if (_textBox is null)
        {
            return;
        }

        CanvasHost.Children.Remove(_textBox);
        _textBox = null;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
        {
            return;
        }

        _items.Clear();
        _redo.Clear();
        Layer.InvalidateVisual();
        UpdateActionButtons();
        StatusLabel.Text = "已清空全部标注";
    }

    private void Undo()
    {
        if (_items.Count == 0)
        {
            return;
        }

        var last = _items[^1];
        _items.RemoveAt(_items.Count - 1);
        _redo.Push(last);
        Layer.InvalidateVisual();
        UpdateActionButtons();
        StatusLabel.Text = $"已撤销，剩余 {_items.Count} 处标注";
    }

    private void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        var item = _redo.Pop();
        _items.Add(item);
        Layer.InvalidateVisual();
        UpdateActionButtons();
        StatusLabel.Text = $"已重做，共 {_items.Count} 处标注";
    }

    private void UpdateActionButtons()
    {
        UndoButton.IsEnabled = _items.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
        ClearButton.IsEnabled = _items.Count > 0;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetImage(RenderComposite());
            StatusLabel.Text = "已复制到剪贴板";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"复制失败：{ex.Message}";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveToFile();

    private void SaveToFile()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg",
            DefaultExt = ".png",
            FileName = $"截图_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            InitialDirectory = Directory.Exists(_saveDirectory) ? _saveDirectory : null
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        try
        {
            var bitmap = RenderComposite();
            BitmapEncoder encoder = Path.GetExtension(dialog.FileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
                _ => new PngBitmapEncoder()
            };

            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write);
            encoder.Save(stream);

            SavedPath = dialog.FileName;
            StatusLabel.Text = $"已保存：{dialog.FileName}";
            Saved?.Invoke(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：{ex.Message}", "截图", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>把原图与全部标注按原始分辨率合成为一张位图。</summary>
    private BitmapSource RenderComposite()
    {
        var width = _source.PixelWidth;
        var height = _source.PixelHeight;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(_source, new Rect(0, 0, width, height));
            foreach (var annotation in _items)
            {
                AnnotationRenderer.Draw(context, annotation, _source);
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (e.Key)
            {
                case Key.Z:
                    Undo();
                    e.Handled = true;
                    return;
                case Key.Y:
                    Redo();
                    e.Handled = true;
                    return;
                case Key.S:
                    SaveToFile();
                    e.Handled = true;
                    return;
                case Key.C:
                    Copy_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Escape)
        {
            if (_textBox is not null)
            {
                RemoveTextBox();
            }
            else
            {
                Close();
            }

            e.Handled = true;
        }
    }

    private static string Describe(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Rectangle => "矩形",
        AnnotationTool.Ellipse => "椭圆",
        AnnotationTool.Arrow => "箭头",
        AnnotationTool.Pen => "画笔",
        AnnotationTool.Mosaic => "马赛克",
        _ => "文字"
    };
}
