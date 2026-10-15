using SharpSight.UiAutomation;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// R1 确定性防物理输入回归护栏（与桌面是否交互无关，CI 必过/必败）：
/// ① 扫描宿主与 UI 自动化库源码树 <c>src/DotNetDebuggerMcp/**/*.cs</c> + <c>src/SharpSight.UiAutomation/**/*.cs</c>，
///    禁止出现物理输入 API token；
/// ② 扫描构建出的宿主与 <c>SharpSight.UiAutomation.dll</c> 程序集的 IL 元数据（TypeRef/MemberRef/PInvoke import），
///    禁止对物理输入 API 的引用。两层均为确定性兜底——即使 e2e 因 CI 非交互/共享桌面跳过，也不会静默放过物理回归。
/// （测试文件自身含负向 token 字符串，故只扫 src/ 与宿主产物，不扫测试源码。）
/// </summary>
public sealed class NoPhysicalInputGuardTests
{
    /// <summary>
    /// 物理输入 API/token 黑名单（源码与 IL 共用语义）。
    /// <para><b>P3 例外</b>：<c>ui_action clickPoint=true</c> 的真实点击走 <c>AutomationElement.Click</c>（内部即
    /// <c>FlaUI.Core.Input.Mouse</c>）——故 <c>Click</c> 不再入此黑名单（那会让合法调用恒违规）；改由
    /// <see cref="RealClickCallSite_IsUniqueAndGuarded"/> 断言「真实点击调用点唯一且只在
    /// <c>PerformClickPoint</c> 内」。<c>FlaUI.Core.Input</c> 命名空间黑名单保留：本库不得**直接**引 Mouse。</para>
    /// </summary>
    internal static readonly string[] ForbiddenTokens =
    [
        "FlaUI.Core.Input",
        "Mouse.",
        "SetCursorPos",
        "SendInput",
        "SetForegroundWindow",
        "ActivateWindow",
        "PerformPhysicalMouse",
    ];

    /// <summary>真实点击入口 token（P3）：只允许出现在 <c>UiPatternDispatcher.PerformClickPoint</c> 内。</summary>
    private static readonly string[] RealClickTokens = ["Click(", "DoubleClick(", "RightClick(", "RightDoubleClick("];

    [Fact]
    public void HostSource_ContainsNoPhysicalInputTokens()
    {
        // 物理输入敏感面（UIA 定位/动作）在 Task 2 迁入 SharpSight.UiAutomation——源码扫描同步覆盖两个源码树，
        // 否则护栏在迁出后静默失去覆盖对象（原 UiElementLocator 等文件的 @物理输入 检查会落空）。
        var roots = new[]
        {
            Path.Combine(TestDataPaths.RepositoryRoot, "src", "DotNetDebuggerMcp"),
            Path.Combine(TestDataPaths.RepositoryRoot, "src", "SharpSight.UiAutomation"),
        };
        foreach (var root in roots)
            Assert.True(Directory.Exists(root), $"源码目录不存在：{root}");

        // 排除 bin/obj 生成物，只扫受跟踪源码。
        var files = roots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(f => !IsBuildOutput(f))
            .ToList();
        Assert.NotEmpty(files);

        var violations = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var token in ForbiddenTokens)
                {
                    if (lines[i].Contains(token, StringComparison.Ordinal))
                        violations.Add($"{Path.GetRelativePath(TestDataPaths.RepositoryRoot, file)}:{i + 1}: [{token}] {lines[i].Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "宿主与 SharpSight.UiAutomation 源码不得含物理输入 API（U1A 全 UIA 化）；发现：\n" + string.Join("\n", violations));
    }

    [Fact]
    public void HostAssemblyMetadata_ReferencesNoPhysicalInputApis()
    {
        // 宿主程序集 + UI 自动化库程序集：迁出后 FlaUI 引用落在库程序集，只扫宿主会漏掉真正的物理输入引用面。
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
            "宿主与 SharpSight.UiAutomation 程序集 IL 不得引用物理输入 API（U1A 全 UIA 化）；发现：\n" + string.Join("\n", violations));
    }

    private static bool IsBuildOutput(string file)
    {
        var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Contains("bin", StringComparer.OrdinalIgnoreCase)
            || parts.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// P3 护栏：真实点击调用点（<c>.Click(</c>/<c>.DoubleClick(</c>/<c>.RightClick(</c>/<c>.RightDoubleClick(</c>）必须**只**
    /// 出现在 <c>UiPatternDispatcher.PerformClickPoint</c> 方法体内——否则任何人可绕过 <c>clickPoint</c> 开关直接真实点击
    /// （源码 token 扫描抓不到 <c>Click(false)</c>，IL 层又因合法调用共存而无法用黑名单，故按调用点唯一性守）。
    /// </summary>
    [Fact]
    public void RealClickCallSite_IsUniqueAndGuarded()
    {
        var file = Path.Combine(TestDataPaths.RepositoryRoot, "src", "SharpSight.UiAutomation", "UiPatternDispatcher.cs");
        Assert.True(File.Exists(file), $"未找到 {file}");
        var lines = File.ReadAllLines(file);

        var inPerformClickPoint = false;
        var violations = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("void PerformClickPoint(", StringComparison.Ordinal)) { inPerformClickPoint = true; continue; }
            // 方法体结束（顶格右括号）离开锚定区；PerformClickPoint 之后的方法都在顶格声明
            if (inPerformClickPoint && line.StartsWith("    ///", StringComparison.Ordinal)) { inPerformClickPoint = false; }

            // 排除声明行（internal static void PerformClickPoint）与注释
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("///", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

            foreach (var token in RealClickTokens)
            {
                if (!line.Contains(token, StringComparison.Ordinal)) continue;
                if (!inPerformClickPoint)
                    violations.Add($"{i + 1}: [{token}] {line.Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            "真实点击调用点（.Click(/.DoubleClick(/.RightClick(/.RightDoubleClick(）只允许出现在 UiPatternDispatcher.PerformClickPoint 内"
            + "（真实点击须经 ui_action clickPoint=true 显式开关）；发现越界调用点：\n" + string.Join("\n", violations));

        // PerformClickPoint 必须真实存在（防改名后本护栏静默失效）
        Assert.Contains(lines, l => l.Contains("void PerformClickPoint(", StringComparison.Ordinal));
    }

    /// <summary>扫描 TypeRef（禁止 FlaUI.Core.Input 命名空间）+ MemberRef/PInvoke import（禁止物理输入 API 名）。</summary>
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
            if (ns.Equals("FlaUI.Core.Input", StringComparison.Ordinal)
                || ns.StartsWith("FlaUI.Core.Input.", StringComparison.Ordinal))
                violations.Add($"TypeRef {ns}.{reader.GetString(tr.Name)}");
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

    /// <summary>
    /// 物理输入 API 名判定：只匹配明确属于光标/输入注入/抢前台的 API（保留 <c>Mouse.</c> 源码 token 的语义在 IL 层
    /// 对应到 <c>FlaUI.Core.Input.Mouse</c> 的 TypeRef，已由命名空间规则覆盖，故此处不按裸名 "Mouse" 误伤无关成员）。
    /// </summary>
    private static bool IsForbiddenApiName(string name)
        => name is "SetCursorPos" or "SendInput" or "SetForegroundWindow" or "ActivateWindow" or "PerformPhysicalMouse";
}
