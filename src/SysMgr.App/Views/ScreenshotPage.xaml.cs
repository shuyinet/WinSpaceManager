using System.Windows;
using SysMgr.App.ViewModels;

namespace SysMgr.App.Views;

/// <summary>截图页：三种截图方式入口、设置与最近截图缩略图墙。</summary>
public partial class ScreenshotPage : System.Windows.Controls.Page
{
    public ScreenshotPage()
    {
        InitializeComponent();

        var service = ((App)Application.Current).Screenshots
            ?? throw new InvalidOperationException("截图服务未初始化");

        ViewModel = new ScreenshotViewModel(service);
        DataContext = ViewModel;
    }

    public ScreenshotViewModel ViewModel { get; }

    private void Page_Loaded(object sender, RoutedEventArgs e) => ViewModel.Attach();

    private void Page_Unloaded(object sender, RoutedEventArgs e) => ViewModel.Detach();

    private void ChooseDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择截图保存目录",
            InitialDirectory = ViewModel.SaveDirectory,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            _ = ViewModel.ChangeSaveDirectoryAsync(dialog.FolderName);
        }
    }
}
