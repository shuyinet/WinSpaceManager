using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SysMgr.App.Controls;

/// <summary>
/// 圆环进度（自绘，无第三方图表包）。
/// 支持任意起止角度、值变化平滑动画、圆角线帽，以及扫描中的旋转彗星弧（IsIndeterminate）。
/// </summary>
public sealed class RingProgress : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingProgress),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    public static readonly DependencyProperty ThicknessProperty =
        DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(RingProgress),
            new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackColorProperty =
        DependencyProperty.Register(nameof(TrackColor), typeof(Brush), typeof(RingProgress),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x1C, 0x2C, 0x3F)),
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundColorProperty =
        DependencyProperty.Register(nameof(ForegroundColor), typeof(Brush), typeof(RingProgress),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x18, 0xC0, 0xFF)),
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StartAngleProperty =
        DependencyProperty.Register(nameof(StartAngle), typeof(double), typeof(RingProgress),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndAngleProperty =
        DependencyProperty.Register(nameof(EndAngle), typeof(double), typeof(RingProgress),
            new FrameworkPropertyMetadata(360.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsIndeterminateProperty =
        DependencyProperty.Register(nameof(IsIndeterminate), typeof(bool), typeof(RingProgress),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIndeterminateChanged));

    /// <summary>当前值 0-100。</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(value, 0, 100));
    }

    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush TrackColor { get => (Brush)GetValue(TrackColorProperty); set => SetValue(TrackColorProperty, value); }
    public Brush ForegroundColor { get => (Brush)GetValue(ForegroundColorProperty); set => SetValue(ForegroundColorProperty, value); }

    /// <summary>弧起点角度（0=正上方，顺时针）。</summary>
    public double StartAngle { get => (double)GetValue(StartAngleProperty); set => SetValue(StartAngleProperty, value); }
    public double EndAngle { get => (double)GetValue(EndAngleProperty); set => SetValue(EndAngleProperty, value); }

    /// <summary>扫描中：隐藏数值弧，显示持续旋转的彗星弧。</summary>
    public bool IsIndeterminate { get => (bool)GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (RingProgress)d;
        if (ring.IsIndeterminate)
        {
            return;
        }
        var to = (double)e.NewValue;
        var anim = new DoubleAnimation(ring.AnimatedValue, to, TimeSpan.FromMilliseconds(900))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ring.BeginAnimation(AnimatedValueProperty, anim);
    }

    private static void OnIndeterminateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (RingProgress)d;
        if ((bool)e.NewValue)
        {
            ring.BeginAnimation(SpinAngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        }
        else
        {
            ring.BeginAnimation(SpinAngleProperty, null);
        }
    }

    private double AnimatedValue => (double)GetValue(AnimatedValueProperty);

    private static readonly DependencyProperty AnimatedValueProperty =
        DependencyProperty.Register("AnimatedValue", typeof(double), typeof(RingProgress),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty SpinAngleProperty =
        DependencyProperty.Register("SpinAngle", typeof(double), typeof(RingProgress),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var cx = w / 2;
        var cy = h / 2;
        var radius = Math.Min(w, h) / 2 - Thickness / 2 - 2;
        if (radius <= 0)
        {
            return;
        }

        var trackPen = new Pen(TrackColor, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        var sweep = EndAngle - StartAngle;
        var trackGeo = BuildArc(cx, cy, radius, StartAngle, EndAngle);
        dc.DrawGeometry(null, trackPen, trackGeo);

        if (IsIndeterminate)
        {
            var baseColor = ((SolidColorBrush)ForegroundColor).Color;
            // 三段渐隐拖尾，头部最亮
            DrawCometSegment(dc, cx, cy, radius, baseColor, 0.25, 0, 26);
            DrawCometSegment(dc, cx, cy, radius, baseColor, 0.55, 24, 30);
            DrawCometSegment(dc, cx, cy, radius, baseColor, 1.0, 52, 34);
            return;
        }

        var valSweep = sweep * (AnimatedValue / 100.0);
        if (valSweep > 0.5)
        {
            var valuePen = new Pen(ForegroundColor, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var valGeo = BuildArc(cx, cy, radius, StartAngle, StartAngle + valSweep);
            dc.DrawGeometry(null, valuePen, valGeo);
        }
    }

    private void DrawCometSegment(DrawingContext dc, double cx, double cy, double radius, Color baseColor, double opacity, double fromOffset, double length)
    {
        var color = Color.FromArgb((byte)(255 * opacity), baseColor.R, baseColor.G, baseColor.B);
        var pen = new Pen(new SolidColorBrush(color), Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        var spin = (double)GetValue(SpinAngleProperty);
        var geo = BuildArc(cx, cy, radius, spin + fromOffset, spin + fromOffset + length);
        dc.DrawGeometry(null, pen, geo);
    }

    private static Geometry BuildArc(double cx, double cy, double radius, double fromAngle, double toAngle)
    {
        var span = toAngle - fromAngle;
        // 完整圆（起点终点重合）用两段半圆弧绘制，避免 ArcSegment 退化
        if (Math.Abs(span) >= 359.9)
        {
            var mid = fromAngle + 180;
            var p1 = ToPoint(cx, cy, radius, fromAngle);
            var p2 = ToPoint(cx, cy, radius, mid);
            var dir = span >= 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
            var fig = new PathFigure
            {
                StartPoint = p1,
                IsClosed = false,
                Segments =
                {
                    new ArcSegment(p2, new Size(radius, radius), 0, false, dir, true),
                    new ArcSegment(p1, new Size(radius, radius), 0, false, dir, true)
                }
            };
            return new PathGeometry(new[] { fig });
        }

        var start = ToPoint(cx, cy, radius, fromAngle);
        var end = ToPoint(cx, cy, radius, toAngle);
        var large = Math.Abs(span) > 180;
        var d = span >= 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false,
            Segments =
            {
                new ArcSegment(end, new Size(radius, radius), 0, large, d, true)
            }
        };
        return new PathGeometry(new[] { figure });
    }

    private static Point ToPoint(double cx, double cy, double radius, double angle)
    {
        var rad = (angle - 90) * Math.PI / 180;
        return new Point(cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad));
    }
}
