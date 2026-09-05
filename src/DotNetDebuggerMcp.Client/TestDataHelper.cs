namespace DotNetDebuggerMcp.Client;

/// <summary>
/// TestData 测试程序集共享入口：自动发现 tests/TestData 目录下的 dll，并提供端到端用例共用的类型/成员标识， 避免 Program 与各 Case
/// 文件重复硬编码路径、类型名与成员 ID。
/// </summary>
public static class TestDataHelper
{
    /// <summary>
    /// 超大类型（反编译 600+ 行），可触发截断/分页/越界验证。
    /// </summary>
    public const string TypeName = "ILSpyMcp.Samples.BigClass";

    /// <summary>
    /// list_types 默认只返回前约 8 KB，此处用一个排在最前、必然可见的 class 类型名。
    /// </summary>
    public const string ListedClassName = "ILSpyMcp.Samples.Class0001";

    /// <summary>
    /// 继承链派生类（hierarchy 基类链正向/BaseClass 反向验证）。
    /// </summary>
    public const string DerivedTypeName = "ILSpyMcp.Samples.DerivedClass";

    /// <summary>
    /// 接口类型（hierarchy 反向实现者验证）。
    /// </summary>
    public const string InterfaceTypeName = "ILSpyMcp.Samples.IAnimal";

    /// <summary>
    /// 属性/字段/事件/方法齐全的类型（signature 访问器合并与 decompile_member 访问器排除验证）。
    /// </summary>
    public const string MembersTypeName = "ILSpyMcp.Samples.Members";

    /// <summary>
    /// 泛型类型（signature 泛型参数与泛型方法验证）。
    /// </summary>
    public const string GenericTypeName = "ILSpyMcp.Samples.GenericBox`1";

    /// <summary>
    /// 成员签名引用内部类型（dependencies 正向引用验证）。
    /// </summary>
    public const string UsesTypeName = "ILSpyMcp.Samples.Uses";

    /// <summary>
    /// 方法体调用内部方法（call_graph 正向调用验证）。
    /// </summary>
    public const string CallerTypeName = "ILSpyMcp.Samples.Caller";

    /// <summary>
    /// 被调用方（call_graph token 方法级调用点验证：Callee 方法被 Caller 调用）。
    /// </summary>
    public const string CalleeTypeName = "ILSpyMcp.Samples.Callee";

    /// <summary>
    /// 仓库根目录（含 DotNetDebuggerMcp.slnx），随仓库整体移动自动适配。
    /// </summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>
    /// 测试程序集路径：明确指向主样本 ILSpyMcp.TestSamples.dll（tests/TestData 下可能还有 Ext/DebugTarget 等，
    /// 不能按「第一个 dll」取——字母序会选错）。
    /// </summary>
    public static string Dll { get; } = Path.Combine(RepoRoot, "tests", "TestData", "ILSpyMcp.TestSamples.dll");

    /// <summary>
    /// 跨程序集测试程序集（引用 TestSamples.dll 的 Callee），供 call_chain includeExternal 跨程序集展开用例。
    /// </summary>
    public static string ExtDll { get; } = Path.Combine(RepoRoot, "tests", "TestData", "ILSpyMcp.TestSamplesExt.dll");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DotNetDebuggerMcp.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("未找到仓库根目录（缺少 DotNetDebuggerMcp.slnx）");
    }
}