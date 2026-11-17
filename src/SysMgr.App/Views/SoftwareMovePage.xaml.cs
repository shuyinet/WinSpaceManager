using SysMgr.App.ViewModels;

namespace SysMgr.App.Views;

/// <summary>软件搬家页：勾选已装软件整体搬到其他盘，原目录保留目录联接。</summary>
public partial class SoftwareMovePage : System.Windows.Controls.Page
{
    public SoftwareMovePage(SysMgrServices services)
    {
        InitializeComponent();
        ViewModel = new SoftwareMoveViewModel(services);
        DataContext = ViewModel;
    }

    public SoftwareMoveViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, System.Windows.RoutedEventArgs e)
        => await ViewModel.EnsureLoadedAsync();

    private void ChooseDestination_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择软件搬家的目标文件夹",
            InitialDirectory = ViewModel.DestinationPath,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            ViewModel.DestinationPath = dialog.FolderName;
        }
    }
}
