using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;
using ILSpyMcp.Configuration;
using ILSpyMcp.Metadata;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace ILSpyMcp.Decompiler;

/// <summary>
/// 进程内反编译服务：以 ICSharpCode.Decompiler 库在进程内完成反编译。
/// 每次调用独立构建 PEFile + UniversalAssemblyResolver + DecompilerSettings + CSharpDecompiler，用完即释放；
/// 全部入口 try/catch 兜底返回中文提示，不抛异常（纯元数据定位用 ILSpyMcp.Metadata.MetadataNaming）。
/// </summary>
public sealed class InProcessDecompiler
{
    /// <summary>
    /// 超时包装：同步 work 放入 Task.Run 在后台执行，并把取消令牌注入 work（由反编译引擎在协作式检查点自行中断）。
    /// timeout 内未完成（或取消触发）时立即返回 timeoutHint（不阻塞等待后台任务中断完成）；引擎收到令牌后会在
    /// 下一个检查点抛 OperationCanceledException 自行停止，后台不再跑完占 CPU。注意 Task.Delay(timeout, cancellationToken)
    /// 在取消触发时会以取消状态完成，统一视为超时处理。
    /// </summary>
    /// <param name="work">要执行的同步反编译工作，接收取消令牌并返回文本；令牌传递给反编译引擎实现协作式中断。</param>
    /// <param name="timeout">最长等待时间；超时即返回 timeoutHint。</param>
    /// <param name="cancellationToken">取消令牌；取消触发同样返回 timeoutHint，且令牌会传入 work。</param>
    /// <param name="timeoutHint">超时/取消时返回的中文提示文本。</param>
    /// <returns>work 的结果，或 timeoutHint（超时/取消），或失败时的中文错误提示。</returns>
    public static async Task<string> RunWithTimeoutAsync(Func<CancellationToken, string> work, TimeSpan timeout, CancellationToken cancellationToken, string timeoutHint)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var task = Task.Run(() => work(cts.Token));
        try
        {
            // 超时由 cts.CancelAfter 触发、取消由外部令牌经链接触发：令牌注入 work 后引擎在检查点抛 OCE 自行中断（不再跑完占 CPU）。
            // 取消时 Task.Delay 以取消状态完成（Task.WhenAny 不抛异常），返回的 completed 不是 work 任务即视为超时/取消
            var delay = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(task, delay);
            if (ReferenceEquals(completed, task))
            {
                // work 先完成：返回其结果（work 抛 OCE 时由下 catch 转成超时/取消提示）
                return await task;
            }
            // 超时/取消：立即返回 timeoutHint，不 await 后台任务；引擎收到令牌后在下一检查点协作式中断
            return timeoutHint;
        }
        catch (OperationCanceledException)
        {
            // 兜底：work 自身抛出的 OperationCanceledException（引擎检查点中断）统一按超时/取消返回 timeoutHint
            return timeoutHint;
        }
        catch (Exception ex)
        {
            // work 本身抛出的异常不向上传播，兜底返回中文提示（后台任务异常不可被外部观察，此处就地降级）
            return $"反编译失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 反编译指定类型到文本；未找到类型返回中文提示；输出超 <see cref="AppConfig.MaxOutputBytes"/> 字符时返回改用写盘提示。
    /// </summary>
    /// <param name="assemblyPath">程序集文件路径（dll/exe）。</param>
    /// <param name="typeName">类型全名，兼容 +/ . 嵌套分隔与泛型 arity（如 GenericBox`1）。</param>
    /// <param name="cancellationToken">取消令牌，透传给反编译引擎实现协作式中断。</param>
    /// <returns>反编译文本或中文提示。</returns>
    public static string DecompileType(string assemblyPath, string typeName, CancellationToken cancellationToken = default)
    {
        return Execute(assemblyPath, cancellationToken, (module, decompiler) =>
        {
            var handle = MetadataNaming.FindType(module.Metadata, typeName);
            if (handle is null) return MetadataNaming.BuildNotFoundMessage(module.Metadata, typeName);
            return CheckOutputSize(decompiler.DecompileAsString(handle.Value));
        });
    }

    /// <summary>
    /// 反编译指定成员（元数据 token，如 "0x06000005"）到文本；token 非法/越界返回中文提示；输出超上限时返回改用写盘提示。
    /// </summary>
    /// <param name="assemblyPath">程序集文件路径（dll/exe）。</param>
    /// <param name="token">元数据 token，0x 开头的十六进制，如 0x06000005。</param>
    /// <param name="cancellationToken">取消令牌，透传给反编译引擎实现协作式中断。</param>
    /// <returns>反编译文本或中文提示。</returns>
    public static string DecompileMember(string assemblyPath, string token, CancellationToken cancellationToken = default)
    {
        return Execute(assemblyPath, cancellationToken, (module, decompiler) =>
        {
            var trimmed = token.Trim();
            if (!trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var tokenValue))
            {
                return $"\"{trimmed}\" 不是有效的元数据 token，应为 0x 开头的十六进制格式，如 0x06000005";
            }

            // 按 token 的 Kind 校验 row 数防越界
            var candidate = MetadataTokens.EntityHandle(tokenValue);
            int rowNumber = tokenValue & 0x00ffffff;
            int rowCount = candidate.Kind switch
            {
                HandleKind.TypeDefinition => module.Metadata.TypeDefinitions.Count,
                HandleKind.FieldDefinition => module.Metadata.FieldDefinitions.Count,
                HandleKind.MethodDefinition => module.Metadata.MethodDefinitions.Count,
                HandleKind.PropertyDefinition => module.Metadata.PropertyDefinitions.Count,
                HandleKind.EventDefinition => module.Metadata.EventDefinitions.Count,
                _ => 0,
            };
            if (rowNumber < 1 || rowNumber > rowCount)
            {
                return $"元数据 token {trimmed} 未引用本模块的类型或成员";
            }

            return CheckOutputSize(decompiler.DecompileAsString(candidate));
        });
    }

    /// <summary>
    /// 反编译整个程序集到文本；输出超上限时返回改用写盘提示。
    /// </summary>
    /// <param name="assemblyPath">程序集文件路径（dll/exe）。</param>
    /// <param name="cancellationToken">取消令牌，透传给反编译引擎实现协作式中断。</param>
    /// <returns>反编译文本或中文提示。</returns>
    public static string DecompileWholeModule(string assemblyPath, CancellationToken cancellationToken = default)
    {
        return Execute(assemblyPath, cancellationToken, (_, decompiler) => CheckOutputSize(decompiler.DecompileWholeModuleAsString()));
    }

    /// <summary>
    /// 反编译写入目录：单文件布局，全量时输出 {程序集名}.decompiled.cs，指定类型时每个类型一个 {TypeName}.decompiled.cs 文件。
    /// 写入磁盘不做输出上限截断；返回成功提示（含文件数）或错误提示。
    /// </summary>
    /// <param name="assemblyPath">程序集文件路径（dll/exe）。</param>
    /// <param name="outputDir">输出目录（不存在则创建）。</param>
    /// <param name="typeName">指定则仅反编译该类型，支持逗号分隔多个类型批量写盘；为空则反编译整个程序集。</param>
    /// <param name="cancellationToken">取消令牌，透传给反编译引擎实现协作式中断。</param>
    /// <returns>成功提示（含文件数与来源）或错误提示。</returns>
    public static string DecompileToDir(string assemblyPath, string outputDir, string? typeName, CancellationToken cancellationToken = default)
    {
        return Execute(assemblyPath, cancellationToken, (module, decompiler) =>
        {
            Directory.CreateDirectory(outputDir);
            if (string.IsNullOrEmpty(typeName))
            {
                // 全量：整个程序集写入单文件 {程序集名}.decompiled.cs
                var fullPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(assemblyPath) + ".decompiled.cs");
                File.WriteAllText(fullPath, decompiler.DecompileWholeModuleAsString());
                return BuildWriteSuccess(outputDir, assemblyPath);
            }

            // 指定类型：typeName 支持逗号分隔多个类型批量写盘，每个类型写入 {TypeName}.decompiled.cs。
            // 宽松语义——找到的写盘、未找到的累计进提示，部分成功也算成功
            var names = typeName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var missing = new List<string>();
            foreach (var name in names)
            {
                var handle = MetadataNaming.FindType(module.Metadata, name);
                if (handle is null)
                {
                    missing.Add(name);
                    continue;
                }
                var text = decompiler.DecompileAsString(handle.Value);
                File.WriteAllText(Path.Combine(outputDir, name + ".decompiled.cs"), text);
            }
            if (missing.Count > 0)
            {
                // 单一类型未找到：保持既有错误提示形态（「未找到类型 」前缀会被 IsErrorResult 判为错误），附相近类型名
                if (names.Length == 1) return MetadataNaming.BuildNotFoundMessage(module.Metadata, missing[0]);
                // 批量未找到：附于成功提示之后（不以「未找到类型 」开头，避免被 IsErrorResult 误判为错误）
                var hint = BuildWriteSuccess(outputDir, assemblyPath);
                return hint[..^1] + $"；未找到：{string.Join("、", missing)}）";
            }
            return BuildWriteSuccess(outputDir, assemblyPath);
        });
    }

    /// <summary>
    /// 项目模式写盘：{程序集名}.csproj + 每个类型一个源码文件，nestedDirectories 控制是否按命名空间嵌套目录。
    /// 写入磁盘不做输出上限截断；返回成功提示（含文件数）或错误提示。
    /// </summary>
    /// <param name="assemblyPath">程序集文件路径（dll/exe）。</param>
    /// <param name="outputDir">输出目录（不存在则创建）。</param>
    /// <param name="nestedDirectories">是否按命名空间嵌套目录。</param>
    /// <param name="cancellationToken">取消令牌，透传给反编译引擎实现协作式中断。</param>
    /// <returns>成功提示（含文件数与来源）或错误提示。</returns>
    public static string DecompileToProject(string assemblyPath, string outputDir, bool nestedDirectories, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            var projectFileName = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(assemblyPath) + ".csproj");

            using var module = OpenModule(assemblyPath);
            var resolver = new UniversalAssemblyResolver(assemblyPath, false, module.Metadata.DetectTargetFrameworkId());
            var settings = new DecompilerSettings
            {
                ThrowOnAssemblyResolveErrors = false,
                UseSdkStyleProjectFormat = WholeProjectDecompiler.CanUseSdkStyleProjectFormat(module),
                UseNestedDirectoriesForNamespaces = nestedDirectories,
            };
            var projectDecompiler = new WholeProjectDecompiler(settings, resolver, null, resolver, null);
            // append: false 以截断模式打开 csproj：向已存在该文件的目录重跑时清空旧内容，避免 File.OpenWrite 的 OpenOrCreate
            // 语义残留旧尾部字节导致生成损坏的 XML
            using (var projectFileWriter = new StreamWriter(projectFileName, append: false))
            {
                projectDecompiler.DecompileProject(module, outputDir, projectFileWriter, cancellationToken);
            }
            return BuildWriteSuccess(outputDir, assemblyPath);
        }
        catch (OperationCanceledException)
        {
            // 引擎协作式检查点检测到取消：后台任务被中断，返回取消提示（不得被泛型 catch 吞成「反编译失败」）
            return "反编译已取消";
        }
        catch (IOException ex)
        {
            return $"反编译失败：IO 错误（{ex.Message}）";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"反编译失败：无访问权限（{ex.Message}）";
        }
        catch (BadImageFormatException ex)
        {
            return $"反编译失败：程序集格式无效（{ex.Message}）";
        }
        catch (Exception ex)
        {
            return $"反编译失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 安全打开程序集：自建 FileStream 传给 PEFile（成功接管所有权由 using 释放）。
    /// 直接 new PEFile(path) 在解析失败（如非程序集文件）抛异常时 FileStream 句柄会泄漏到 GC，这里显式兜底释放。
    /// </summary>
    /// <param name="assemblyPath">程序集文件路径。</param>
    /// <returns>构造成功的 PEFile（调用方负责 using 释放）。</returns>
    private static PEFile OpenModule(string assemblyPath)
    {
        var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read);
        try
        {
            return new PEFile(assemblyPath, stream);
        }
        catch
        {
            // PEFile 构造失败未接管流，此处释放避免句柄泄漏，随后重抛由外层 catch 转成中文提示
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 构建 PEFile + resolver + settings + CSharpDecompiler 并执行 work；I/O/格式/未知异常统一返回中文错误提示，不抛异常。
    /// 取消令牌注入 <see cref="CSharpDecompiler.CancellationToken"/>，引擎在协作式检查点自行中断并抛 OCE，转为「反编译已取消」。
    /// PEFile 用 using 释放（resolver/settings/decompiler 非 IDisposable）。
    /// </summary>
    /// <param name="assemblyPath">程序集文件路径。</param>
    /// <param name="cancellationToken">取消令牌，注入反编译引擎。</param>
    /// <param name="work">反编译工作，接收 PEFile 与 CSharpDecompiler，返回提示文本或反编译文本。</param>
    /// <returns>work 的结果或中文错误提示。</returns>
    private static string Execute(string assemblyPath, CancellationToken cancellationToken, Func<PEFile, CSharpDecompiler, string> work)
    {
        try
        {
            using var module = OpenModule(assemblyPath);
            var resolver = new UniversalAssemblyResolver(assemblyPath, false, module.Metadata.DetectTargetFrameworkId());
            var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
            var decompiler = new CSharpDecompiler(assemblyPath, resolver, settings);
            decompiler.CancellationToken = cancellationToken;
            return work(module, decompiler);
        }
        catch (OperationCanceledException)
        {
            // 引擎协作式检查点检测到取消：后台任务被中断，返回取消提示（不得被泛型 catch 吞成「反编译失败」）
            return "反编译已取消";
        }
        catch (IOException ex)
        {
            return $"反编译失败：IO 错误（{ex.Message}）";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"反编译失败：无访问权限（{ex.Message}）";
        }
        catch (BadImageFormatException ex)
        {
            return $"反编译失败：程序集格式无效（{ex.Message}）";
        }
        catch (Exception ex)
        {
            return $"反编译失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 判定文本是否为 InProcessDecompiler 生成的错误提示（而非反编译结果）。 供执行管道在写缓存前排除错误提示——错误提示不入缓存，
    /// 同 key 后续调用可重试。覆盖全部错误提示形态：反编译异常兜底、未找到类型、输出超限、非法/越界 token、反编译已取消。 超时提示（timeoutHint）
    /// 由调用方另行判定（本方法不命中该类文本），无需重复处理。新增错误提示时必须同步扩展本判定，否则会被管道误当正常结果写入缓存。
    /// </summary>
    /// <param name="text">反编译入口返回的文本。</param>
    /// <returns>是错误提示返回 true；反编译结果返回 false。</returns>
    internal static bool IsErrorResult(string text)
    {
        // 全部错误提示前缀：Execute/RunWithTimeoutAsync 的「反编译失败：」兜底、未找到类型、输出超限、
        // 「元数据 token …未引用…」越界、「反编译已取消」（引擎检查点中断）、以及以引号开头的非法 token 提示
        // （正常反编译文本不可能以这些开头）
        return text.StartsWith("反编译失败：", StringComparison.Ordinal)
            || text.StartsWith("反编译已取消", StringComparison.Ordinal)
            || text.StartsWith("未找到类型 ", StringComparison.Ordinal)
            || text.StartsWith("反编译输出超过上限", StringComparison.Ordinal)
            || text.StartsWith("元数据 token ", StringComparison.Ordinal)
            || text.StartsWith("\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// 文本输出超 <see cref="AppConfig.MaxOutputBytes"/> 字符数时返回改用写盘提示，否则原样返回。
    /// </summary>
    /// <param name="text">反编译生成的文本。</param>
    /// <returns>原文本或超限提示。</returns>
    private static string CheckOutputSize(string text)
    {
        return text.Length > AppConfig.MaxOutputBytes ? "反编译输出超过上限，建议改用 decompile_to_dir" : text;
    }

    /// <summary>
    /// 组装写盘成功提示：输出目录 + 文件数 + 来源程序集；文件枚举失败时退回不含文件数的提示。
    /// </summary>
    /// <param name="outputDir">输出目录。</param>
    /// <param name="assemblyPath">来源程序集。</param>
    /// <returns>成功提示文本。</returns>
    private static string BuildWriteSuccess(string outputDir, string assemblyPath)
    {
        try
        {
            var count = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Length;
            return $"已写入 {outputDir}（{count} 个文件，来源 {assemblyPath}）";
        }
        catch
        {
            return $"已写入 {outputDir}（来源 {assemblyPath}）";
        }
    }
}
