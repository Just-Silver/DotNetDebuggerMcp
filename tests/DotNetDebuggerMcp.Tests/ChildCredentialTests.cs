using System.ComponentModel;
using SharpSight.ChildSession.Security;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildCredentialTests
{
    [Theory]
    [InlineData(@"DOMAIN\alice", "alice", "DOMAIN")]
    [InlineData(@"MicrosoftAccount\alice@example.com", "alice@example.com", "MicrosoftAccount")]
    [InlineData(@"alice@example.com", "alice@example.com", "")]
    [InlineData(@"alice", "alice", "")]
    [InlineData(@"  alice  ", "alice", "")]
    [InlineData(@"", "", "")]
    public void 用户名按域与UPN形式拆分(string raw, string userName, string domain)
    {
        var split = ChildCredential.Split(raw);
        Assert.Equal(userName, split.UserName);
        Assert.Equal(domain, split.Domain);
    }

    [Fact]
    public void 用户名或密码为空时不能成为自动登录凭据()
    {
        Assert.Null(ChildCredential.TryCreate("", "secret", "test"));
        Assert.Null(ChildCredential.TryCreate("user", "", "test"));
        var credential = ChildCredential.TryCreate(@"DOMAIN\user", "secret", "test");
        Assert.NotNull(credential);
        Assert.Equal("user", credential.UserName);
        Assert.Equal("DOMAIN", credential.Domain);
        Assert.Equal("secret", credential.Password);
        Assert.Equal("test", credential.Source);
    }

    [Fact]
    public void 缺失的凭据目标按不存在处理而不是抛错()
    {
        var target = "DotNetDebuggerMcp/missing-" + Guid.NewGuid().ToString("N");
        Assert.False(WindowsCredentialVault.Exists(target, WindowsCredentialVault.Generic));
        Assert.False(WindowsCredentialVault.Exists(target, WindowsCredentialVault.DomainPassword));
        Assert.Null(WindowsCredentialVault.Read(target, WindowsCredentialVault.Generic));
        Assert.Null(WindowsCredentialVault.Read(target, WindowsCredentialVault.DomainPassword));
    }

    [Fact]
    public void 凭据库写入读取删除往返()
    {
        var target = "DotNetDebuggerMcp/tests-" + Guid.NewGuid().ToString("N");
        try
        {
            try
            {
                WindowsCredentialVault.Write(target, WindowsCredentialVault.Generic,
                    @"DOMAIN\tester", "p@ss-中文-123", "临时测试凭据");
            }
            catch (Win32Exception ex)
            {
                Assert.Skip($"本机凭据库不可写：{ex.Message}");
            }
            Assert.True(WindowsCredentialVault.Exists(target, WindowsCredentialVault.Generic));
            var value = WindowsCredentialVault.Read(target, WindowsCredentialVault.Generic);
            Assert.NotNull(value);
            Assert.Equal(@"DOMAIN\tester", value.Value.UserName);
            Assert.Equal("p@ss-中文-123", value.Value.Password);
            Assert.True(WindowsCredentialVault.Delete(target, WindowsCredentialVault.Generic));
            Assert.False(WindowsCredentialVault.Exists(target, WindowsCredentialVault.Generic));
            Assert.Null(WindowsCredentialVault.Read(target, WindowsCredentialVault.Generic));
        }
        finally { try { WindowsCredentialVault.Delete(target, WindowsCredentialVault.Generic); } catch { } }
    }
}
