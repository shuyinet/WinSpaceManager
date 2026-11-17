using System.Collections.ObjectModel;
using SysMgr.Core.Logging;

namespace SysMgr.App.ViewModels;

/// <summary>
/// 操作直播面板 ViewModel（FR-TRUST-01）。
/// 订阅 IOperationLogger.LiveStream，实时追加条目到集合。
/// 暂停仅停止渲染（停止 UI 追加），不中断落盘与广播。
/// </summary>
public sealed class LiveStreamViewModel : ViewModelBase, IDisposable
{
    private readonly IDisposable? _subscription;
    private bool _isPaused;

    public LiveStreamViewModel(SysMgrServices services)
    {
        ClearCommand = new RelayCommand(_ => Entries.Clear());
        TogglePauseCommand = new RelayCommand(_ => IsPaused = !IsPaused);
        _subscription = services.Logger.LiveStream.Subscribe(new LiveObserver(entry =>
        {
            if (!IsPaused)
            {
                App.Current?.Dispatcher.Invoke(() => Entries.Insert(0, entry));
            }
        }));
    }

    public ObservableCollection<OperationLogEntry> Entries { get; } = new();

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (Set(ref _isPaused, value))
            {
                OnPropertyChanged(nameof(PauseButtonText));
            }
        }
    }

    public string PauseButtonText => IsPaused ? "继续滚动" : "暂停滚动";

    public RelayCommand ClearCommand { get; }
    public RelayCommand TogglePauseCommand { get; }

    public void Dispose()
    {
        _subscription?.Dispose();
    }

    /// <summary>IObserver 包装（Core 零依赖无 System.Reactive 扩展）。</summary>
    private sealed class LiveObserver(Action<OperationLogEntry> onNext) : IObserver<OperationLogEntry>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(OperationLogEntry value) => onNext(value);
    }
}
