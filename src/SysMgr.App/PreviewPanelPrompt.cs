using System.Windows;
using SysMgr.Core.Operations;
using SysMgr.Core.Trust;

namespace SysMgr.App;

/// <summary>
/// TRUST-02 预演端口的生产实现：弹出深色科技风 <see cref="Views.PreviewDialog"/>，
/// 在 UI 线程上等待用户审核结论。
/// </summary>
public sealed class PreviewPanelPrompt : IPreviewPrompt
{
    /// <inheritdoc />
    public Task<PreviewResult> PromptAsync(OperationPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var dispatcher = Application.Current?.Dispatcher
                         ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

        return Task.FromResult(dispatcher.Invoke(() =>
        {
            var dialog = new Views.PreviewDialog(plan)
            {
                Owner = Application.Current?.MainWindow
            };
            ct.Register(() => dialog.Dispatcher.Invoke(dialog.Close));
            dialog.ShowDialog();
            return dialog.Result ?? new PreviewResult(false, new[] { "预演被中断" }, null);
        }));
    }
}
