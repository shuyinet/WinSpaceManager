namespace SysMgr.Core.Tests.Fakes;

/// <summary>
/// 动作观察者（测试用：把 OnNext 回调包装成 IObserver，替代 System.Reactive Observer.Create）。
/// </summary>
public sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void OnNext(T value) => onNext(value);
}
