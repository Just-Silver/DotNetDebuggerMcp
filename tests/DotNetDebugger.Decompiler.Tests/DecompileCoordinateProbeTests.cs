using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotNetDebugger.Decompiler.Decompiler;
using DotNetDebugger.Decompiler.Document;
using DotNetDebugger.Decompiler.Metadata;
using Xunit;

namespace DotNetDebugger.Decompiler.Tests;

/// <summary>
/// P3 步骤0 探针（spec §3）：MCP decompile 工具（InProcessDecompiler.DecompileType → DecompileAsString）
/// 与行映射源（DocumentService → 位置回写 writer）的文本必须逐字节一致——agent 引用的反编译行号才与
/// 映射坐标同系。任一类型不一致即失败（fallback 见 spec：line 定为 DocumentService 坐标并统一渲染）。
/// </summary>
public sealed class DecompileCoordinateProbeTests
{
    private static string Dll => TestDataPaths.TestSamplesDll;

    [Fact]
    public void DecompileType_全类型文本与DocumentService逐字节一致()
    {
        var types = EnumerateTypeFullNames(Dll);
        Assert.NotEmpty(types);

        var mismatches = new List<string>();
        var compared = 0;
        foreach (var type in types)
        {
            var tool = InProcessDecompiler.DecompileType(Dll, type, TestContext.Current.CancellationToken);
            var doc = DocumentService.GetTypeDocument(Dll, type);
            // 任一侧失败（反编译报错/超限）的类型不会成为 agent 的行号坐标来源，跳过不比
            if (doc.Error is not null || InProcessDecompiler.IsErrorResult(tool)) continue;
            compared++;
            if (tool != doc.Text)
            {
                // 诊断：第一个差异行
                var a = tool.Replace("\r\n", "\n").Split('\n');
                var b = doc.Text.Replace("\r\n", "\n").Split('\n');
                if (a.Length != b.Length)
                {
                    mismatches.Add($"{type}（行数 {a.Length}≠{b.Length}）");
                    Console.Error.WriteLine($"[probe] {type}: 行数 {a.Length} != {b.Length}");
                    continue;
                }
                for (var i = 0; i < a.Length; i++)
                {
                    if (a[i] != b[i])
                    {
                        Console.Error.WriteLine($"[probe] {type} 第 {i + 1} 行:\n  tool: {a[i]}\n  doc : {b[i]}");
                        break;
                    }
                }
                mismatches.Add(type);
            }
        }

        Assert.True(compared > 0, "无可比类型（两侧都失败）");
        Assert.True(mismatches.Count == 0,
            $"以下类型的两管线文本不一致（行号坐标基准断裂，见 spec §3 fallback）：{string.Join("、", mismatches)}");
    }

    private static List<string> EnumerateTypeFullNames(string dll)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var mr = pe.GetMetadataReader();
        return mr.TypeDefinitions
            .Select(h => MetadataNaming.FullName(mr, mr.GetTypeDefinition(h)))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }
}
