using System.Windows;
using SysMgr.App.ViewModels;

namespace SysMgr.App.Views;

/// <summary>文件粉碎页（FR-SHRED-01/02）。</summary>
public partial class ShredPage : System.Windows.Controls.Page
{
    public ShredPage(SysMgrServices services)
    {
        InitializeComponent();
        ViewModel = new ShredViewModel(services);
        DataContext = ViewModel;
    }

    public ShredViewModel ViewModel { get; }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "选择要粉碎的文件"
        };
        if (dialog.ShowDialog() == true)
        {
            await ViewModel.AddPathsAsync(dialog.FileNames);
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要粉碎的文件夹（将递归粉碎全部内容）"
        };
        if (dialog.ShowDialog() == true)
        {
            await ViewModel.AddPathsAsync(new[] { dialog.FolderName });
        }
    }

    private void Page_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void Page_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
        {
            await ViewModel.AddPathsAsync(paths);
        }
    }
}
