using System.Text.Json;

namespace DotNetDebuggerMcp.Services;

/// <summary>场景解析失败（中文提示直接面 agent；含场景路径 / 步骤号 / 字段名线索）。</summary>
internal sealed class VerifyFormatException : Exception
{
    public VerifyFormatException(string message) : base(message) { }
}

/// <summary>步骤类型。breakpoint/continue/assert 为 v1 实现步骤；uiAction/uiAssert 为 U1A（驱动 UI/断言 UI 状态）；ui/set 预留（Requires 标依赖，运行时报告未就绪）。</summary>
internal enum VerifyStepKind { Breakpoint, Continue, Assert, Ui, Set, UiAction, UiAssert }

/// <summary>断言原语（assert 步骤的 kind）。</summary>
internal enum VerifyAssertKind { BreakpointHit, Evaluate, Output, State, NoException }

/// <summary>目标启动快照（debug_launch 可复现参数）。</summary>
internal sealed class VerifyTarget
{
    /// <summary>可执行文件路径（可含参数，空格分隔）；相对 server 当前工作目录。</summary>
    public required string CommandLine { get; init; }

    /// <summary>目标进程工作目录（空=exe 所在目录）。</summary>
    public string WorkingDirectory { get; init; } = "";

    /// <summary>附加环境变量（KEY=VALUE 多行或分号分隔）。</summary>
    public string Environment { get; init; } = "";
}

/// <summary>可选重编译配置（只重编场景指定工程，产物走项目默认输出路径，不设置 OutputPath）。</summary>
internal sealed class VerifyBuild
{
    /// <summary>工程文件（.csproj 等）绝对路径。</summary>
    public required string Project { get; init; }

    /// <summary>编译配置（默认 Debug）。</summary>
    public string Configuration { get; init; } = "Debug";

    /// <summary>编译超时秒数（默认 120）。</summary>
    public int TimeoutSeconds { get; init; } = 120;
}

/// <summary>
/// 一条场景步骤（区分字段按 Kind 生效；详见各字段注释）。ui/set 为预留步骤：解析通过但标记
/// <see cref="Requires"/>（U1/W1），运行期执行器报告「步骤类型依赖未就绪」。
/// </summary>
internal sealed class VerifyStep
{
    public required VerifyStepKind Kind { get; init; }

    // ---- breakpoint ----
    /// <summary>类型全名（如 DebugTarget.Program）。</summary>
    public string TypeName { get; init; } = "";

    /// <summary>成员名（类型内方法名子串，命中唯一方法）。</summary>
    public string MemberName { get; init; } = "";

    /// <summary>第 N 次命中起生效（默认 1）。</summary>
    public int Hit { get; init; } = 1;

    // ---- continue ----
    /// <summary>等停点秒数（默认 10，范围 1-300）。</summary>
    public int WaitSeconds { get; init; } = 10;

    // ---- assert ----
    public VerifyAssertKind AssertKind { get; init; }

    /// <summary>assert breakpointHit：引用场景内第 N 个 breakpoint 步骤（0-based 按出现顺序）。</summary>
    public int BreakpointIndex { get; init; } = -1;

    /// <summary>assert evaluate：表达式路径（如 n、b.A、scores[2]）。</summary>
    public string Path { get; init; } = "";

    /// <summary>assert evaluate equals：期望等于（与 contains 互斥；字符串比较字面值不带引号）。</summary>
    public string EqualsText { get; init; } = "";

    /// <summary>assert evaluate/output contains：期望展示文本含子串（忽略大小写；evaluate 与 equals 互斥）。</summary>
    public string ContainsText { get; init; } = "";

    /// <summary>assert output stream 过滤：out / err（空=全部）。</summary>
    public string Stream { get; init; } = "";

    /// <summary>assert state expect：会话状态（Stopped/Exited/…，逗号分隔=任一命中）。</summary>
    public string Expect { get; init; } = "";

    // ---- uiAction / uiAssert（U1A）----
    /// <summary>uiAction/uiAssert：目标进程（pid 或进程名子串）。</summary>
    public string Process { get; init; } = "";

    /// <summary>uiAction：语义动词（invoke/toggle/…/windowstate；input=走 ui_input 写值）。</summary>
    public string Verb { get; init; } = "";

    /// <summary>uiAction/uiAssert：上次 ui_find 序号（&gt;=0 优先；默认 -1 用 name/type）。</summary>
    public int UiIndex { get; init; } = -1;

    /// <summary>uiAction/uiAssert：控件名/文本子串。</summary>
    public string UiName { get; init; } = "";

    /// <summary>uiAction/uiAssert：控件类型。</summary>
    public string UiType { get; init; } = "";

    /// <summary>uiAction（scroll）：up/down。</summary>
    public string Direction { get; init; } = "";

    /// <summary>uiAction（scroll）：行数（0=默认）。</summary>
    public int Lines { get; init; }

    /// <summary>uiAction（windowstate）：normal/maximized/minimized。</summary>
    public string WindowState { get; init; } = "";

    /// <summary>uiAction（verb=input）：要写入的值。</summary>
    public string UiValue { get; init; } = "";

    /// <summary>uiAssert：读取的状态（value/name/toggle/…）。</summary>
    public string What { get; init; } = "";

    // ---- ui / set（预留）----
    /// <summary>预留步骤的依赖能力代号（ui→U1、set→W1）；已实现步骤为空。</summary>
    public string Requires { get; init; } = "";

    /// <summary>步骤在场景中的位置（1-based，日志/错误引用）。</summary>
    public int Index { get; init; }
}

/// <summary>
/// debug_verify 场景模型 + JSON 解析（System.Text.Json JsonDocument 手解析，中文错误含 路径/步骤号/字段 上下文）。
/// 语法：{ "name"?, "target": { commandLine(必填), workingDirectory?, environment? }, "build"?: { project, configuration?, timeoutSeconds? },
/// "steps": [ {breakpoint|continue|assert|uiAction|uiAssert|ui|set}... ] }。断言原语 kind ∈ breakpointHit/evaluate/output/state/noException；
/// uiAction 驱动 U1A 语义动作（verb=input 走写值），uiAssert 断言 UI 状态（what + equals/contains）。
/// </summary>
internal sealed class VerifyScenario
{
    public string Name { get; init; } = "";
    public required VerifyTarget Target { get; init; }
    public VerifyBuild? Build { get; init; }
    public required IReadOnlyList<VerifyStep> Steps { get; init; }

    /// <summary>场景内 breakpoint 步骤总数（assert breakpointIndex 的 0-based 引用域）。</summary>
    public int BreakpointCount { get; init; }

    /// <summary>解析场景 JSON 文件；失败抛 <see cref="VerifyFormatException"/>（中文提示，面 agent）。</summary>
    public static VerifyScenario Parse(string path)
    {
        if (!File.Exists(path))
            throw new VerifyFormatException($"场景文件不存在：{path}（debug_verify 需场景 JSON 文件路径）。");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line
                ? $"（第 {line} 行，第 {(ex.BytePositionInLine + 1) ?? 0} 列）"
                : "";
            throw new VerifyFormatException($"场景文件 {path} JSON 解析失败{where}：{ex.Message}。");
        }
        catch (Exception ex)
        {
            throw new VerifyFormatException($"场景文件读取失败：{ex.Message}（{path}）。");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new VerifyFormatException("场景根必须是 JSON 对象（含 target 与 steps）。");

            var name = GetString(root, "name") ?? "";
            var target = ParseTarget(root, name);
            var build = ParseBuild(root, name);

            if (!root.TryGetProperty("steps", out var stepsEl))
                throw new VerifyFormatException($"场景 {name} 缺少 steps（步骤数组）。".Trim());
            if (stepsEl.ValueKind != JsonValueKind.Array)
                throw new VerifyFormatException($"场景 {name} 的 steps 必须是数组。".Trim());

            var steps = new List<VerifyStep>();
            var breakpointCount = 0;
            var index = 0;
            foreach (var element in stepsEl.EnumerateArray())
            {
                index++;
                if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != 1)
                    throw new VerifyFormatException($"场景 {name} 第 {index} 步必须是单键对象（步骤类型：breakpoint/continue/assert/uiAction/uiAssert/ui/set）。");
                var property = element.EnumerateObject().First();
                var (step, isBreakpoint) = ParseStep(property.Name, property.Value, name, index);
                steps.Add(step);
                if (isBreakpoint) breakpointCount++;
            }

            // 断点引用越界检查：breakpointHit 断言引用的 index 须 < breakpointCount（解析在全部步骤后做，引用前后顺序均可）
            foreach (var step in steps)
            {
                if (step.Kind == VerifyStepKind.Assert && step.AssertKind == VerifyAssertKind.BreakpointHit
                    && (step.BreakpointIndex < 0 || step.BreakpointIndex >= breakpointCount))
                    throw new VerifyFormatException(
                        $"场景 {name} 第 {step.Index} 步 assert.breakpointIndex={step.BreakpointIndex} 越界：场景共 {breakpointCount} 个 breakpoint 步骤（0-based 按出现顺序），越界说明场景无对应断点步骤或引用序数有误。");
            }

            return new VerifyScenario
            {
                Name = name,
                Target = target,
                Build = build,
                Steps = steps,
                BreakpointCount = breakpointCount,
            };
        }
    }

    private static VerifyTarget ParseTarget(JsonElement root, string name)
    {
        if (!root.TryGetProperty("target", out var targetEl))
            throw new VerifyFormatException($"场景 {name} 缺少 target（目标启动快照，commandLine 必填）。");
        if (targetEl.ValueKind != JsonValueKind.Object)
            throw new VerifyFormatException($"场景 {name} 的 target 必须是对象。");
        var commandLine = RequiredString(targetEl, "commandLine", $"场景 {name} 的 target.commandLine 必填（目标可执行文件路径，可含参数）。");
        return new VerifyTarget
        {
            CommandLine = commandLine,
            WorkingDirectory = GetString(targetEl, "workingDirectory") ?? "",
            Environment = GetString(targetEl, "environment") ?? "",
        };
    }

    private static VerifyBuild? ParseBuild(JsonElement root, string name)
    {
        if (!root.TryGetProperty("build", out var buildEl))
            return null;
        if (buildEl.ValueKind != JsonValueKind.Object)
            throw new VerifyFormatException($"场景 {name} 的 build 必须是对象（project/configuration/timeoutSeconds）。");
        var project = RequiredString(buildEl, "project", $"场景 {name} 的 build.project 必填（要编译的工程文件路径）。");
        var timeout = GetInt(buildEl, "timeoutSeconds") ?? 120;
        if (timeout <= 0 || timeout > 3600)
            throw new VerifyFormatException($"场景 {name} 的 build.timeoutSeconds 须在 1-3600 之间（默认 120）。");
        return new VerifyBuild
        {
            Project = project,
            Configuration = GetString(buildEl, "configuration") ?? "Debug",
            TimeoutSeconds = timeout,
        };
    }

    /// <summary>解析单个步骤；返回 (步骤, 是否 breakpoint 步骤)。ui/set 解析通过并标记 Requires。</summary>
    private static (VerifyStep Step, bool IsBreakpoint) ParseStep(string kind, JsonElement value, string name, int index)
    {
        var where = $"场景 {name} 第 {index} 步";
        switch (kind)
        {
            case "breakpoint":
            {
                var typeName = RequiredString(value, "typeName", $"{where} breakpoint 缺少 typeName（类型全名，如 DebugTarget.Program）。");
                var memberName = RequiredString(value, "memberName", $"{where} breakpoint 缺少 memberName（类型内方法名子串）。");
                var hit = GetInt(value, "hit") ?? 1;
                if (hit < 1)
                    throw new VerifyFormatException($"{where} breakpoint.hit 须 ≥ 1（第 N 次命中起生效，默认 1）。");
                return (new VerifyStep
                {
                    Kind = VerifyStepKind.Breakpoint,
                    Index = index,
                    TypeName = typeName,
                    MemberName = memberName,
                    Hit = hit,
                }, true);
            }
            case "continue":
            {
                var wait = GetInt(value, "waitSeconds") ?? 10;
                if (wait is < 0 or > 300)
                    throw new VerifyFormatException($"{where} continue.waitSeconds 须在 0-300 之间（默认 10；0=放行不等停点，供后续 uiAction 在目标运行中驱动 UI）。");
                return (new VerifyStep { Kind = VerifyStepKind.Continue, Index = index, WaitSeconds = wait }, false);
            }
            case "assert":
            {
                var step = ParseAssertStep(value, name, index, where);
                return (step, false);
            }
            case "uiAction":
                return (ParseUiActionStep(value, name, index, where), false);
            case "uiAssert":
                return (ParseUiAssertStep(value, name, index, where), false);
            case "ui":
                return (new VerifyStep { Kind = VerifyStepKind.Ui, Index = index, Requires = "U1" }, false);
            case "set":
                return (new VerifyStep { Kind = VerifyStepKind.Set, Index = index, Requires = "W1" }, false);
            default:
                throw new VerifyFormatException($"{where} 未知步骤类型 \"{kind}\"（支持：breakpoint/continue/assert/uiAction/uiAssert/ui/set）。");
        }
    }

    /// <summary>解析 uiAction 步骤（process+verb 必填；定位 index/name/type + 可选 direction/lines/windowstate/value）。</summary>
    private static VerifyStep ParseUiActionStep(JsonElement value, string name, int index, string where)
    {
        var process = RequiredString(value, "process", $"{where} uiAction 缺少 process（目标进程 pid 或进程名子串）。");
        var verb = RequiredString(value, "verb", $"{where} uiAction 缺少 verb（invoke/toggle/select/expand/collapse/focus/scroll/scrollintoview/windowstate；input=写值）。");
        var lines = GetInt(value, "lines") ?? 0;
        if (lines is < 0 or > 100)
            throw new VerifyFormatException($"{where} uiAction.lines 须在 0-100 之间（默认 0）。");
        return new VerifyStep
        {
            Kind = VerifyStepKind.UiAction,
            Index = index,
            Process = process,
            Verb = verb,
            UiIndex = GetInt(value, "index") ?? -1,
            UiName = GetString(value, "name") ?? "",
            UiType = GetString(value, "type") ?? "",
            Direction = GetString(value, "direction") ?? "",
            Lines = lines,
            WindowState = GetString(value, "windowstate") ?? "",
            UiValue = GetString(value, "value") ?? "",
        };
    }

    /// <summary>解析 uiAssert 步骤（process+what 必填；定位 index/name/type；equals/contains 互斥二选一）。</summary>
    private static VerifyStep ParseUiAssertStep(JsonElement value, string name, int index, string where)
    {
        var process = RequiredString(value, "process", $"{where} uiAssert 缺少 process（目标进程 pid 或进程名子串）。");
        var what = RequiredString(value, "what", $"{where} uiAssert 缺少 what（value/name/toggle/selected/expandstate/rangevalue/enabled/offscreen/rect/helptext）。");
        var equals = GetString(value, "equals");
        var contains = GetString(value, "contains");
        var hasEquals = !string.IsNullOrEmpty(equals);
        var hasContains = !string.IsNullOrEmpty(contains);
        if (hasEquals && hasContains)
            throw new VerifyFormatException($"{where} uiAssert 的 equals 与 contains 互斥——只能二选一。");
        if (!hasEquals && !hasContains)
            throw new VerifyFormatException($"{where} uiAssert 须二选一给 equals 或 contains（当前都没给）。");
        return new VerifyStep
        {
            Kind = VerifyStepKind.UiAssert,
            Index = index,
            Process = process,
            What = what,
            UiIndex = GetInt(value, "index") ?? -1,
            UiName = GetString(value, "name") ?? "",
            UiType = GetString(value, "type") ?? "",
            EqualsText = hasEquals ? equals! : "",
            ContainsText = hasContains ? contains! : "",
        };
    }

    private static VerifyStep ParseAssertStep(JsonElement value, string name, int index, string where)
    {
        var kindText = RequiredString(value, "kind", $"{where} assert.kind 必填（支持：breakpointHit/evaluate/output/state/noException）。");
        switch (kindText)
        {
            case "breakpointHit":
            {
                var bpIndex = GetInt(value, "breakpointIndex")
                    ?? throw new VerifyFormatException($"{where} assert（breakpointHit）缺少 breakpointIndex（场景内第 N 个 breakpoint 步骤，0-based）。");
                return new VerifyStep
                {
                    Kind = VerifyStepKind.Assert,
                    Index = index,
                    AssertKind = VerifyAssertKind.BreakpointHit,
                    BreakpointIndex = bpIndex,
                };
            }
            case "evaluate":
            {
                var path = RequiredString(value, "path", $"{where} assert（evaluate）缺少 path（表达式，如 n、b.A、scores[2]）。");
                var equals = GetString(value, "equals");
                var contains = GetString(value, "contains");
                var hasEquals = !string.IsNullOrEmpty(equals);
                var hasContains = !string.IsNullOrEmpty(contains);
                if (hasEquals && hasContains)
                    throw new VerifyFormatException($"{where} assert（evaluate）的 equals 与 contains 互斥——只能二选一。");
                if (!hasEquals && !hasContains)
                    throw new VerifyFormatException($"{where} assert（evaluate）须二选一给 equals 或 contains（当前都没给）。");
                return new VerifyStep
                {
                    Kind = VerifyStepKind.Assert,
                    Index = index,
                    AssertKind = VerifyAssertKind.Evaluate,
                    Path = path,
                    EqualsText = hasEquals ? equals! : "",
                    ContainsText = hasContains ? contains! : "",
                };
            }
            case "output":
            {
                var contains = RequiredString(value, "contains", $"{where} assert（output）缺少 contains（v1 只支持 contains 断言）。");
                var stream = GetString(value, "stream") ?? "";
                if (stream is not ("" or "out" or "err"))
                    throw new VerifyFormatException($"{where} assert（output）.stream 只支持 out / err（缺省=全部）。");
                return new VerifyStep
                {
                    Kind = VerifyStepKind.Assert,
                    Index = index,
                    AssertKind = VerifyAssertKind.Output,
                    ContainsText = contains,
                    Stream = stream,
                };
            }
            case "state":
            {
                var expect = RequiredString(value, "expect", $"{where} assert（state）缺少 expect（如 Stopped / Exited，逗号分隔=任一命中）。");
                return new VerifyStep
                {
                    Kind = VerifyStepKind.Assert,
                    Index = index,
                    AssertKind = VerifyAssertKind.State,
                    Expect = expect,
                };
            }
            case "noException":
                return new VerifyStep { Kind = VerifyStepKind.Assert, Index = index, AssertKind = VerifyAssertKind.NoException };
            default:
                throw new VerifyFormatException($"{where} assert.kind={kindText} 未知断言类型（支持：breakpointHit/evaluate/output/state/noException）。");
        }
    }

    private static string? GetString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int? GetInt(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var el)) return null;
        return el.ValueKind is JsonValueKind.Number && el.TryGetInt32(out var value) ? value : null;
    }

    private static string RequiredString(JsonElement obj, string property, string error)
    {
        var value = GetString(obj, property);
        if (string.IsNullOrWhiteSpace(value)) throw new VerifyFormatException(error);
        return value!;
    }
}
