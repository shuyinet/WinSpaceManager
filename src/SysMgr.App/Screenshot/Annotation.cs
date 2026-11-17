using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SysMgr.App.Screenshot;

/// <summary>标注工具类型。</summary>
public enum AnnotationTool
{
    Rectangle,
    Ellipse,
    Arrow,
    Pen,
    Text,
    Mosaic
}

/// <summary>一条标注（坐标单位为图像像素，与截图原始分辨率一致）。</summary>
public sealed class Annotation
{
    public AnnotationTool Tool { get; init; }
    public Point Start { get; set; }
    public Point End { get; set; }
    public List<Point> Points { get; } = new();
    public string? Text { get; set; }
    public Color Color { get; init; } = Color.FromRgb(0xFF, 0x4D, 0x62);
    public double Thickness { get; init; } = 3;

    /// <summary>马赛克纹理缓存（同一张源图只生成一次）。</summary>
    public BitmapSource? MosaicCache { get; set; }

    /// <summary>拖拽中的临时预览：马赛克先画半透明块，避免实时逐像素计算。</summary>
    public bool Preview { get; set; }

    public Rect Bounds
    {
        get
        {
            var rect = new Rect(Start, End);
            return new Rect(
                Math.Min(rect.Left, rect.Right),
                Math.Min(rect.Top, rect.Bottom),
                Math.Abs(rect.Width),
                Math.Abs(rect.Height));
        }
    }
}

/// <summary>把标注画到 DrawingContext：编辑器预览与导出位图共用同一套绘制逻辑。</summary>
public static class AnnotationRenderer
{
    public const double TextFontSize = 22;

    public static void Draw(DrawingContext dc, Annotation annotation, BitmapSource source)
    {
        var brush = new SolidColorBrush(annotation.Color);
        brush.Freeze();

        var pen = new Pen(brush, annotation.Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();

        switch (annotation.Tool)
        {
            case AnnotationTool.Rectangle:
                dc.DrawRectangle(null, pen, annotation.Bounds);
                break;

            case AnnotationTool.Ellipse:
            {
                var bounds = annotation.Bounds;
                dc.DrawEllipse(null, pen,
                    new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2),
                    bounds.Width / 2, bounds.Height / 2);
                break;
            }

            case AnnotationTool.Arrow:
                DrawArrow(dc, annotation, brush, pen);
                break;

            case AnnotationTool.Pen:
                DrawPen(dc, annotation, pen);
                break;

            case AnnotationTool.Text:
            {
                if (string.IsNullOrEmpty(annotation.Text))
                {
                    break;
                }

                var formatted = new FormattedText(
                    annotation.Text,
                    System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"),
                    TextFontSize,
                    brush,
                    1.0);

                dc.DrawText(formatted, annotation.Start);
                break;
            }

            case AnnotationTool.Mosaic:
            {
                var bounds = annotation.Bounds;
                if (bounds.Width < 2 || bounds.Height < 2)
                {
                    break;
                }

                if (annotation.Preview)
                {
                    var previewBrush = new SolidColorBrush(Color.FromArgb(0xB4, 0x10, 0x18, 0x20));
                    previewBrush.Freeze();
                    dc.DrawRectangle(previewBrush, pen, bounds);
                    break;
                }

                annotation.MosaicCache ??= Pixelate(source, bounds);
                if (annotation.MosaicCache is { } mosaic)
                {
                    dc.DrawImage(mosaic, bounds);
                }

                break;
            }
        }
    }

    private static void DrawArrow(DrawingContext dc, Annotation annotation, Brush brush, Pen pen)
    {
        var start = annotation.Start;
        var end = annotation.End;
        var vector = end - start;
        if (vector.Length < 1)
        {
            return;
        }

        vector.Normalize();
        var headLength = Math.Clamp(10 + annotation.Thickness * 2.4, 10, 30);
        var bodyEnd = end - vector * (headLength * 0.75);

        dc.DrawLine(pen, start, bodyEnd);

        var perpendicular = new Vector(-vector.Y, vector.X);
        var half = headLength * 0.42;
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(end, true, true);
            ctx.LineTo(bodyEnd + perpendicular * half, true, false);
            ctx.LineTo(bodyEnd - perpendicular * half, true, false);
        }
        head.Freeze();
        dc.DrawGeometry(brush, null, head);
    }

    private static void DrawPen(DrawingContext dc, Annotation annotation, Pen pen)
    {
        if (annotation.Points.Count < 2)
        {
            if (annotation.Points.Count == 1)
            {
                dc.DrawEllipse(pen.Brush, null, annotation.Points[0],
                    annotation.Thickness / 2, annotation.Thickness / 2);
            }

            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(annotation.Points[0], false, false);
            for (var i = 1; i < annotation.Points.Count; i++)
            {
                ctx.LineTo(annotation.Points[i], true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    /// <summary>把指定区域按块取平均，生成马赛克纹理（尺寸与区域一致）。</summary>
    private static BitmapSource Pixelate(BitmapSource source, Rect bounds)
    {
        var region = new Int32Rect(
            (int)Math.Floor(bounds.X),
            (int)Math.Floor(bounds.Y),
            Math.Max((int)Math.Ceiling(bounds.Width), 1),
            Math.Max((int)Math.Ceiling(bounds.Height), 1));

        region = new Int32Rect(
            Math.Clamp(region.X, 0, Math.Max(source.PixelWidth - 1, 0)),
            Math.Clamp(region.Y, 0, Math.Max(source.PixelHeight - 1, 0)),
            Math.Clamp(region.Width, 1, source.PixelWidth - Math.Clamp(region.X, 0, Math.Max(source.PixelWidth - 1, 0))),
            Math.Clamp(region.Height, 1, source.PixelHeight - Math.Clamp(region.Y, 0, Math.Max(source.PixelHeight - 1, 0))));

        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var stride = region.Width * 4;
        var pixels = new byte[stride * region.Height];
        converted.CopyPixels(region, pixels, stride, 0);

        var block = Math.Max(6, Math.Min(region.Width, region.Height) / 14);

        for (var by = 0; by < region.Height; by += block)
        {
            for (var bx = 0; bx < region.Width; bx += block)
            {
                var maxY = Math.Min(by + block, region.Height);
                var maxX = Math.Min(bx + block, region.Width);

                long b = 0, g = 0, r = 0, a = 0, count = 0;
                for (var y = by; y < maxY; y++)
                {
                    var row = y * stride;
                    for (var x = bx; x < maxX; x++)
                    {
                        var offset = row + x * 4;
                        b += pixels[offset];
                        g += pixels[offset + 1];
                        r += pixels[offset + 2];
                        a += pixels[offset + 3];
                        count++;
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                var color = ((byte)(b / count), (byte)(g / count), (byte)(r / count), (byte)(a / count));
                for (var y = by; y < maxY; y++)
                {
                    var row = y * stride;
                    for (var x = bx; x < maxX; x++)
                    {
                        var offset = row + x * 4;
                        pixels[offset] = color.Item1;
                        pixels[offset + 1] = color.Item2;
                        pixels[offset + 2] = color.Item3;
                        pixels[offset + 3] = color.Item4;
                    }
                }
            }
        }

        var result = BitmapSource.Create(
            region.Width, region.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}

/// <summary>标注图层：负责把标注列表绘制到界面上（预览）。</summary>
public sealed class AnnotationLayer : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(BitmapSource), typeof(AnnotationLayer),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public BitmapSource? Source
    {
        get => (BitmapSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public IReadOnlyList<Annotation> Items { get; set; } = Array.Empty<Annotation>();

    public Annotation? Pending { get; set; }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Source is null)
        {
            return;
        }

        foreach (var item in Items)
        {
            AnnotationRenderer.Draw(drawingContext, item, Source);
        }

        if (Pending is not null)
        {
            AnnotationRenderer.Draw(drawingContext, Pending, Source);
        }
    }
}
