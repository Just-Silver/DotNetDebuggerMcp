using DotNetDebugger.Web.Services;
using Xunit;

namespace DotNetDebugger.Web.Tests;

/// <summary>TypeTreeData 服务端测试：程序集类型枚举与命名空间分组（纯元数据秒回）。</summary>
public sealed class TypeTreeDataTests
{
    [Fact]
    public void GetNamespaces_测试样本_含Samples命名空间且缓存()
    {
        var data = new TypeTreeData();
        var nss = data.GetNamespaces(TestDataPaths.TestSamplesDll);

        Assert.NotNull(nss);
        Assert.Contains("ILSpyMcp.Samples", nss!);
        // 同 dll 再查命中缓存（同一 AssemblyTree 实例）
        var nss2 = data.GetNamespaces(TestDataPaths.TestSamplesDll);
        Assert.Same(nss, nss2);
    }

    [Fact]
    public void GetTypes_Samples命名空间_含BigClass且含生成类型过滤()
    {
        var data = new TypeTreeData();
        var types = data.GetTypes(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples");

        Assert.Contains("ILSpyMcp.Samples.BigClass", types);
        // 编译器生成类型（含 < > 的匿名/闭包类）不应出现
        Assert.DoesNotContain(types, t => t.Contains('<'));
    }

    [Fact]
    public void GetTypes_未知命名空间_返回空()
    {
        var data = new TypeTreeData();
        Assert.Empty(data.GetTypes(TestDataPaths.TestSamplesDll, "No.Such.Ns"));
    }

    [Fact]
    public void GetNamespaces_不存在的dll_返回null()
    {
        var data = new TypeTreeData();
        Assert.Null(data.GetNamespaces(@"C:\no\such\file.dll"));
    }
}
