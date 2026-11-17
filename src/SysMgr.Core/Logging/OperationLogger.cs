using System.Text.Json;

namespace SysMgr.Core.Logging;

/// <summary>
/// 操作日志器实现（TDD §2.6 / FR-TRUST-01）。
/// history.jsonl 追加写入（append-only，永不自动清理），同时通过 <see cref="LiveStream"/> 推送订阅者。
/// 写入采用 FileShare.Read 允许第三方工具随时读取核验（TRUST-04 第 1 层证据）。
/// </summary>
public sealed class OperationLogger : IOperationLogger
{
    private readonly string _historyFilePath;
    private readonly SimpleLiveSubject _stream = new();
    private readonly object _fileLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public OperationLogger(string historyFilePath)
    {
        _historyFilePath = historyFilePath ?? throw new ArgumentNullException(nameof(historyFilePath));
    }

    /// <inheritdoc />
    public IObservable<OperationLogEntry> LiveStream => _stream;

    /// <inheritdoc />
    public void LogOperation(OperationLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var line = JsonSerializer.Serialize(entry, JsonOptions);
        lock (_fileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_historyFilePath)!);
            File.AppendAllText(_historyFilePath, line + Environment.NewLine);
        }

        _stream.Publish(entry);
    }

    /// <summary>零依赖的直播 Subject（Core 不引入 System.Reactive）。</summary>
    private sealed class SimpleLiveSubject : IObservable<OperationLogEntry>
    {
        private readonly object _gate = new();
        private readonly List<IObserver<OperationLogEntry>> _observers = new();

        public IDisposable Subscribe(IObserver<OperationLogEntry> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            lock (_gate)
            {
                _observers.Add(observer);
            }
            return new Subscription(this, observer);
        }

        public void Publish(OperationLogEntry entry)
        {
            IObserver<OperationLogEntry>[] snapshot;
            lock (_gate)
            {
                snapshot = _observers.ToArray();
            }
            foreach (var observer in snapshot)
            {
                observer.OnNext(entry);
            }
        }

        private void Unsubscribe(IObserver<OperationLogEntry> observer)
        {
            lock (_gate)
            {
                _observers.Remove(observer);
            }
        }

        private sealed class Subscription(
            SimpleLiveSubject owner,
            IObserver<OperationLogEntry> observer) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                owner.Unsubscribe(observer);
            }
        }
    }
}
