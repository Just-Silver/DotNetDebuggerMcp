using System.Reflection.PortableExecutable;

namespace DotNetDebugger.Decompiler.Configuration;

/// <summary>
/// 能力库自用的内部常量（自原宿主 Configuration.AppConfig 拆分，仅 Decompiler/Metadata 层用到的最小集）：
/// 避免能力库反向依赖宿主程序集。
/// </summary>
internal static class DecompilerConfig
{
    /// <summary>
    /// 引用程序集解析器（<c>UniversalAssemblyResolver</c>）打开依赖程序集的流选项：PrefetchMetadata 令 PEReader 在构造时
    /// 一次读入元数据并立即关闭文件句柄。若不传（默认 Default），解析出的 PEFile 无人 Dispose，OS 句柄会持有到 GC——
    /// 被反编译程序集所在 bin 目录的 dll 被锁住，随后 dotnet build/clean 报 MSB3061/MSB3021（用户侧「MCP 锁定 dll」）。
    /// 与 ILSpy 官方按文件名反编译路径（LoadInMemory=true 时解析器取 PrefetchMetadata）一致。
    /// </summary>
    public const PEStreamOptions DependencyStreamOptions = PEStreamOptions.PrefetchMetadata;

    /// <summary>
    /// call_chain 跨程序集调用展开的最大递归深度（原 AppConfig.ExternalExpandMaxDepth）。
    /// </summary>
    public const int ExternalExpandMaxDepth = 5;

    /// <summary>
    /// call_chain 单次跨程序集调用展开最多展开的外部节点数（原 AppConfig.ExternalExpandMaxNodes）。
    /// </summary>
    public const int ExternalExpandMaxNodes = 200;

    /// <summary>
    /// 单次反编译生成文本的字符数上限（原 AppConfig.MaxOutputBytes，值与宿主缓存上限一致）：超过即返回
    /// 「建议改用 decompile_to_dir」提示且该结果不入缓存。
    /// </summary>
    public const long MaxOutputBytes = 64 * 1024 * 1024;
}
