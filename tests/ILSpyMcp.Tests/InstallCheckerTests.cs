using ILSpyMcp;
using Xunit;

namespace ILSpyMcp.Tests;

public class InstallCheckerTests
{
    [Fact]
    public async Task 退出码为0_判定已安装()
    {
        var fake = new FakeProcessRunner { Code = 0 };
        var checker = new InstallChecker(fake);

        Assert.True(await checker.CheckInstalledAsync());
    }

    [Fact]
    public async Task 退出码非0_判定未安装()
    {
        var fake = new FakeProcessRunner { Code = 1 };
        var checker = new InstallChecker(fake);

        Assert.False(await checker.CheckInstalledAsync());
    }

    [Fact]
    public async Task 会话内缓存_重复检测只拉起一次子进程()
    {
        var fake = new FakeProcessRunner { Code = 0 };
        var checker = new InstallChecker(fake);

        await checker.CheckInstalledAsync();
        await checker.CheckInstalledAsync();

        Assert.Equal(1, fake.CallCount);
        Assert.True(checker.IsInstalled);
    }

    [Fact]
    public async Task 检测参数为ilspycmd版本号()
    {
        var fake = new FakeProcessRunner { Code = 0 };
        var checker = new InstallChecker(fake);

        await checker.CheckInstalledAsync();

        Assert.Equal(1, fake.CallCount);
        Assert.Equal("ilspycmd", fake.LastExecutable);
        Assert.Equal("-v", Assert.Single(fake.LastArgs!));
        Assert.NotNull(fake.Timeout);
    }

    [Fact]
    public async Task 并发检测_只拉起一次子进程()
    {
        var fake = new FakeProcessRunner { Code = 0 };
        var checker = new InstallChecker(fake);

        var tasks = Enumerable.Range(0, 20).Select(_ => checker.CheckInstalledAsync()).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r));
        Assert.Equal(1, fake.CallCount);
        Assert.True(checker.IsInstalled);
    }

    [Fact]
    public async Task 失败场景_首次缓存false后续不再调用()
    {
        var fake = new FakeProcessRunner { Code = -1 };
        var checker = new InstallChecker(fake);

        Assert.False(await checker.CheckInstalledAsync());
        Assert.False(await checker.CheckInstalledAsync());

        Assert.False(checker.IsInstalled);
        Assert.Equal(1, fake.CallCount);
    }
}
