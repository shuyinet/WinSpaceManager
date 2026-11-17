using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SysMgr.Core.Operations;
using SysMgr.Core.Safety;
using SysMgr.Core.Trust;

namespace SysMgr.App.Views;

/// <summary>
/// TRUST-02 深色科技风预演对话框。逐项展示动作/目标/影响/大小，
/// Low 等级需勾选风险确认；用户确认后返回 <see cref="PreviewResult"/>。
/// </summary>
public partial class PreviewDialog : Window
{
    private readonly OperationPlan _plan;
    private const string NotePlaceholder = "备注（可选，仅记录到操作历史）";

    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x62));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xFF, 0xB2, 0x3E));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x18, 0xC0, 0xFF));
    private static readonly Brush Purple = new SolidColorBrush(Color.FromRgb(0x9D, 0x7C, 0xFF));

    /// <summary>对话框结论；关闭后由调用方读取。</summary>
    public PreviewResult? Result { get; private set; }

    public PreviewDialog(OperationPlan plan)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        InitializeComponent();

        DescriptionText.Text = plan.Description;
        ModuleText.Text = plan.ModuleId;
        FrText.Text = plan.FrId;
        ConfigureSafety(plan.SafetyLevel);

        var rows = plan.Actions.Select((a, i) => new PreviewRow(i + 1, a)).ToList();
        ActionGrid.ItemsSource = rows;

        NoteBox.GotFocus += (_, _) =>
        {
            if (NoteBox.Text == NotePlaceholder)
            {
                NoteBox.Text = string.Empty;
            }
        };
        NoteBox.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(NoteBox.Text))
            {
                NoteBox.Text = NotePlaceholder;
            }
        };
    }

    private void ConfigureSafety(SafetyLevel level)
    {
        SafetyText.Text = level switch
        {
            SafetyLevel.High => "安全等级 High",
            SafetyLevel.Medium => "安全等级 Medium",
            SafetyLevel.Low => "安全等级 Low",
            _ => level.ToString()
        };

        var (bg, fg) = level switch
        {
            SafetyLevel.High => ("#1A23E3A0", new SolidColorBrush(Color.FromRgb(0x23, 0xE3, 0xA0))),
            SafetyLevel.Medium => ("#1AFFB23E", new SolidColorBrush(Color.FromRgb(0xFF, 0xB2, 0x3E))),
            _ => ("#1AFF4D62", Danger)
        };
        SafetyBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
        SafetyText.Foreground = fg;

        if (level == SafetyLevel.Low)
        {
            DangerBar.Visibility = Visibility.Visible;
            InfoBar.Visibility = Visibility.Collapsed;
            ConfirmButton.IsEnabled = false;
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Ack_Changed(object sender, RoutedEventArgs e)
    {
        ConfirmButton.IsEnabled = AckBox.IsChecked == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var note = NoteBox.Text == NotePlaceholder ? null : NoteBox.Text.Trim();
        Result = new PreviewResult(true, Array.Empty<string>(), string.IsNullOrEmpty(note) ? null : note);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = new PreviewResult(false, new[] { "用户取消预演" }, null);
        DialogResult = false;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Cancel_Click(sender, e);

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine("sysmgr TRUST-02 预演清单");
        sb.AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"模块：{_plan.ModuleId}（{_plan.FrId}）");
        sb.AppendLine($"描述：{_plan.Description}");
        sb.AppendLine($"安全等级：{_plan.SafetyLevel}");
        sb.AppendLine();
        for (var i = 0; i < _plan.Actions.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {Describe(_plan.Actions[i])}");
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"sysmgr_preview_{_plan.PlanId:N}.txt",
            Filter = "文本文件|*.txt"
        };
        if (dlg.ShowDialog() == true)
        {
            System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        }
    }

    private static string Describe(OperationAction action)
    {
        var subject = action.Target ?? action.Key ?? action.ServiceName ?? action.TaskPath ?? string.Empty;
        var newVal = action.NewValue ?? action.NewPath;
        return string.IsNullOrEmpty(newVal)
            ? $"{action.Kind}：{subject}"
            : $"{action.Kind}：{subject} → {newVal}";
    }

    /// <summary>预演列表行模型。</summary>
    private sealed class PreviewRow
    {
        public PreviewRow(int index, OperationAction a)
        {
            Index = index;
            (KindText, KindColor) = MapKind(a.Kind);
            Subject = a.Target ?? a.Key ?? a.ServiceName ?? a.TaskPath ?? string.Empty;
            Impact = BuildImpact(a);
            SizeText = a.SizeBytes is long bytes && bytes > 0 ? Converters.BytesFormatter.Format(bytes) : string.Empty;
        }

        public int Index { get; }
        public string KindText { get; }
        public Brush KindColor { get; }
        public string Subject { get; }
        public string Impact { get; }
        public string SizeText { get; }

        private static (string, Brush) MapKind(ActionKind kind) => kind switch
        {
            ActionKind.DeleteFile => ("删除文件", Danger),
            ActionKind.Purge => ("清理垃圾", Danger),
            ActionKind.Shred => ("粉碎", Danger),
            ActionKind.MoveFile => ("迁移文件", Accent),
            ActionKind.CopyFile => ("复制文件", Accent),
            ActionKind.DeleteRegistry => ("删除注册表", Purple),
            ActionKind.SetRegistry => ("写入注册表", Purple),
            ActionKind.StopService => ("停止服务", Warn),
            ActionKind.DisableService => ("禁用服务", Warn),
            ActionKind.DisableStartup => ("禁用启动项", Warn),
            ActionKind.DeleteScheduledTask => ("删除计划任务", Purple),
            ActionKind.KillProcess => ("结束进程", Warn),
            ActionKind.RunExternalUninstaller => ("运行卸载程序", Accent),
            ActionKind.CreateJunction => ("创建联接", Accent),
            ActionKind.RedirectKnownFolder => ("改写保存位置", Accent),
            ActionKind.SetStartupEnabled => ("切换启动项", Warn),
            ActionKind.SetScheduledTaskEnabled => ("切换计划任务", Warn),
            _ => (kind.ToString(), Accent)
        };

        private static string BuildImpact(OperationAction a)
        {
            if (!string.IsNullOrEmpty(a.NewPath))
            {
                return a.NewPath!;
            }
            if (!string.IsNullOrEmpty(a.NewValue))
            {
                return string.IsNullOrEmpty(a.OriginalValue)
                    ? a.NewValue!
                    : $"{a.OriginalValue} → {a.NewValue}";
            }
            return string.Empty;
        }
    }
}
