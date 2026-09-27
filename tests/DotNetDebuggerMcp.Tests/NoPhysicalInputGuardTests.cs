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
    /// <summary>物理输入 API/token 黑名单（源码与 IL 共用语义）。</summary>
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
