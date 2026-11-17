using System.Windows;
using System.Windows.Input;

namespace SysMgr.App.Views;

/// <summary>
/// 通用确认弹窗（深色科技风）：用于卸载失败、进程占用等需要用户明确决策的场景。
/// 主按钮文案由调用方指定（如「强制卸载」），避免「是/否」这类含糊确认。
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string primaryText, string secondaryText = "取消")
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryBtn.Content = primaryText;
        SecondaryBtn.Content = secondaryText;
    }

    /// <summary>用户是否点击了主按钮。</summary>
    public bool Confirmed { get; private set; }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
