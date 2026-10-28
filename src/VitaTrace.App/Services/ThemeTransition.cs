using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace VitaTrace.App.Services;

/// <summary>
/// 主题切换的圆形扩散/收缩过渡。
///
/// 做法：切换前把当前窗口内容截成一张静态图，盖在真实内容上方；
/// 真实内容先切到新主题，再让遮罩上的“洞”从点击位置向外扩散，
/// 露出下面的新主题。这样过渡只发生在 WPF 视觉层，不阻塞 UI 线程。
/// </summary>
public static class ThemeTransition
{
    private static readonly Dictionary<Window, ActiveOverlay> Active = new();

    public static void Play(IReadOnlyList<Window> windows, Point screenOrigin, Action applyTheme)
    {
        var snapshots = new List<Snapshot>();
        foreach (var window in windows)
        {
            try
            {
                var snapshot = Snapshot.Capture(window, screenOrigin);
                if (snapshot is not null)
                {
                    snapshots.Add(snapshot);
                }
            }
            catch
            {
                // 窗口尚未渲染/最小化时截图可能失败；此时直接切主题，不让动画阻断功能。
            }
        }

        applyTheme();

        foreach (var snapshot in snapshots)
        {
            snapshot.Show();
        }
    }

    private sealed record Snapshot(
        Window Window,
        Panel Host,
        BitmapSource Image,
        Point Origin,
        double MaxRadius)
    {
        public static Snapshot? Capture(Window window, Point screenOrigin)
        {
            if (window.Content is not Panel host ||
                host.ActualWidth < 2 ||
                host.ActualHeight < 2)
            {
                return null;
            }

            var dpi = VisualTreeHelper.GetDpi(window);
            var width = Math.Max(1, (int)Math.Ceiling(host.ActualWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)Math.Ceiling(host.ActualHeight * dpi.DpiScaleY));

            // 用窗口实际 DPI 渲染，否则 150% 缩放时截图只占画布左上角 2/3，
            // 过渡会看起来像“旧窗口缩小了一圈”。
            var bitmap = new RenderTargetBitmap(
                width,
                height,
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            bitmap.Render(host);
            bitmap.Freeze();

            var origin = host.PointFromScreen(screenOrigin);
            var maxRadius = Math.Max(
                Math.Max(Distance(origin, new Point(0, 0)), Distance(origin, new Point(host.ActualWidth, 0))),
                Math.Max(Distance(origin, new Point(0, host.ActualHeight)), Distance(origin, new Point(host.ActualWidth, host.ActualHeight))));

            // 终点必须明显越过最远角：如果刚好等于角距离，遮罩最外圈仍是旧主题，
            // 动画结束移除遮罩时右下角会“啪”地跳一下。多留 25% 半径让最远角提前完成。
            return new Snapshot(window, host, bitmap, origin, maxRadius * 1.25 + 24);
        }

        public void Show()
        {
            if (Active.TryGetValue(Window, out var prior))
            {
                prior.Remove();
                Active.Remove(Window);
            }

            var overlay = new Grid
            {
                IsHitTestVisible = false,
                ClipToBounds = true,
                Background = Brushes.Transparent,
                Width = Host.ActualWidth,
                Height = Host.ActualHeight
            };
            Panel.SetZIndex(overlay, 10000);

            if (Host is Grid grid)
            {
                Grid.SetRowSpan(overlay, Math.Max(1, grid.RowDefinitions.Count));
                Grid.SetColumnSpan(overlay, Math.Max(1, grid.ColumnDefinitions.Count));
            }

            overlay.Children.Add(new Image
            {
                Source = Image,
                Stretch = Stretch.Fill,
                Width = Host.ActualWidth,
                Height = Host.ActualHeight
            });

            Host.Children.Add(overlay);

            // 用径向渐变透明度遮罩：中心透明 => 露出下面的新主题；
            // 外圈不透明 => 旧主题仍然覆盖；中心透明处露出新主题。
            var mask = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = Origin,
                GradientOrigin = Origin,
                RadiusX = 0.1,
                RadiusY = 0.1
            };

            // 正向：洞的中心先透明，边缘留 15% 羽化，扩散时不会像硬边圆片。
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0.85));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 1));

            overlay.OpacityMask = mask;

            // 正向 900ms；不再提供反向收缩，切换回去也从点击位置向外扩散。
            var duration = TimeSpan.FromMilliseconds(900);
            var animation = new DoubleAnimation(
                0.1,
                MaxRadius,
                new Duration(duration))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop
            };

            animation.Completed += (_, _) =>
            {
                try
                {
                    Host.Children.Remove(overlay);
                }
                catch
                {
                    // 窗口已关闭或 Host 被替换。
                }

                if (Active.TryGetValue(Window, out var current) && ReferenceEquals(current.Overlay, overlay))
                {
                    Active.Remove(Window);
                }
            };

            mask.BeginAnimation(RadialGradientBrush.RadiusXProperty, animation);
            mask.BeginAnimation(RadialGradientBrush.RadiusYProperty, animation.Clone());

            Active[Window] = new ActiveOverlay(Host, overlay);
        }

        private static double Distance(Point a, Point b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    private sealed record ActiveOverlay(Panel Host, UIElement Overlay)
    {
        public void Remove()
        {
            try
            {
                Host.Children.Remove(Overlay);
            }
            catch
            {
                // 窗口已关闭。
            }
        }
    }
}
