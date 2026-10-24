using SharpSight.UiAutomation;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// 物理输入面收敛护栏（确定性，CI 必过/必败）：
/// ① 扫描宿主与 UI 自动化库源码树 <c>src/DotNetDebuggerMcp/**/*.cs</c> + <c>src/SharpSight.UiAutomation/**/*.cs</c>，
///    禁止出现原生光标/输入注入 API token；
/// ② 扫描构建出的宿主与 <c>SharpSight.UiAutomation.dll</c> 程序集的 IL 元数据（TypeRef/MemberRef/PInvoke import），
///    只放行失焦提交用的键盘按键与真实点击前的窗口激活。
/// <para><b>策略</b>：UI 自动化按「共享桌面、功能优先」运行，允许为完成业务动作短暂接管焦点；但仍禁止手写
/// <c>SendInput</c>/<c>SetCursorPos</c>/直接 <c>Mouse</c> 调用——真正需要真实点击时必须经 <c>ui_action clickPoint=true</c>
/// 显式开关，且调用点唯一（<see cref="RealClickCallSite_IsUniqueAndGuarded"/>）。</para>
/// （测试文件自身含负向 token 字符串，故只扫 src/ 与宿主产物，不扫测试源码。）
/// </summary>
public sealed class NoPhysicalInputGuardTests
{
    /// <summary>仍然禁止的 API/token（源码与 IL 共用语义）：原生光标移动、输入注入、以及绕过 clickPoint 的鼠标调用。</summary>
    internal static readonly string[] ForbiddenTokens =
    [
        "SetCursorPos",
        "SendInput",
        "ActivateWindow",
        "PerformPhysicalMouse",
    ];

    /// <summary>需按文件放行的受控 token（默认禁止，只在其白名单文件内合法）。</summary>
    private static readonly string[] GuardedTokens = ["FlaUI.Core.Input", "SetForegroundWindow", "Mouse."];

    /// <summary>
    /// 受控物理输入白名单：只允许出现在指定文件的指定 token。
    /// <para><c>BindingCommit</c> 用一次 Tab 触发 LostFocus，<c>UiPatternDispatcher</c> 用 <c>SetForegroundWindow</c>
    /// 把目标窗口激活（否则真实点击打在遮挡窗口/被激活动作吃掉）。</para>
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedTokensByFile = new(StringComparer.OrdinalIgnoreCase)
    {
        [Path.Combine("SharpSight.UiAutomation", "UiPatternDispatcher.cs")] = ["SetForegroundWindow"],
        [Path.Combine("SharpSight.UiAutomation", "BindingCommit.cs")] = ["FlaUI.Core.Input", "Mouse."],
    };

    /// <summary>真实点击入口 token（P3）：只允许出现在 <c>UiPatternDispatcher.PerformClickPoint</c> 内。</summary>
    private static readonly string[] RealClickTokens = [".Click(", ".DoubleClick(", ".RightClick(", ".RightDoubleClick("];

    [Fact]
    public void HostSource_ContainsNoUnsactionedPhysicalInputTokens()
    {
        // 物理输入敏感面（UIA 定位/动作）在 SharpSight.UiAutomation——源码扫描覆盖两个源码树。
        var roots = new[]
        {
            Path.Combine(TestDataPaths.RepositoryRoot, "src", "DotNetDebuggerMcp"),
            Path.Combine(TestDataPaths.RepositoryRoot, "src", "SharpSight.UiAutomation"),
        };
        foreach (var root in roots)
            Assert.True(Directory.Exists(root), $"源码目录不存在：{root}");

        var files = roots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(f => !IsBuildOutput(f))
            .ToList();
        Assert.NotEmpty(files);

        var violations = new List<string>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(TestDataPaths.RepositoryRoot, file);
            var allowed = AllowedTokensByFile
                .FirstOrDefault(kv => relative.EndsWith(kv.Key, StringComparison.OrdinalIgnoreCase)).Value ?? [];
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;   // 注释里的 API 名不算调用点
                foreach (var token in ForbiddenTokens)
                {
                    if (lines[i].Contains(token, StringComparison.Ordinal))
                        violations.Add($"{relative}:{i + 1}: [{token}] {lines[i].Trim()}");
                }
                foreach (var token in GuardedTokens)
                {
                    if (!lines[i].Contains(token, StringComparison.Ordinal)) continue;
                    if (allowed.Contains(token, StringComparer.Ordinal)) continue;
                    violations.Add($"{relative}:{i + 1}: [{token}] {lines[i].Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "宿主与 SharpSight.UiAutomation 源码不得含原生光标/输入注入 API；发现：\n" + string.Join("\n", violations));
    }

    [Fact]
    public void HostAssemblyMetadata_ReferencesNoUnsactionedPhysicalInputApis()
    {
        var assemblies = new[]
        {
            typeof(Tools.Debugger.UiTools).Assembly,
            typeof(UiAutomationService).Assembly,
        };

        var violations = new List<string>();
        foreach (var assembly in assemblies)
        {
            var location = assembly.Location;
            Assert.True(File.Exists(location), $"程序集不存在：{location}");
            violations.AddRange(ScanMetadata(location));
        }

        Assert.True(violations.Count == 0,
            "程序集 IL 不得引用原生光标/输入注入 API（失焦提交的键盘与窗口激活已放行）；发现：\n"
            + string.Join("\n", violations));
    }

    private static bool IsBuildOutput(string file)
    {
        var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Contains("bin", StringComparer.OrdinalIgnoreCase)
            || parts.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// P3 护栏：真实点击调用点（<c>.Click(</c>/<c>.DoubleClick(</c>/<c>.RightClick(</c>/<c>.RightDoubleClick(</c>）必须**只**
    /// 出现在 <c>UiPatternDispatcher.PerformClickPoint</c> 方法体内——否则任何人可绕过 <c>clickPoint</c> 开关直接真实点击。
    /// </summary>
    [Fact]
    public void RealClickCallSite_IsUniqueAndGuarded()
    {
        var root = Path.Combine(TestDataPaths.RepositoryRoot, "src");
        Assert.True(Directory.Exists(root), $"源码目录不存在：{root}");

        var locatorFile = Path.Combine(root, "SharpSight.UiAutomation", "UiPatternDispatcher.cs");
        Assert.True(File.Exists(locatorFile), $"未找到 {locatorFile}");
        Assert.Contains(File.ReadAllLines(locatorFile), l => l.Contains("void PerformClickPoint(", StringComparison.Ordinal));

        var allowed = AllowedClickPointLines(File.ReadAllLines(locatorFile));

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
        {
            var lines = File.ReadAllLines(file);
            var rel = Path.GetRelativePath(TestDataPaths.RepositoryRoot, file);
            // 失焦提交用到的 Mouse.Click 是「点窗口空白处」的一个点，不涉及按控件定位的真实点击；仅此文件放行。
            if (rel.EndsWith(Path.Combine("SharpSight.UiAutomation", "BindingCommit.cs"), StringComparison.OrdinalIgnoreCase))
                continue;
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("///", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (var token in RealClickTokens)
                {
                    if (!lines[i].Contains(token, StringComparison.Ordinal)) continue;
                    if (rel.EndsWith("UiPatternDispatcher.cs", StringComparison.Ordinal) && allowed.Contains(i + 1)) continue;
                    violations.Add($"{rel}:{i + 1}: [{token}] {lines[i].Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "真实点击调用点（.Click(/.DoubleClick(/.RightClick(/.RightDoubleClick(）只允许出现在 UiPatternDispatcher.PerformClickPoint 内"
            + "（须经 ui_action clickPoint=true 显式开关）；发现越界调用点：\n" + string.Join("\n", violations));
    }

    /// <summary>按方法体花括号配对，取出 PerformClickPoint 方法体覆盖的行号（1 基）。</summary>
    private static HashSet<int> AllowedClickPointLines(string[] lines)
    {
        var allowed = new HashSet<int>();
        var depth = 0;
        var started = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!started)
            {
                if (!lines[i].Contains("void PerformClickPoint(", StringComparison.Ordinal)) continue;
                started = true;
            }
            allowed.Add(i + 1);
            depth += lines[i].Count(c => c == '{');
            depth -= lines[i].Count(c => c == '}');
            if (depth <= 0 && lines[i].Contains('}')) break;   // 方法体闭合
        }
        return allowed;
    }

    /// <summary>
    /// 扫描 TypeRef（<c>FlaUI.Core.Input</c> 只放行键盘类型）+ MemberRef/PInvoke import（禁止原生物理输入 API 名）。
    /// </summary>
    private static List<string> ScanMetadata(string assemblyPath)
    {
        var violations = new List<string>();
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        foreach (var handle in reader.TypeReferences)
        {
            var tr = reader.GetTypeReference(handle);
            var ns = reader.GetString(tr.Namespace);
            if (!ns.Equals("FlaUI.Core.Input", StringComparison.Ordinal)
                && !ns.StartsWith("FlaUI.Core.Input.", StringComparison.Ordinal))
                continue;
            var name = reader.GetString(tr.Name);
            if (name is "Keyboard" or "Mouse" or "MouseButton") continue;   // 失焦提交用：键盘换焦点 + 点击窗口空白处（BindingCommit）
            violations.Add($"TypeRef {ns}.{name}");
        }

        foreach (var handle in reader.MemberReferences)
        {
            var name = reader.GetString(reader.GetMemberReference(handle).Name);
            if (IsForbiddenApiName(name))
                violations.Add($"MemberRef {name}");
        }

        foreach (var handle in reader.MethodDefinitions)
        {
            var md = reader.GetMethodDefinition(handle);
            if ((md.Attributes & MethodAttributes.PinvokeImpl) == 0) continue;
            var import = md.GetImport();
            var name = reader.GetString(import.Name);
            if (IsForbiddenApiName(name))
                violations.Add($"PInvoke import {name}");
        }

        return violations;
    }

    /// <summary>禁止的原生物理输入 API 名（窗口激活 <c>SetForegroundWindow</c> 已放行给真实点击前置步骤）。</summary>
    private static bool IsForbiddenApiName(string name)
        => name is "SetCursorPos" or "SendInput" or "ActivateWindow" or "PerformPhysicalMouse";
}
