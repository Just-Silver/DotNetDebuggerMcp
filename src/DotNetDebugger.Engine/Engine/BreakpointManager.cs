using ClrDebug;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;

namespace DotNetDebugger.Engine.Engine;

/// <summary>
/// 断点管理：登记 DebugBreakpoint 并绑定到目标进程模块/函数/IL。模块加载事件时登记模块供按名查找
/// （v1：断点需模块已加载才能设，未加载抛中文提示）。
/// </summary>
public sealed class BreakpointManager
{
    private readonly List<DebugBreakpoint> _breakpoints = new();
    private readonly Dictionary<string, CorDebugModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISourceLineBreakpointResolver? _sourceLineResolver;
    private int _nextId = 1;

    public BreakpointManager(ISourceLineBreakpointResolver? sourceLineResolver = null) => _sourceLineResolver = sourceLineResolver;

    /// <summary>
    /// 模块加载时登记（供按名查找；Name 可能是全路径，归一化为文件名），并重绑该模块下
    /// 未绑定的 pending 断点：普通 token 断点直接 Bind；源行型断点先经 resolver 解析（若解析器注入）
    /// 出 token+IL 再 Bind。全部调用在引擎 MTA 线程（attach 枚举 / 命令泵 LoadModule），无并发。
    /// 返回本次重绑成功的断点数。
    /// </summary>
    public int TrackModule(CorDebugModule module)
    {
        string name;
        try
        {
            name = SafeName(module);
            // CorDebugModule.Name 实际返回全路径（spike 实测）；归一化为文件名 + 保留全路径双键
            _modules[Path.GetFileName(name)] = module;
            _modules[name] = module;
        }
        catch { return 0; /* 登记失败无从重绑 */ }

        var rebound = 0;
        var modulePath = SafeName(module); // 全路径（磁盘定位用）
        foreach (var bp in _breakpoints.Where(b => !b.IsBound
                && (b.IsSourceLine && b.MethodToken == 0 && string.IsNullOrEmpty(b.ModuleName) // 源行任意模块
                    || ModuleMatches(b.ModuleName, name))).ToList())
        {
            try
            {
                if (bp.IsSourceLine && bp.MethodToken == 0)
                {
                    // 源行型 pending：先解析出 token+IL（该模块 PDB 含此源文件才命中），再绑定；
                    // 失败保持 pending 等其它模块
                    if (TryBindSourceLine(bp, modulePath)) rebound++;
                }
                else
                {
                    Bind(bp, module);
                    rebound++;
                }
            }
            catch
            {
                // 重绑失败（token 无效/无 IL）：保持未绑定，agent 经 debug_breakpoint_list 可见，不阻塞进程
            }
        }
        return rebound;
    }

    public IReadOnlyList<DebugBreakpoint> Breakpoints => _breakpoints;

    /// <summary>按模块短名（文件名）或全路径反查模块全路径（CorDebugModule.Name 实际返回全路径）。
    /// 停点无条件跟随用：由停点模块名定位磁盘文件。未登记返回 null。</summary>
    public string? GetModulePath(string moduleName)
    {
        if (!_modules.TryGetValue(moduleName, out var module)) return null;
        try { return module.Name; } catch { return null; }
    }

    /// <summary>已加载模块快照（短名 → 磁盘路径，按路径去重；用户目标模块在前、路径稳定排序）。
    /// 行断点跨模块解析用（typeName/sourcePath 省缺 moduleName 时遍历）。MTA 单线程调用。</summary>
    public IReadOnlyList<(string Name, string Path)> GetModules()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string Name, string Path)>();
        foreach (var key in _modules.Keys)
        {
            var path = GetModulePath(key);
            if (string.IsNullOrEmpty(path) || !seen.Add(path!)) continue;
            list.Add((System.IO.Path.GetFileName(path!), path!));
        }
        return list.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 登记断点。模块未加载时登记为 pending（不绑定运行时，LoadModule 时 TrackModule 自动重绑）；
    /// 模块已加载时同步绑定，方法 token 无效/无 IL 抛中文 InvalidOperationException（agent 立即拿到原因）。
    /// P7：condition 非空 = 条件断点（P6 子集表达式；语法校验由宿主 set 时做，此处透传）。
    /// </summary>
    public DebugBreakpoint Add(string moduleName, int methodToken, int ilOffset, int hitCount = 1, DebugBreakpointMode mode = DebugBreakpointMode.Stop, string? condition = null)
    {
        var bp = new DebugBreakpoint(_nextId++, moduleName, methodToken, ilOffset, hitCount, mode, condition);
        if (!_modules.TryGetValue(moduleName, out var module))
        {
            _breakpoints.Add(bp); // pending：等模块加载后重绑
            return bp;
        }
        Bind(bp, module);
        _breakpoints.Add(bp);
        return bp;
    }

    /// <summary>
    /// 登记源行型断点（R6）：sourcePath+line 在模块未加载时登记为 pending（IsBound=false），
    /// 模块加载时 TrackModule 经 ISourceLineBreakpointResolver 解析出 token+IL 再绑定；
    /// 若 moduleName 指定则只在该模块加载时尝试，空串=任意模块（sharpdbg 语义）。
    /// 未注入 resolver 或模块始终不加载 → 保持 pending（agent 经 debug_breakpoint_list 可见）。
    /// </summary>
    public DebugBreakpoint AddSourceLine(string sourcePath, int line, string moduleName = "", int hitCount = 1, DebugBreakpointMode mode = DebugBreakpointMode.Stop, string? condition = null)
    {
        var bp = new DebugBreakpoint(_nextId++, moduleName, 0, 0, hitCount, mode, condition)
        {
            SourcePath = sourcePath,
            SourceLine = line,
        };
        _breakpoints.Add(bp);
        return bp;
    }

    /// <summary>是否未注入源行解析器（源行断点不可用）。</summary>
    public bool HasNoSourceLineResolver => _sourceLineResolver is null;

    /// <summary>
    /// 源行型 pending 断点在指定模块磁盘路径上解析并绑定（TrackModule/SetSourceLine 兜底共用）。
    /// 解析成功（该模块 PDB 含此源文件）→ 填充 ModuleName/MethodToken/IlOffset 并 Bind，返回 true；
    /// 解析失败/无解析器 → 保持 pending 返回 false。MTA 单线程调用。
    /// </summary>
    public bool TryBindSourceLine(DebugBreakpoint bp, string modulePath)
    {
        if (_sourceLineResolver is null || bp.IsBound || !bp.IsSourceLine || bp.MethodToken != 0) return false;
        if (!_modules.TryGetValue(Path.GetFileName(modulePath), out var module)
            && !_modules.TryGetValue(modulePath, out module)) return false;
        var resolved = _sourceLineResolver.Resolve(modulePath, bp.SourcePath!, bp.SourceLine);
        if (resolved is null) return false;
        try
        {
            bp.ModuleName = resolved.ModuleName;
            bp.MethodToken = resolved.MethodToken;
            bp.IlOffset = resolved.IlOffset;
            Bind(bp, module);
            return true;
        }
        catch { return false; /* 绑定失败（罕见）：保持 pending */ }
    }

    /// <summary>绑定断点到 模块/函数/IL。token 定位失败/无 IL 抛中文 InvalidOperationException。</summary>
    private static void Bind(DebugBreakpoint bp, CorDebugModule module)
    {
        CorDebugFunction fn;
        try
        {
            fn = module.GetFunctionFromToken(new mdMethodDef((uint)bp.MethodToken));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"方法 {bp.MethodToken:x8} 定位失败（{ex.Message}）；token 应为 0x06 开头的 mdMethodDef", ex);
        }
        var il = fn.ILCode
            ?? throw new InvalidOperationException($"方法 {bp.MethodToken:x8} 无 IL 代码（非 IL 方法或取 IL 失败）");
        var runtimeBp = il.CreateBreakpoint(bp.IlOffset);
        runtimeBp.Activate(true); // 关键：创建后必须 Activate 才生效（research/06 A.2）
        bp.RuntimeBreakpoint = runtimeBp;
    }

    public bool Remove(int id)
    {
        var bp = _breakpoints.FirstOrDefault(b => b.Id == id);
        if (bp is null) return false;
        try { bp.RuntimeBreakpoint?.Activate(false); } catch { }
        _breakpoints.Remove(bp);
        return true;
    }

    public void Clear()
    {
        foreach (var bp in _breakpoints)
        {
            try { bp.RuntimeBreakpoint?.Activate(false); } catch { }
        }
        _breakpoints.Clear();
    }

    /// <summary>
    /// 回调命中时匹配登记的断点。注意：不能按 RuntimeBreakpoint 引用比较——ClrDebug 的
    /// CorDebugBreakpoint.New 每次事件都新建 wrapper（源码核对），命中事件的 Breakpoint 是新的 wrapper 实例。
    /// 须按 模块 + 函数 token + IL offset 内容匹配（sharpdbg 用原生 COM 接口比较可行，wrapper 必须内容比较）。
    /// </summary>
    public DebugBreakpoint? Match(CorDebugFunctionBreakpoint hit)
    {
        var hitFunction = hit.Function;
        if (hitFunction is null) return null;
        var hitToken = hitFunction.Token.Value;
        var hitOffset = hit.Offset;
        string? hitModule = null;
        try { hitModule = Path.GetFileName(hitFunction.Module?.Name); } catch { /* 取不到模块名退化为不校验 */ }
        return MatchContent(hitModule, hitToken, hitOffset);
    }

    /// <summary>内容匹配：模块文件名（null=取不到时退化为不校验模块）+ 方法 token + IL offset；未绑定断点永不命中。internal 供测试。</summary>
    internal DebugBreakpoint? MatchContent(string? moduleFile, uint token, int ilOffset)
        => _breakpoints.FirstOrDefault(b =>
            b.RuntimeBreakpoint is not null
            && (uint)b.MethodToken == token
            && b.IlOffset == ilOffset
            && (moduleFile is null || ModuleMatches(b.ModuleName, moduleFile)));

    /// <summary>断点模块名与运行时模块名匹配：文件名或全路径，忽略大小写。internal 供测试。</summary>
    internal static bool ModuleMatches(string breakpointModule, string moduleFullName)
        => string.Equals(breakpointModule, moduleFullName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(breakpointModule, Path.GetFileName(moduleFullName), StringComparison.OrdinalIgnoreCase);

    private static string SafeName(CorDebugModule m)
    {
        try { return m.Name; } catch { return "<unknown>"; }
    }
}
