using DotNetDebugger.Decompiler.Document;
using DotNetDebugger.Decompiler.Metadata;
using DotNetDebugger.Engine.Session;
using DotNetDebugger.Session;
using DotNetDebuggerMcp.Formatting;
using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// 调试断点工具：设置/移除/清除断点。四种定位方式：① 模块名+方法 token+IL offset（未加载模块登记待绑定）；
/// ② typeName+memberName（类型内成员名子串，命中方法直接设断点）；③ typeName+反编译视图行（agent 看到的 decompile 输出行号）；
/// ④ sourcePath+PDB 源码行（顺堆栈行号断案发现场）。
/// </summary>
[McpServerToolType]
public static class DebugBreakpointTool
{
    /// <summary>行断点定位失败的通用指引（CI 实录：刚 launch 的目标模块可能尚未完成加载——attach 窗口竞速）。</summary>
    private const string LineBreakpointRetryHint =
        "刚 debug_launch 的目标其模块可能尚未加载完（attach 竞速窗口）——先 debug_continue 进入运行（目标带启动延迟则停在其窗口内）再设行断点，或用 methodToken 方式登记待绑定。";
    /// <summary>
    /// 设置断点：按 模块名 + 方法 token（0x06 开头）+ IL offset 定位，或 typeName+memberName 成员级、typeName+line 反编译行、sourcePath+line 源码行定位。模块已加载即绑定；
    /// 未加载登记为 pending（加载后自动绑定）。返回断点 id。
    /// </summary>
    /// <param name="moduleName">模块名（如 DebugTarget.dll）；token 方式必填；行/成员定位方式可省（省缺在已加载模块中解析）。</param>
    /// <param name="methodToken">方法 token（0x06000005，从反编译 signature 行尾取）；提供时按 token 定位（优先）。</param>
    /// <param name="ilOffset">IL offset，默认 0（方法入口）。</param>
    /// <param name="typeName">类型全名（与 decompile 输出同格式）；与 memberName 组合按成员级定位（方法名子串），或与 line 组合按反编译视图行定位（需模块已加载）。</param>
    /// <param name="memberName">成员名（方法，子串忽略大小写）；与 typeName 组合按成员级定位（line 忽略，命中方法即设断点）。</param>
    /// <param name="sourcePath">源文件路径（绝对/相对/仅文件名如 Program.cs）；与 line 组合按 PDB 源码行定位（模块旁需有 PDB）。</param>
    /// <param name="line">行号（1-based）：typeName 方式=decompile 输出行号；sourcePath 方式=源码行号。</param>
    /// <param name="hitCount">第 N 次命中起生效，默认 1（每次都停/记）。</param>
    /// <param name="mode">命中模式：stop（默认）=停下；trace=不停、记变量轨迹（经 debug_wait 取回）。</param>
    /// <param name="condition">P6 子集条件表达式，默认空=无条件；为真才停/记（语法错当场拒绝，求值失败计入未通过反馈）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>中文结果提示（断点 id）或错误提示。</returns>
    [McpServerTool]
    [Description("设置断点，四种定位方式：① token：moduleName+methodToken（0x06 开头，signature 行尾取）+ilOffset（未加载模块登记待绑定，加载后自动绑定）；② 成员：typeName+memberName（类型内方法名子串，忽略大小写，命中唯一方法即设断点；属性/事件/字段成员会提示；多方法匹配返回 #MEMBER 清单，取 methodToken 精确重设）；③ 反编译行：typeName+line（line 为 decompile 输出的行号，需模块已加载）；④ 源码行：sourcePath+line（源文件绝对/相对/仅文件名，按 PDB 序列点定位，需模块旁有 PDB；模块未加载/未命中时登记延迟项，模块加载后自动解析绑定）。memberName 提供时成员级优先（line 忽略）。可选 hitCount（第 N 次命中起生效）与 mode（stop=命中停 / trace=命中不停记轨迹，经 debug_wait 批量取回）。返回断点 id；设好后 debug_continue 运行至命中。")]
    public static async Task<string> DebugBreakpointSet(
        [Description("模块名（如 DebugTarget.dll）；token 方式必填；行/成员定位方式可省，省缺在已加载模块中解析。")] string moduleName = "",
        [Description("方法 token（0x06000005，从反编译 signature 行尾或 #MEMBER 取）；提供时优先按 token 定位。")] string methodToken = "",
        [Description("IL offset，默认 0（方法入口）。")] int ilOffset = 0,
        [Description("类型全名（与 decompile 输出同格式）；与 memberName 组合按成员级定位（方法名子串，命中方法即设断点），或与 line 组合按反编译视图行定位。")] string typeName = "",
        [Description("成员名（方法，子串忽略大小写，默认空=不启用）；与 typeName 组合按成员级定位——命中唯一方法即设断点（line 忽略）；属性/事件/字段成员会提示改用方法或访问器；多方法匹配返回 #MEMBER 清单，用返回的 methodToken 精确重设。")] string memberName = "",
        [Description("源文件路径（绝对/相对/仅文件名如 Program.cs）；与 line 组合按 PDB 源码行定位。")] string sourcePath = "",
        [Description("行号（1-based）：typeName 方式=decompile 输出行号；sourcePath 方式=源码行号；默认 0=未提供。")] int line = 0,
        [Description("开始生效的命中次数：第 N 次命中起每次都停/记录，默认 1=每次。")] int hitCount = 1,
        [Description("命中行为：stop=命中停进程（默认）；trace=命中不停、快照变量记轨迹（debug_wait/debug_state 批量取回，环形上限 100 条）。")] string mode = "stop",
        [Description("条件表达式（P6 子集：成员访问/索引/一元 !/单次比较），默认空=无条件。条件为真才停/记——Hits 只数条件为真次数；语法错当场拒绝；命中时求值失败（未知名/缺字段/非布尔）放行并计入 debug_state/debug_wait 的「条件未通过」反馈。如 i == 3、b.A > 0。")] string condition = "",
        CancellationToken cancellationToken = default)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null) return "当前无活动调试会话。先用 debug_launch / debug_attach 建立会话。";
        if (hitCount < 1) return "hitCount 须 ≥ 1（第 N 次命中起生效，默认 1=每次）。";
        var modeNorm = mode.Trim().ToLowerInvariant();
        if (modeNorm is not ("stop" or "trace")) return $"mode 无效：{mode}（可选 stop=命中停 / trace=记轨迹）。";
        var modeValue = modeNorm == "trace" ? DebugBreakpointMode.Trace : DebugBreakpointMode.Stop;

        // P7：条件语法 set 时校验（parser 纯语法可脱进程调）——「写错条件永不命中」在门口杀掉
        var conditionNorm = string.IsNullOrWhiteSpace(condition) ? null : condition.Trim();
        if (conditionNorm is not null)
        {
            try { ExpressionParser.Parse(conditionNorm); }
            catch (ExpressionEvaluationException ex) { return $"条件表达式无效，断点未设：{ex.Message}"; }
        }

        // E：memberName 定位优先于 sourcePath/typeName 行定位（memberName+line 同给时以成员级为准，line 忽略）
        if (!string.IsNullOrWhiteSpace(methodToken)) return await SetByTokenAsync(active, moduleName, methodToken, ilOffset, hitCount, modeValue, conditionNorm, cancellationToken);
        if (!string.IsNullOrWhiteSpace(memberName)) return await SetByMemberAsync(active, moduleName, typeName, memberName, hitCount, modeValue, conditionNorm, cancellationToken);
        if (!string.IsNullOrWhiteSpace(sourcePath)) return await SetBySourceLineAsync(active, moduleName, sourcePath, line, hitCount, modeValue, conditionNorm, cancellationToken);
        if (!string.IsNullOrWhiteSpace(typeName)) return await SetByTypeLineAsync(active, moduleName, typeName, line, hitCount, modeValue, conditionNorm, cancellationToken);
        return "请提供定位方式之一：methodToken（token 定位）、typeName+memberName（成员级）、typeName+line（反编译视图行）、sourcePath+line（PDB 源码行）。";
    }

    /// <summary>token 分支（现状语义）：模块必填；未加载登记 pending。</summary>
    private static async Task<string> SetByTokenAsync(DotNetDebugger.Session.ActiveDebugSession active, string moduleName, string methodToken, int ilOffset, int hitCount, DebugBreakpointMode modeValue, string? condition, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(moduleName)) return "请指定模块名（moduleName，token 定位方式必填）。";
        if (!TryParseToken(methodToken, out var token))
            return $"方法 token 格式无效：{methodToken}（应为 0x06000005 形式的十六进制）。";

        try
        {
            var bp = await active.Session.SetBreakpointAsync(moduleName, token, ilOffset, hitCount, modeValue, condition, ct);
            DebugSessionService.Manager.Actions.Log("debug_breakpoint_set", $"{moduleName} {methodToken}+{ilOffset}", $"id={bp.Id}");
            return DescribeSet(bp);
        }
        catch (Exception ex)
        {
            return $"设置断点失败：{ex.Message}";
        }
    }

    /// <summary>统一断点设置成功文案（绑定状态 + 模式/计数/条件标注）。</summary>
    private static string DescribeSet(DebugBreakpoint bp)
    {
        var modeTag = bp.Mode == DebugBreakpointMode.Trace
            ? $" [trace]（命中不停记轨迹，debug_wait/debug_state 批量取回；当前未读 {DebugSessionService.Manager.Active?.Buffer.PendingTraceCount ?? 0} 条）"
            : "";
        var hitTag = bp.HitCount > 1 ? $" [hitCount={bp.HitCount}，当前已命中 {bp.Hits}]" : "";
        var conditionTag = bp.Condition is not null ? $" [条件: {bp.Condition}——为真才停，命中 N 计条件为真次数]" : "";
        return bp.IsBound
            ? $"断点已设: id={bp.Id} 位置={bp}{modeTag}{hitTag}{conditionTag}。用 debug_continue 运行至命中。"
            : $"断点已登记: id={bp.Id} 位置={bp}{modeTag}{hitTag}{conditionTag}（模块 {bp.ModuleName} 尚未加载，加载后自动绑定）。" +
              "模块名需与已加载模块一致；绑定状态用 debug_breakpoint_list 查看。用 debug_continue 运行至命中。";
    }

    /// <summary>反编译视图行分支（P3-3a）：typeName 全名定位类型 → DocumentService 行映射 → token+IL → 断点。</summary>
    private static async Task<string> SetByTypeLineAsync(DotNetDebugger.Session.ActiveDebugSession active, string moduleName, string typeName, int line, int hitCount, DebugBreakpointMode modeValue, string? condition, CancellationToken ct)
    {
        if (line <= 0) return "请提供行号（line，1-based，decompile 输出行号）。";

        try
        {
            var (modules, singlePath, resolveError) = await ResolveModuleForLineAsync(active, moduleName);
            if (resolveError is not null) return resolveError;

            // 类型定位：显式模块 → 单模块内 FindTypes（0=未找到+相近名，>1=模块内歧义）；省缺 → 跨已加载模块扫描
            string module;
            string docPath;
            string fullName;
            if (singlePath is not null)
            {
                var typeHits = TypeFullNamesInModule(singlePath, typeName);
                if (typeHits.Count == 0)
                {
                    using var fs = File.OpenRead(singlePath);
                    using var pe = new PEReader(fs);
                    return MetadataNaming.BuildNotFoundMessage(pe.GetMetadataReader(), typeName);
                }
                if (typeHits.Count > 1)
                    return $"类型 {typeName} 有歧义，匹配：{string.Join("、", typeHits)}。请提供更精确的全名。";
                module = Path.GetFileName(singlePath);
                docPath = singlePath;
                fullName = typeHits[0];
            }
            else
            {
                var hits = modules.Select(m => (m.Name, m.Path, Names: TypeFullNamesInModule(m.Path, typeName)))
                    .Where(x => x.Names.Count > 0).ToList();
                if (hits.Count == 0)
                    return $"在已加载模块中未找到类型 {typeName}（已扫描：{string.Join("、", modules.Select(m => m.Name))}）。{LineBreakpointRetryHint}";
                if (hits.Count > 1)
                    return $"类型 {typeName} 在多个模块中命中，请提供 moduleName 消歧：{string.Join("；", hits.Select(h => $"{h.Name}: {string.Join("、", h.Names)}"))}";
                module = hits[0].Name;
                docPath = hits[0].Path;
                fullName = hits[0].Names[0];
            }

            var doc = DocumentService.GetTypeDocument(docPath, fullName);
            if (doc.Error is not null) return doc.Error;
            var target = DocumentService.GetBreakpointTargetAtLine(doc, line);
            if (target is null)
                return $"类型 {fullName} 第 {line} 行无法定位到语句（不在任何方法区间，如 usings/类声明行）。请改用 methodToken 方式。";

            var bp = await active.Session.SetBreakpointAsync(module, target.Value.MethodToken, target.Value.IlOffset, hitCount, modeValue, condition, ct);
            DebugSessionService.Manager.Actions.Log("debug_breakpoint_set", $"{fullName} line {line}", $"id={bp.Id}");
            var at = $"类型 {fullName} 第 {line} 行 → {module}!0x{target.Value.MethodToken:x8}+{target.Value.IlOffset}"
                     + (target.Value.Exact ? "" : "（该行无独立语句，落于方法首条语句）");
            return DescribeSetWithPosition(bp, at);
        }
        catch (Exception ex)
        {
            return $"按反编译行设置断点失败：{ex.Message}";
        }
    }

    /// <summary>成员级分支（E）：typeName 全名定位类型 → MemberResolver 类型内按方法名子串定位 → 0x06 方法 token → 断点。
    /// 解析规则：先 FindTypes 判歧义/未找到（仿 decompile_member LocateMembers——MemberResolver.FindMembers 内部 FindType 取首个候选会吞歧义）；
    /// 唯一候选类型全名再 FindMembers；token 按前缀过滤（字段 0x04/属性 0x17/事件 0x14 不能作函数断点——Bind 用 GetFunctionFromToken），
    /// 仅 0x06 方法为候选：唯一方法即设断点，多方法返回 #MEMBER 清单（methodToken 闭环重设）。</summary>
    private static async Task<string> SetByMemberAsync(DotNetDebugger.Session.ActiveDebugSession active, string moduleName, string typeName, string memberName, int hitCount, DebugBreakpointMode modeValue, string? condition, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return "请提供 typeName（成员级定位需类型全名，如 DebugTarget.Program；memberName 在其内定位方法）。";

        try
        {
            var (modules, singlePath, resolveError) = await ResolveModuleForLineAsync(active, moduleName);
            if (resolveError is not null) return resolveError;

            // 类型定位：与行分支同规则——显式模块 → 单模块内 FindTypes；省缺 → 跨已加载模块扫描消歧
            string module;
            string modulePath;
            string fullName;
            if (singlePath is not null)
            {
                var typeHits = TypeFullNamesInModule(singlePath, typeName);
                if (typeHits.Count == 0)
                {
                    using var fs = File.OpenRead(singlePath);
                    using var pe = new PEReader(fs);
                    return MetadataNaming.BuildNotFoundMessage(pe.GetMetadataReader(), typeName);
                }
                if (typeHits.Count > 1)
                    return $"类型 {typeName} 有歧义，匹配：{string.Join("、", typeHits)}。请提供更精确的全名。";
                module = Path.GetFileName(singlePath);
                modulePath = singlePath;
                fullName = typeHits[0];
            }
            else
            {
                var hits = modules.Select(m => (m.Name, m.Path, Names: TypeFullNamesInModule(m.Path, typeName)))
                    .Where(x => x.Names.Count > 0).ToList();
                if (hits.Count == 0)
                    return $"在已加载模块中未找到类型 {typeName}（已扫描：{string.Join("、", modules.Select(m => m.Name))}）。{LineBreakpointRetryHint}";
                if (hits.Count > 1)
                    return $"类型 {typeName} 在多个模块中命中，请提供 moduleName 消歧：{string.Join("；", hits.Select(h => $"{h.Name}: {string.Join("、", h.Names)}"))}";
                module = hits[0].Name;
                modulePath = hits[0].Path;
                fullName = hits[0].Names[0];
            }

            // 类型唯一候选：类型内按 memberName 子串定位成员（MemberMatch 无种类字段——按 token 前缀过滤：仅 0x06 方法可作断点）
            var search = MemberResolver.FindMembers(modulePath, fullName, memberName);
            var methodCandidates = search.Matches.Where(m => m.Token.StartsWith("0x06", StringComparison.OrdinalIgnoreCase)).ToList();
            if (methodCandidates.Count == 0)
            {
                // 无 0x06 候选：把唯一非方法命中/未找到区分成 agent 可行动的提示
                if (search.Matches.Count == 1)
                {
                    var only = search.Matches[0];
                    var kind = only.Token.StartsWith("0x04") ? "字段" : only.Token.StartsWith("0x17") ? "属性" : only.Token.StartsWith("0x14") ? "事件" : "成员";
                    return $"成员 {only.Name} 是{kind}，不能设方法断点（token {only.Token} 非 0x06 方法）。请改用：① 其所在方法定位（typeName+line 断方法体行），或 decompile_member 看访问器方法（get_/set_）token 后以 methodToken 设置；② memberName 改输入普通方法名。";
                }
                if (search.Matches.Count > 1)
                    return $"类型 {fullName} 中名称含 \"{memberName}\" 的 {search.Matches.Count} 个成员均非方法（属性/事件/字段），不能设方法断点——请改用 typeName+line 或 decompile_member 看访问器 token 后以 methodToken 设置。";
                var message = $"类型 {fullName} 中未找到名称含 \"{memberName}\" 的方法成员";
                if (search.SimilarNames.Count > 0) message += $"。相近成员：{string.Join("、", search.SimilarNames)}";
                return message + "。可 decompile_member 按成员名定位确认实际名称，或用 methodToken 方式。";
            }

            if (methodCandidates.Count == 1)
            {
                var match = methodCandidates[0];
                var token = ParseTokenRow(match.Token);
                var bp = await active.Session.SetBreakpointAsync(module, token, 0, hitCount, modeValue, condition, ct);
                DebugSessionService.Manager.Actions.Log("debug_breakpoint_set", $"{fullName}.{match.Name}", $"id={bp.Id}");
                return DescribeSetWithPosition(bp, $"类型 {fullName} 成员 {match.Name} → {module}!0x{token:x8}+0x0");
            }

            // 多方法匹配：返回 #MEMBER 签名清单（token 可闭环 methodToken 重设），不设断点
            return RenderMemberListing(modulePath, fullName, memberName, methodCandidates)
                + $"匹配 {methodCandidates.Count} 个方法成员（子串忽略大小写），未设断点——用返回的 methodToken 精确重设（或缩小 memberName）。";
        }
        catch (Exception ex)
        {
            return $"按成员定位设置断点失败：{ex.Message}";
        }
    }

    /// <summary>多方法匹配清单：复用 OutputFormatter.MemberLine 的 #MEMBER JSON 行格式（与 decompile_member 超限清单同形），
    /// 签名经 SignatureRenderer 渲染。方法句柄按 metadata token 取——token 唯一，足以解析出所属类型与方法体。</summary>
    private static string RenderMemberListing(string modulePath, string fullName, string memberName, IReadOnlyList<MemberMatch> matches)
    {
        var tokens = matches.Select(m => ParseTokenRow(m.Token)).ToHashSet();
        var lines = new List<string> { $"类型 {fullName} 中名称含 \"{memberName}\" 的方法成员匹配:" };
        using var fs = File.OpenRead(modulePath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        var seen = new HashSet<int>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (MetadataNaming.FullName(reader, type) != fullName) continue;
            foreach (var mh in type.GetMethods())
            {
                var row = MetadataTokens.GetToken(mh);
                if (!tokens.Contains(row) || !seen.Add(row)) continue;
                var method = reader.GetMethodDefinition(mh);
                var sig = SignatureRenderer.RenderMemberSignature(reader, type, method);
                var token = MetadataNaming.FormatToken(row);
                var name = reader.GetString(method.Name);
                lines.Add(OutputFormatter.MemberLine(name, token, sig, fullName));
            }
        }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>解析 "0x06000005" 文本为 int token（与 TryParseToken 同实现）。</summary>
    private static int ParseTokenRow(string tokenText)
        => TryParseToken(tokenText, out var t) ? t : 0;

    /// <summary>PDB 源码行分支（P3-3b）：sourcePath+line 经 PDB 序列点 → token+IL → 断点。
    /// R6：显式 moduleName 未加载 / 已加载模块全试不命中 → 登记源行延迟项（pending，模块加载后自动解析绑定）。</summary>
    private static async Task<string> SetBySourceLineAsync(DotNetDebugger.Session.ActiveDebugSession active, string moduleName, string sourcePath, int line, int hitCount, DebugBreakpointMode modeValue, string? condition, CancellationToken ct)
    {
        if (line <= 0) return "请提供行号（line，1-based，源码行号）。";

        try
        {
            var modules = await active.Session.GetModulesAsync(ct);
            var candidates = moduleName is not ""
                ? modules.Where(m => ModuleNameMatches(m, moduleName)).ToList()
                : modules.ToList();
            if (moduleName is not "" && candidates.Count == 0)
            {
                // R6：显式模块未加载 → 登记源行延迟项（moduleName 限定），模块加载后自动解析绑定
                var pending = await active.Session.SetSourceLineBreakpointAsync(sourcePath, line, moduleName, hitCount, modeValue, condition, ct);
                DebugSessionService.Manager.Actions.Log("debug_breakpoint_set", $"{sourcePath}:{line} → {moduleName}（未加载，延迟绑定）", $"id={pending.Id}");
                return DescribeSetWithPosition(pending, $"源 {sourcePath} 第 {line} 行 → 模块 {moduleName}（未加载，加载后按 PDB 自动解析绑定）");
            }

            var resolved = new List<(string Module, SourceLineResolver.SourceLineTarget Target)>();
            string? lastError = null;
            foreach (var m in candidates)
            {
                if (SourceLineResolver.Resolve(m.Path, sourcePath, line, out var err) is { } t)
                    resolved.Add((m.Name, t));
                else
                    lastError = err;
            }

            if (resolved.Count == 0)
            {
                // R6：已加载模块全试不命中（无此源文件/无 PDB）→ 登记源行延迟项（任意模块），
                // 后续模块加载时自动尝试解析（覆盖 launch 早期业务模块未加载场景）
                if (moduleName is "")
                {
                    var pending = await active.Session.SetSourceLineBreakpointAsync(sourcePath, line, "", hitCount, modeValue, condition, ct);
                    DebugSessionService.Manager.Actions.Log("debug_breakpoint_set", $"{sourcePath}:{line}（已加载模块未命中，延迟绑定）", $"id={pending.Id}");
                    return DescribeSetWithPosition(pending, $"源 {sourcePath} 第 {line} 行（已加载模块未命中，登记延迟——模块加载后按 PDB 自动解析绑定）")
                        + " " + (lastError ?? "");
                }
                return (lastError ?? "未能按源文件+行定位断点。") + LineBreakpointRetryHint;
            }
            if (resolved.Count > 1)
                return $"源文件 \"{sourcePath}\" 第 {line} 行在多个模块命中，请提供 moduleName 消歧：{string.Join("、", resolved.Select(r => r.Module))}";

            var (module, target) = resolved[0];
            var bp = await active.Session.SetBreakpointAsync(module, target.MethodToken, target.IlOffset, hitCount, modeValue, condition, ct);
            DebugSessionService.Manager.Actions.Log("debug_breakpoint_set", $"{sourcePath}:{line}", $"id={bp.Id}");
            var at = $"源 {sourcePath} 第 {line} 行 → {module}!0x{target.MethodToken:x8}+{target.IlOffset}"
                     + (target.ActualLine == line ? "" : $"（该行无独立语句，落于第 {target.ActualLine} 行）");
            return DescribeSetWithPosition(bp, at);
        }
        catch (Exception ex)
        {
            return $"按源码行设置断点失败：{ex.Message}";
        }
    }

    /// <summary>统一断点设置成功文案（自定义位置描述版：行断点分支用）。</summary>
    private static string DescribeSetWithPosition(DebugBreakpoint bp, string at)
    {
        var modeTag = bp.Mode == DebugBreakpointMode.Trace
            ? $" [trace]（命中不停记轨迹，debug_wait/debug_state 批量取回；当前未读 {DebugSessionService.Manager.Active?.Buffer.PendingTraceCount ?? 0} 条）"
            : "";
        var hitTag = bp.HitCount > 1 ? $" [hitCount={bp.HitCount}，当前已命中 {bp.Hits}]" : "";
        var conditionTag = bp.Condition is not null ? $" [条件: {bp.Condition}——为真才停，命中 N 计条件为真次数]" : "";
        return bp.IsBound
            ? $"断点已设: id={bp.Id} 位置={at}{modeTag}{hitTag}{conditionTag}。用 debug_continue 运行至命中。"
            : $"断点已登记: id={bp.Id} 位置={at}{modeTag}{hitTag}{conditionTag}（模块加载后自动绑定）。";
    }

    /// <summary>行定位的模块解析：显式给定 → 单模块（未加载报错）；省缺 → 全部已加载模块（调用方自行扫描/消歧）。</summary>
    private static async Task<(IReadOnlyList<(string Name, string Path)> Modules, string? SinglePath, string? Error)> ResolveModuleForLineAsync(
        DotNetDebugger.Session.ActiveDebugSession active, string moduleName)
    {
        var modules = await active.Session.GetModulesAsync(CancellationToken.None);
        if (moduleName is "")
        {
            if (modules.Count == 0) return (modules, null, $"当前会话无已加载模块，无法按行定位。{LineBreakpointRetryHint}");
            return (modules, null, null);
        }
        var hit = modules.FirstOrDefault(m => ModuleNameMatches(m, moduleName));
        if (hit.Name is null)
            return (modules, null, $"模块 {moduleName} 未加载（行/成员定位方式要求模块已加载）。已加载：{string.Join("、", modules.Select(m => m.Name))}；{LineBreakpointRetryHint}");
        return ([hit], hit.Path, null);
    }

    private static bool ModuleNameMatches((string Name, string Path) module, string moduleName)
        => module.Name.Equals(moduleName.Trim(), StringComparison.OrdinalIgnoreCase)
           || module.Path.Equals(moduleName.Trim(), StringComparison.OrdinalIgnoreCase);

    private static List<string> TypeFullNamesInModule(string modulePath, string typeName)
    {
        try
        {
            using var fs = File.OpenRead(modulePath);
            using var pe = new PEReader(fs);
            var reader = pe.GetMetadataReader();
            return MetadataNaming.FindTypes(reader, typeName)
                .Select(h => MetadataNaming.FullName(reader, reader.GetTypeDefinition(h)))
                .Distinct()
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>
    /// 列出当前会话全部断点（id、位置、绑定状态）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>断点清单或无断点提示。</returns>
    [McpServerTool]
    [Description("列出当前会话的全部断点（id、模块、方法 token、IL offset、绑定状态）。无断点返回提示。")]
    public static async Task<string> DebugBreakpointList(CancellationToken cancellationToken = default)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null) return "当前无活动调试会话。先用 debug_launch / debug_attach 建立会话。";

        var bps = await active.Session.GetBreakpointsAsync(cancellationToken);
        DebugSessionService.Manager.Actions.Log("debug_breakpoint_list", "", $"{bps.Count} 个");
        if (bps.Count == 0)
            return "当前无断点。用 debug_breakpoint_set 设置。";
        var pendingTraces = active.Buffer.PendingTraceCount;
        var lines = bps.Select(b =>
            "  id=" + b.Id + " "
            + (b.IsSourceLine && b.MethodToken == 0
                ? $"源行 {b.SourcePath}:{b.SourceLine}（{(string.IsNullOrEmpty(b.ModuleName) ? "任意模块" : $"限模块 {b.ModuleName}")}）"
                : $"{b.ModuleName}!0x{b.MethodToken:x8}+{b.IlOffset}")
            + (b.IsSourceLine && b.MethodToken == 0
                ? " 未绑定（源行待解析，模块加载后自动绑定）"
                : (b.IsBound ? " 已绑定" : " 未绑定（模块未加载，加载后自动绑定）"))
            + $" [{(b.Mode == DebugBreakpointMode.Trace ? "trace" : "stop")}]"
            + (b.HitCount > 1 ? $" 命中 {b.Hits}/{b.HitCount}" : (b.Condition is not null ? $" 条件为真 {b.Hits} 次" : ""))
            + (b.Condition is not null ? $" 条件: {b.Condition}" : "")
            + (b.Mode == DebugBreakpointMode.Trace && pendingTraces > 0 ? $" 未读轨迹 {pendingTraces} 条" : ""));
        return $"断点列表（{bps.Count} 个）:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
    }

    /// <summary>
    /// 移除指定断点。
    /// </summary>
    /// <param name="breakpointId">断点 id（debug_breakpoint_set 返回）（必填）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>中文结果提示。</returns>
    [McpServerTool]
    [Description("移除指定断点（id 由 debug_breakpoint_set 返回）。")]
    public static async Task<string> DebugBreakpointRemove(
        [Description("断点 id（debug_breakpoint_set 返回）（必填）。")] int breakpointId = 0,
        CancellationToken cancellationToken = default)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null) return "当前无活动调试会话。";
        if (breakpointId <= 0) return "请指定断点 id（breakpointId 必填）。";

        var removed = await active.Session.RemoveBreakpointAsync(breakpointId, cancellationToken);
        DebugSessionService.Manager.Actions.Log("debug_breakpoint_remove", breakpointId.ToString(), removed ? "ok" : "not-found");
        return removed ? $"断点 {breakpointId} 已移除。" : $"未找到断点 {breakpointId}。";
    }

    /// <summary>
    /// 清除全部断点。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>中文结果提示。</returns>
    [McpServerTool]
    [Description("清除当前会话的全部断点。")]
    public static async Task<string> DebugBreakpointClear(CancellationToken cancellationToken = default)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null) return "当前无活动调试会话。";

        await active.Session.ClearBreakpointsAsync(cancellationToken);
        DebugSessionService.Manager.Actions.Log("debug_breakpoint_clear", "", "ok");
        return "已清除全部断点。";
    }

    internal static bool TryParseToken(string text, out int token)
    {
        token = 0;
        var t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out token);
    }
}
