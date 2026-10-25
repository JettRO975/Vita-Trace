using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VitaTrace.App.Services;
using VitaTrace.App.ViewModels;

namespace VitaTrace.App.Views;

/// <summary>
/// 嵌在主窗口里的设置页。设置窗口不再单独弹出，这样主题切换的圆形遮罩
/// 只覆盖一个窗口，过渡更自然；取消/离开页面时由这里负责把预览主题回滚。
/// </summary>
public partial class SettingsView : UserControl
{
    private SettingsViewModel? _viewModel;
    private bool _ready;
    private bool _saved;
    private string _originalTheme = "sea";
    private string _currentTheme = "sea";
    private string _previousTheme = "sea";

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
    }

    /// <summary>进入设置页：绑定新草稿，并记录进入时的主题。</summary>
    public void Activate(SettingsViewModel viewModel)
    {
        _ready = false;
        _saved = false;
        DataContext = viewModel;
        HookViewModel();

        _originalTheme = viewModel.Theme;
        _currentTheme = _originalTheme;
        _previousTheme = _originalTheme;
        _ready = true;
    }

    /// <summary>离开设置页：未保存的预览主题回滚到进入前的值。</summary>
    public void Deactivate()
    {
        if (_ready && !_saved && _currentTheme != _originalTheme && Application.Current is App app)
        {
            app.ApplyThemeNow(_originalTheme);
        }

        _ready = false;
        if (_viewModel is not null)
        {
            _viewModel.Saved -= OnSaved;
        }

        _viewModel = null;
        DataContext = null;
    }

    private void HookViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.Saved -= OnSaved;
        }

        _viewModel = DataContext as SettingsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Saved += OnSaved;
        }
    }

    private void OnSaved(object? sender, EventArgs e) => _saved = true;

    /// <summary>
    /// 点击外观胶囊时立即预览；点回上一次选中的外观时圆形遮罩收缩，
    /// 其余情况从点击位置向外扩散。
    /// </summary>
    private void AppearanceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready ||
            AppearanceList.SelectedItem is not AppearanceOption option ||
            option.Value == _currentTheme)
        {
            return;
        }

        if (AppearanceList.ItemContainerGenerator.ContainerFromItem(option) is not FrameworkElement container)
        {
            return;
        }

        var center = container.TranslatePoint(
            new Point(container.ActualWidth / 2, container.ActualHeight / 2),
            this);
        var screenPoint = PointToScreen(center);
        var reverse = option.Value == _previousTheme;

        if (Application.Current is App app)
        {
            app.PreviewTheme(option.Value, screenPoint, reverse);
        }

        _previousTheme = _currentTheme;
        _currentTheme = option.Value;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel is not null)
        {
            _viewModel.CancelCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }
}
