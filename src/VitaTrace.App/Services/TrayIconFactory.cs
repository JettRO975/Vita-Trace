using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
// WPF 的全局 using 里有 System.Windows.Media，Color/Brushes/FontStyle/PixelFormat 会撞名；
// 托盘图标明确用 System.Drawing 的版本，所以这里用别名消歧。
using Color = System.Drawing.Color;
using Brushes = System.Drawing.Brushes;
using FontStyle = System.Drawing.FontStyle;
using Pen = System.Drawing.Pen;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace VitaTrace.App.Services;

/// <summary>
/// 托盘图标在运行时绘制，避免为两个状态（正常/低电量）维护外部 ico 资源，
/// 也保证高 DPI 下边缘清晰。图标语义：蓝色圆 + 闪电 = 电量；红色圆 + ! = 低电量。
/// </summary>
public static class TrayIconFactory
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// 生成“编号 + 电量圆环”的托盘图标。
    /// 圆圈闭合的弧度就是电量：0% 只有灰色底环，100% 是完整圆环；
    /// 20% 及以上绿色，20% 以下红色。中间留空，只放设备序号。
    /// 只由 MainViewModel 已确认可读的设备调用，读不到电量的设备不会出现在托盘里。
    /// </summary>
    public static Icon CreateDevice(int number, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);

        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            var color = percent >= 20
                ? Color.FromArgb(31, 181, 122)
                : Color.FromArgb(229, 72, 77);

            var ring = new RectangleF(3f, 3f, 26f, 26f);
            using var trackPen = new Pen(Color.FromArgb(105, 128, 135, 150), 3.6f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawEllipse(trackPen, ring);

            if (percent > 0)
            {
                using var progressPen = new Pen(color, 3.6f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };

                if (percent >= 100)
                {
                    graphics.DrawEllipse(progressPen, ring);
                }
                else
                {
                    graphics.DrawArc(progressPen, ring, -90f, 360f * percent / 100f);
                }
            }

            var text = number.ToString();
            var size = text.Length switch
            {
                1 => 13f,
                2 => 11f,
                _ => 8.5f
            };

            using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            using var textBrush = new SolidBrush(color);
            graphics.DrawString(text, font, textBrush, new RectangleF(3f, 3f, 26f, 26f), format);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    /// <summary>
    /// ^ 溢出区的应用小图标：直接使用与 exe 相同的透明 ICO。
    /// 不再运行时画“V”，保证任务栏、标题栏、托盘三处图案一致。
    /// </summary>
    public static Icon CreateApp()
    {
        var uri = new Uri("pack://application:,,,/Assets/VitaTrace.ico", UriKind.Absolute);
        var resource = System.Windows.Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException("找不到托盘图标资源 Assets/VitaTrace.ico");
        using var stream = resource.Stream;
        return new Icon(stream, 32, 32);
    }

    public static Icon Create(bool alert)
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var background = alert
                ? Color.FromArgb(229, 72, 77)
                : Color.FromArgb(47, 107, 255);

            using var brush = new SolidBrush(background);
            graphics.FillEllipse(brush, 1f, 1f, 30f, 30f);

            if (alert)
            {
                using var font = new Font("Segoe UI", 18f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                graphics.DrawString("!", font, Brushes.White, new RectangleF(0, 0, 32, 32), format);
            }
            else
            {
                var bolt = new[]
                {
                    new PointF(18f, 5f),
                    new PointF(10f, 18f),
                    new PointF(15.5f, 18f),
                    new PointF(13f, 27f),
                    new PointF(22f, 14f),
                    new PointF(16.5f, 14f)
                };
                graphics.FillPolygon(Brushes.White, bolt);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }
}
