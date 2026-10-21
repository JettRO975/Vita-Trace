using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VitaTrace.App.Controls;

/// <summary>
/// 电量圆环。用 OnRender 直接绘制，避免引入图表库：
/// - 圆环从 -90° 顺时针填充；
/// - 数值变化时做 550ms 缓动，而不是瞬间跳变；
/// - 鼠标悬停时整体轻微放大，提供反馈但不影响布局。
///
/// 文本由外部传入（已本地化），控件本身不判断“不支持/失败”等业务状态。
/// </summary>
public sealed class BatteryRing : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(BatteryRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnPercentChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(BatteryRing),
        new FrameworkPropertyMetadata("—", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(BatteryRing),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(
        nameof(ProgressBrush), typeof(Brush), typeof(BatteryRing),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(BatteryRing),
        new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(BatteryRing),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionBrushProperty = DependencyProperty.Register(
        nameof(CaptionBrush), typeof(Brush), typeof(BatteryRing),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(BatteryRing),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty AnimatedPercentProperty = DependencyProperty.Register(
        nameof(AnimatedPercent), typeof(double), typeof(BatteryRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly ScaleTransform _scale = new(1, 1);

    public BatteryRing()
    {
        RenderTransform = _scale;
        RenderTransformOrigin = new Point(0.5, 0.5);
        MouseEnter += (_, _) => AnimateScale(1.035);
        MouseLeave += (_, _) => AnimateScale(1.0);
    }

    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public Brush ProgressBrush
    {
        get => (Brush)GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public Brush CaptionBrush
    {
        get => (Brush)GetValue(CaptionBrushProperty);
        set => SetValue(CaptionBrushProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    private double AnimatedPercent
    {
        get => (double)GetValue(AnimatedPercentProperty);
        set => SetValue(AnimatedPercentProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 12)
        {
            return;
        }

        var thickness = Math.Clamp(Thickness, 4, size / 4);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(1, size / 2 - thickness / 2 - 2);

        var trackPen = new Pen(TrackBrush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        trackPen.Freeze();
        drawingContext.DrawEllipse(null, trackPen, center, radius, radius);

        var percent = Math.Clamp(AnimatedPercent, 0, 100);
        if (percent > 0.05)
        {
            var progressPen = new Pen(ProgressBrush, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            progressPen.Freeze();

            if (percent >= 99.9)
            {
                drawingContext.DrawEllipse(null, progressPen, center, radius, radius);
            }
            else
            {
                var startAngle = -90d;
                var endAngle = startAngle + 360d * percent / 100d;
                var startPoint = PointOnCircle(center, radius, startAngle);
                var endPoint = PointOnCircle(center, radius, endAngle);

                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(startPoint, isFilled: false, isClosed: false);
                    context.ArcTo(
                        endPoint,
                        new Size(radius, radius),
                        rotationAngle: 0,
                        isLargeArc: percent > 50,
                        SweepDirection.Clockwise,
                        isStroked: true,
                        isSmoothJoin: false);
                }

                geometry.Freeze();
                drawingContext.DrawGeometry(null, progressPen, geometry);
            }
        }

        DrawLabels(drawingContext, center, size);
    }

    private void DrawLabels(DrawingContext drawingContext, Point center, double size)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        var valueText = new FormattedText(
            string.IsNullOrWhiteSpace(Text) ? "—" : Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            Math.Max(20, size * 0.22),
            TextBrush ?? Brushes.Black,
            pixelsPerDip);

        drawingContext.DrawText(valueText, new Point(
            center.X - valueText.Width / 2,
            center.Y - valueText.Height / 2 - size * 0.015));

        if (string.IsNullOrWhiteSpace(Caption))
        {
            return;
        }

        var captionText = new FormattedText(
            Caption,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            Math.Max(10, size * 0.072),
            CaptionBrush ?? Brushes.Gray,
            pixelsPerDip);

        drawingContext.DrawText(captionText, new Point(
            center.X - captionText.Width / 2,
            center.Y + valueText.Height / 2 - size * 0.03));
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Point(
            center.X + radius * Math.Cos(radians),
            center.Y + radius * Math.Sin(radians));
    }

    private static void OnPercentChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var ring = (BatteryRing)dependencyObject;
        var target = Math.Clamp((double)args.NewValue, 0, 100);
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(550),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ring.BeginAnimation(AnimatedPercentProperty, animation);
    }

    private void AnimateScale(double target)
    {
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
}
