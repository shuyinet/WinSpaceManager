using System.Windows;
using System.Windows.Controls;

namespace SysMgr.App.Views;

/// <summary>设置页：加载偏好，承载 <see cref="ViewModels.SettingsViewModel"/>。</summary>
public partial class SettingsPage : Page
{
    private readonly ViewModels.SettingsViewModel _viewModel;

    public SettingsPage(SysMgrServices services)
    {
        InitializeComponent();
        _viewModel = new ViewModels.SettingsViewModel(services);
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.LoadAsync();
    }
}
