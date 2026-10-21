using System.Windows.Media;

namespace VitaTrace.App.Services;

/// <summary>
/// 电量颜色。故意使用固定色而不是主题色：红/黄/绿在浅色和深色下都要保持可读，
/// 且电量含义不能被主题改变。
/// </summary>
public static class BatteryPalette
{
    public static Brush Good { get; } = Freeze(Color.FromRgb(0x1F, 0xB5, 0x7A));
    public static Brush Medium { get; } = Freeze(Color.FromRgb(0xE0, 0x9A, 0x16));
    public static Brush Low { get; } = Freeze(Color.FromRgb(0xE5, 0x48, 0x4D));
    public static Brush Unknown { get; } = Freeze(Color.FromRgb(0x8A, 0x92, 0xA6));
    public static Brush Wired { get; } = Freeze(Color.FromRgb(0x2F, 0x6B, 0xFF));

    public static Brush For(double percent) => percent >= 50 ? Good : percent >= 20 ? Medium : Low;

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
