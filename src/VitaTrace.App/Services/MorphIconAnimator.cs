using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace VitaTrace.App.Services;

/// <summary>
/// 轻量的 Morphicons 交互移植（原库为 MIT，https://github.com/guillermolg00/morphicons）。
///
/// 原库的核心是“路径插值 + 弹簧物理”；完整移植 TS 求解器成本很高，这里保留
/// 对应用最有价值的部分：所有图标按钮点击时用弹簧曲线做压缩/回弹，刷新按钮
/// 额外转一圈。只做视觉反馈，不改变任何业务状态。
/// </summary>
public static class MorphIconAnimator
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        EventManager.RegisterClassHandler(
            typeof(Button),
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnButtonPressed));
    }

    private static void OnButtonPressed(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button button || !button.IsEnabled)
        {
            return;
        }

        var icon = FindIcon(button);
        if (icon is null)
        {
            return;
        }

        Bounce(icon);

        // 刷新按钮：点击后转一圈，强调“正在刷新”。
        // 用 Tag 而不是按钮名字，避免把设置/关闭也误旋转。
        if (string.Equals(button.Tag as string, "Refresh", StringComparison.Ordinal))
        {
            SpinOnce(icon);
        }
    }

    private static Path? FindIcon(DependencyObject root)
    {
        if (root is Path path)
        {
            return path;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindIcon(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static void Bounce(Path icon)
    {
        icon.RenderTransformOrigin = new Point(0.5, 0.5);

        if (icon.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            icon.RenderTransform = scale;
        }

        var duration = TimeSpan.FromMilliseconds(360);
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = duration,
        };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            0.78,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)),
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360)),
            new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 3.2 }));

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void SpinOnce(Path icon)
    {
        var transform = icon.RenderTransform as TransformGroup;
        if (transform is null)
        {
            transform = new TransformGroup();
            if (icon.RenderTransform is not null && !icon.RenderTransform.Value.IsIdentity)
            {
                transform.Children.Add(icon.RenderTransform);
            }

            transform.Children.Add(new RotateTransform(0, 8, 8));
            icon.RenderTransform = transform;
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        var rotate = transform.Children.OfType<RotateTransform>().FirstOrDefault();
        if (rotate is null)
        {
            rotate = new RotateTransform(0, 8, 8);
            transform.Children.Add(rotate);
        }

        var animation = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        rotate.BeginAnimation(RotateTransform.AngleProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
