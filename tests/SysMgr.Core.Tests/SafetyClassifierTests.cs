using SysMgr.Core.Safety;
using Xunit;

namespace SysMgr.Core.Tests;

public sealed class SafetyClassifierTests
{
    private static readonly string[] CriticalPrefixes =
    [
        @"C:\Windows\System32",
        @"C:\Windows\WinSxS",
        @"HKLM\SYSTEM\CurrentControlSet"
    ];

    private readonly SafetyClassifier _classifier = new(CriticalPrefixes);

    [Fact]
    public void 系统关键路径判Low()
    {
        var verdict = _classifier.Classify(new FileItem(@"C:\Windows\System32\drivers\x.sys", 10));

        Assert.Equal(SafetyLevel.Low, verdict.Level);
        Assert.NotEmpty(verdict.Evidence);
    }

    [Fact]
    public void 前缀近似但非子目录不误判()
    {
        // System32xxx 不应被当作 System32 子目录
        var verdict = _classifier.Classify(new FileItem(@"C:\Windows\System32bak\x", 10));

        Assert.NotEqual(SafetyLevel.Low, verdict.Level);
    }

    [Fact]
    public void 临时与缓存路径判High()
    {
        Assert.Equal(SafetyLevel.High,
            _classifier.Classify(new FileItem(@"C:\Users\a\AppData\Local\Temp\foo.tmp", 1)).Level);
        Assert.Equal(SafetyLevel.High,
            _classifier.Classify(new FileItem(@"C:\Users\a\AppData\Local\X\Cache\data", 1)).Level);
        Assert.Equal(SafetyLevel.High,
            _classifier.Classify(new FileItem(@"C:\Explorer\thumbcache_256.db", 1)).Level);
    }

    [Fact]
    public void 注册表残留判High()
    {
        var verdict = _classifier.Classify(
            new RegistryItem(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Foo", null));

        Assert.Equal(SafetyLevel.High, verdict.Level);
    }

    [Fact]
    public void 普通程序目录判Medium()
    {
        var verdict = _classifier.Classify(new FileItem(@"C:\ProgramData\Shared\config.xml", 10));

        Assert.Equal(SafetyLevel.Medium, verdict.Level);
    }
}
