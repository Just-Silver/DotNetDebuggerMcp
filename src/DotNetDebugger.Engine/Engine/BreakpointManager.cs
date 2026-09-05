using ClrDebug;
using DotNetDebugger.Engine.Models;

namespace DotNetDebugger.Engine.Engine;

/// <summary>
/// 断点管理：登记 DebugBreakpoint 并绑定到目标进程模块/函数/IL。模块加载事件时登记模块供按名查找
/// （v1：断点需模块已加载才能设，未加载抛中文提示）。
/// </summary>
public sealed class BreakpointManager
{
    private readonly List<DebugBreakpoint> _breakpoints = new();
    private readonly Dictionary<string, CorDebugModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId = 1;

    /// <summary>模块加载时登记（供按名查找；Name 可能是全路径，归一化为文件名）。</summary>
    public void TrackModule(CorDebugModule module)
    {
        try
        {
            var name = SafeName(module);
            // CorDebugModule.Name 实际返回全路径（spike 实测）；归一化为文件名 + 保留全路径双键
            var fileName = Path.GetFileName(name);
            _modules[fileName] = module;
            _modules[name] = module;
        }
        catch { /* 登记失败忽略 */ }
    }

    public IReadOnlyList<DebugBreakpoint> Breakpoints => _breakpoints;

    /// <summary>登记并绑定断点。模块未加载/方法无 IL 抛中文 InvalidOperationException。</summary>
    public DebugBreakpoint Add(string moduleName, int methodToken, int ilOffset)
    {
        if (!_modules.TryGetValue(moduleName, out var module))
        {
            throw new InvalidOperationException(
                $"模块 {moduleName} 尚未加载（断点需模块加载后才能设置；先运行到该模块加载，或用反编译工具确认模块名）");
        }

        var bp = new DebugBreakpoint(_nextId++, moduleName, methodToken, ilOffset);
        CorDebugFunction fn;
        try
        {
            fn = module.GetFunctionFromToken(new mdMethodDef((uint)methodToken));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"方法 {methodToken:x8} 定位失败（{ex.Message}）；token 应为 0x06 开头的 mdMethodDef", ex);
        }
        var il = fn.ILCode
            ?? throw new InvalidOperationException($"方法 {methodToken:x8} 无 IL 代码（可能未 JIT 或非 IL 方法，先运行到该方法再设断点）");
        var runtimeBp = il.CreateBreakpoint(ilOffset);
        runtimeBp.Activate(true); // 关键：创建后必须 Activate 才生效（research/06 A.2）
        bp.RuntimeBreakpoint = runtimeBp;
        _breakpoints.Add(bp);
        return bp;
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
    /// 须按 函数 token + IL offset 内容匹配（sharpdbg 用原生 COM 接口比较可行，wrapper 必须内容比较）。
    /// </summary>
    public DebugBreakpoint? Match(CorDebugFunctionBreakpoint hit)
    {
        var hitFunction = hit.Function;
        if (hitFunction is null) return null;
        var hitToken = hitFunction.Token.Value;
        var hitOffset = hit.Offset;
        return _breakpoints.FirstOrDefault(b =>
            b.RuntimeBreakpoint is not null
            && (uint)b.MethodToken == hitToken
            && b.IlOffset == hitOffset);
    }

    private static string SafeName(CorDebugModule m)
    {
        try { return m.Name; } catch { return "<unknown>"; }
    }
}
