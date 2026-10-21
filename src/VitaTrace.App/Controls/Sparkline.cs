using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VitaTrace.Core.Models;

namespace VitaTrace.App.Controls;

/// <summary>
/// 轻量电量曲线。特性：
/// - 固定 0–100 纵轴，不同设备的曲线可以直接比较；
/// - Percent 为 null 时断开线段，不把“读取失败”画成 0%；
/// - 鼠标悬停显示采样时间与电量，并带淡入与放大反馈；
/// - 不依赖第三方图表库，避免额外体积与主题适配问题。
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IEnumerable<BatterySample>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPointsChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty HoverOpacityProperty = DependencyProperty.Register(
        nameof(HoverOpacity), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private IReadOnlyList<BatterySample> _samples = Array.Empty<BatterySample>();
    private int _hoverIndex = -1;

    public Sparkline()
    {
        IsHitTestVisible = true;
        MouseMove += OnMouseMove;
        MouseEnter += (_, _) => AnimateHover(1);
        MouseLeave += (_, _) =>
        {
            _hoverIndex = -1;
            AnimateHover(0);
            InvalidateVisual();
        };
    }

    public IEnumerable<BatterySample>? Points
    {
        get => (IEnumerable<BatterySample>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    private double HoverOpacity
    {
        get => (double)GetValue(HoverOpacityProperty);
        set => SetValue(HoverOpacityProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (ActualWidth < 40 || ActualHeight < 40 || _samples.Count == 0)
        {
            return;
        }

        var plot = GetPlotRect();
        DrawGrid(drawingContext, plot);

        if (_samples.Count == 1)
        {
            var point = new Point(plot.Left + plot.Width / 2, ValueToY(_samples[0].Percent, plot));
            DrawDot(drawingContext, point, 4.5);
            return;
        }

        var linePen = new Pen(Stroke, 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        linePen.Freeze();

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var figureOpen = false;
            for (var i = 0; i < _samples.Count; i++)
            {
                var sample = _samples[i];
                var x = XForIndex(i, plot);

                if (sample.Percent is null)
                {
                    figureOpen = false;
                    continue;
                }

                var point = new Point(x, ValueToY(sample.Percent, plot));
                if (!figureOpen)
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                    figureOpen = true;
                }
                else
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: true);
                }
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, linePen, geometry);

        // 只有数据连续时才画面积，断点处不伪造“补间”。
        if (_samples.All(s => s.Percent is not null))
        {
            DrawArea(drawingContext, plot, linePen);
        }

        var last = _samples.LastOrDefault(s => s.Percent is not null);
        if (last != default)
        {
            var index = _samples.ToList().FindLastIndex(s => s.Percent is not null);
            var lastPoint = new Point(XForIndex(index, plot), ValueToY(last.Percent, plot));
            DrawDot(drawingContext, lastPoint, 4);
        }

        DrawHover(drawingContext, plot);
    }

    private void DrawGrid(DrawingContext drawingContext, Rect plot)
    {
        var gridPen = new Pen(GridBrush, 1)
        {
            DashStyle = new DashStyle(new double[] { 3, 4 }, 0)
        };
        gridPen.Freeze();

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var labelTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        foreach (var value in new[] { 0, 25, 50, 75, 100 })
        {
            var y = ValueToY(value, plot);
            drawingContext.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));

            var label = new FormattedText(
                value.ToString(CultureInfo.InvariantCulture),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                labelTypeface,
                9,
                TextBrush,
                pixelsPerDip);
            drawingContext.DrawText(label, new Point(plot.Left - label.Width - 6, y - label.Height / 2));
        }
    }

    private void DrawArea(DrawingContext drawingContext, Rect plot, Pen linePen)
    {
        var color = (Stroke as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        var fill = new LinearGradientBrush(
            Color.FromArgb(55, color.R, color.G, color.B),
            Color.FromArgb(0, color.R, color.G, color.B),
            new Point(0, 0),
            new Point(0, 1));
        fill.Freeze();

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(XForIndex(0, plot), plot.Bottom), isFilled: true, isClosed: true);
            for (var i = 0; i < _samples.Count; i++)
            {
                context.LineTo(new Point(XForIndex(i, plot), ValueToY(_samples[i].Percent, plot)), true, true);
            }

            context.LineTo(new Point(XForIndex(_samples.Count - 1, plot), plot.Bottom), true, true);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(fill, null, geometry);
    }

    private void DrawDot(DrawingContext drawingContext, Point point, double radius)
    {
        drawingContext.DrawEllipse(Brushes.White, null, point, radius + 1.5, radius + 1.5);
        drawingContext.DrawEllipse(Stroke, null, point, radius, radius);
    }

    private void DrawHover(DrawingContext drawingContext, Rect plot)
    {
        if (_hoverIndex < 0 || _hoverIndex >= _samples.Count || HoverOpacity <= 0.01)
        {
            return;
        }

        var sample = _samples[_hoverIndex];
        var x = XForIndex(_hoverIndex, plot);
        var y = ValueToY(sample.Percent, plot);

        drawingContext.PushOpacity(HoverOpacity);

        var guidePen = new Pen(TextBrush, 1)
        {
            DashStyle = new DashStyle(new double[] { 2, 3 }, 0)
        };
        guidePen.Freeze();
        drawingContext.DrawLine(guidePen, new Point(x, plot.Top), new Point(x, plot.Bottom));

        // 悬停点外圈随透明度放大，形成轻微的“呼吸”反馈。
        var haloRadius = 7 + 4 * HoverOpacity;
        drawingContext.DrawEllipse(
            new SolidColorBrush(Color.FromArgb((byte)(45 * HoverOpacity), 47, 107, 255)),
            null,
            new Point(x, y),
            haloRadius,
            haloRadius);
        drawingContext.DrawEllipse(Stroke, null, new Point(x, y), 4.5, 4.5);

        var percentText = sample.Percent is null ? "—" : $"{sample.Percent}%";
        var text = $"{sample.Timestamp.LocalDateTime:HH:mm}  {percentText}";
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            11,
            Brushes.White,
            pixelsPerDip);

        var boxWidth = formatted.Width + 16;
        var boxHeight = formatted.Height + 10;
        var boxLeft = Math.Clamp(x - boxWidth / 2, 2, ActualWidth - boxWidth - 2);
        var boxTop = Math.Max(2, y - boxHeight - 14);

        var boxBrush = new SolidColorBrush(Color.FromArgb(225, 27, 30, 37));
        boxBrush.Freeze();
        drawingContext.DrawRoundedRectangle(boxBrush, null, new Rect(boxLeft, boxTop, boxWidth, boxHeight), 6, 6);
        drawingContext.DrawText(formatted, new Point(boxLeft + 8, boxTop + 4));

        drawingContext.Pop();
    }

    private Rect GetPlotRect()
    {
        // 左侧留给 0/50/100 标签，底部留给时间标签。
        return new Rect(28, 8, Math.Max(1, ActualWidth - 36), Math.Max(1, ActualHeight - 34));
    }

    private double XForIndex(int index, Rect plot)
    {
        return _samples.Count <= 1
            ? plot.Left + plot.Width / 2
            : plot.Left + plot.Width * index / (_samples.Count - 1);
    }

    private static double ValueToY(int? percent, Rect plot)
    {
        var value = Math.Clamp(percent ?? 0, 0, 100);
        return plot.Bottom - plot.Height * value / 100d;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_samples.Count == 0)
        {
            return;
        }

        var plot = GetPlotRect();
        var relative = (e.GetPosition(this).X - plot.Left) / Math.Max(1, plot.Width);
        var index = (int)Math.Round(relative * (_samples.Count - 1));
        index = Math.Clamp(index, 0, _samples.Count - 1);

        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            InvalidateVisual();
        }
    }

    private void AnimateHover(double target)
    {
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(HoverOpacityProperty, animation);
    }

    private static void OnPointsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var sparkline = (Sparkline)dependencyObject;

        if (args.OldValue is INotifyCollectionChanged oldCollection)
        {
            oldCollection.CollectionChanged -= sparkline.OnCollectionChanged;
        }

        if (args.NewValue is INotifyCollectionChanged newCollection)
        {
            newCollection.CollectionChanged += sparkline.OnCollectionChanged;
        }

        sparkline._samples = (args.NewValue as IEnumerable<BatterySample>)?.ToList()
                             ?? new List<BatterySample>();
        sparkline._hoverIndex = -1;
        sparkline.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _samples = Points?.ToList() ?? new List<BatterySample>();
        if (_hoverIndex >= _samples.Count)
        {
            _hoverIndex = -1;
        }

        InvalidateVisual();
    }
}
