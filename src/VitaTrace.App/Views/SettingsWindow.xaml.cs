using System.Windows;
using System.Windows.Input;
using VitaTrace.App.Services;

namespace VitaTrace.App.Views;

/// <summary>
/// 设置窗口。窗口本身只处理 Escape 关闭和标题栏外观；
/// 所有设置项的读写与校验都在 SettingsViewModel 中完成。
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        // 设置窗口和主窗口共用主题；这里只需要补一次 DWM 深色边框/圆角。
        SourceInitialized += (_, _) =>
            WindowEffects.Apply(this, ((App)Application.Current).Theme.IsDark);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }
}
