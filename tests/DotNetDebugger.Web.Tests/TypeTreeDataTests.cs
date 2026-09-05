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
        Assert.Contains(TestDataPaths.SamplesNamespace, nss!);
        // 同 dll 再查命中缓存（同一 AssemblyTree 实例）
        var nss2 = data.GetNamespaces(TestDataPaths.TestSamplesDll);
        Assert.Same(nss, nss2);
    }

    [Fact]
    public void GetTypes_Samples命名空间_含BigClass且含生成类型过滤()
    {
        var data = new TypeTreeData();
        var types = data.GetTypes(TestDataPaths.TestSamplesDll, TestDataPaths.SamplesNamespace);

        Assert.Contains($"{TestDataPaths.SamplesNamespace}.BigClass", types);
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

    [Fact]
    public void GetMembers_BigClass_含BigMethod且过滤访问器()
    {
        var data = new TypeTreeData();
        var members = data.GetMembers(TestDataPaths.TestSamplesDll, $"{TestDataPaths.SamplesNamespace}.BigClass");

        Assert.NotEmpty(members);
        // BigMethod 在（方法，带 token）
        var big = Assert.Single(members, m => m.Name == "BigMethod");
        Assert.Equal(TypeMemberKind.Method, big.Kind);
        Assert.True(big.Token > 0);
        // 属性/事件访问器方法（get_/set_/add_/remove_）不作为独立方法出现
        Assert.DoesNotContain(members, m => m.Name.StartsWith("get_") || m.Name.StartsWith("set_")
            || m.Name.StartsWith("add_") || m.Name.StartsWith("remove_"));
    }

    [Fact]
    public void GetMembers_Props类型_属性与索引器在且字段backing被滤()
    {
        var data = new TypeTreeData();
        var members = data.GetMembers(TestDataPaths.TestSamplesDll, $"{TestDataPaths.SamplesNamespace}.Props");

        // 属性节点在（含静态属性/索引器），访问器方法不单列
        Assert.Contains(members, m => m.Kind == TypeMemberKind.Property);
        // 自动属性 backing field（名含 <）不出现
        Assert.DoesNotContain(members, m => m.Name.Contains('<'));
        // 方法顺序在属性前（dnSpyEx 顺序：方法→属性→事件→字段）
        var firstMethod = members.ToList().FindIndex(m => m.Kind == TypeMemberKind.Method);
        var firstProp = members.ToList().FindIndex(m => m.Kind == TypeMemberKind.Property);
        if (firstMethod >= 0 && firstProp >= 0) Assert.True(firstMethod < firstProp);
    }

    [Fact]
    public void GetMembers_未知类型_返回空()
    {
        var data = new TypeTreeData();
        Assert.Empty(data.GetMembers(TestDataPaths.TestSamplesDll, "No.Such.Type"));
    }
}
