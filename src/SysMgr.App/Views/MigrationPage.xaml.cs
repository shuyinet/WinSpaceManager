using SysMgr.App.ViewModels;

namespace SysMgr.App.Views;

/// <summary>大文件搬家页（FR-MIGRATE-01）。</summary>
public partial class MigrationPage : System.Windows.Controls.Page
{
    public MigrationPage(SysMgrServices services)
    {
        InitializeComponent();
        ViewModel = new MigrationViewModel(services);
        DataContext = ViewModel;
    }

    public MigrationViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, System.Windows.RoutedEventArgs e)
        => await ViewModel.EnsureLoadedAsync();

    private void ChooseDestination_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择搬家的目标文件夹",
            InitialDirectory = ViewModel.DestinationPath,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            ViewModel.DestinationPath = dialog.FolderName;
        }
    }
}
