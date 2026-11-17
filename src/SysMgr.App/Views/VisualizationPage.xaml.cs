using System.Windows.Controls;
using SysMgr.App.ViewModels;
using SysMgr.App.ViewModels.Navigation;
using SysMgr.Modules.Visualization;

namespace SysMgr.App.Views;

/// <summary>磁盘可视化页（FR-VIS-01/02/03）。Treemap 事件与右键菜单在此接线。</summary>
public partial class VisualizationPage : Page
{
    public VisualizationPage(SysMgrServices services)
    {
        InitializeComponent();
        ViewModel = new VisualizationViewModel(services);
        DataContext = ViewModel;

        Treemap.DrillDownRequested += ViewModel.DrillInto;
        Treemap.OpenRequested += ViewModel.OpenInExplorer;
        Treemap.NavigateUpRequested += ViewModel.GoBack;
        Treemap.NodeContextMenuRequested += ShowNodeMenu;

        // 首次进入该功能页且尚未扫描 → 自动分析当前磁盘
        Loaded += (_, _) =>
        {
            if (ViewModel.ScanCommand.CanExecute(null) && AutoScanGate.TryClaim("visualization"))
            {
                ViewModel.ScanCommand.Execute(null);
            }
        };
    }

    public VisualizationViewModel ViewModel { get; }

    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Treemap.SelectedNode is null)
        {
            return;
        }
        if (Treemap.SelectedNode.IsDirectory)
        {
            ViewModel.DrillInto(Treemap.SelectedNode);
        }
        else
        {
            ViewModel.OpenInExplorer(Treemap.SelectedNode);
        }
    }

    private void ShowNodeMenu(TreemapNode node)
    {
        var menu = new ContextMenu();

        if (node.IsDirectory && node.Children.Count > 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "下钻进入",
                Command = ViewModel.DrillCommand,
                CommandParameter = node
            });
        }
        menu.Items.Add(new MenuItem
        {
            Header = "在资源管理器中打开",
            Command = ViewModel.OpenInExplorerCommand,
            CommandParameter = node
        });

        if (!node.IsOtherAggregate)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem
            {
                Header = ViewModel.IsTagged(node) ? "移除标签「待处理」" : "标记「待处理」",
                Command = ViewModel.ToggleTagCommand,
                CommandParameter = node
            });
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem
            {
                Header = "迁移到目标目录",
                Command = ViewModel.MigrateCommand,
                CommandParameter = node
            });
            menu.Items.Add(new MenuItem
            {
                Header = "删除（可回滚）",
                Command = ViewModel.DeleteCommand,
                CommandParameter = node
            });
        }

        Treemap.ContextMenu = menu;
        menu.IsOpen = true;
    }
}
