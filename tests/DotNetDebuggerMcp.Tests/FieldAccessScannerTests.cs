using DotNetDebugger.Decompiler.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// FieldAccessScanner 字段读写点扫描用例。 素材：生成测试程序集（tests/TestData）中的 FieldHolder（public int
/// Data;）——FieldUser.Read 读取 Data（ldfld）、 FieldWriter.Write 写入 Data（stfld）、无取地址（ldflda/ldsflda）。
/// </summary>
public class FieldAccessScannerTests
{
    [Fact]
    public void FieldHolder_Data_读取写入定位且无取地址()
    {
        using var scope = new MetadataScope();
        var target = FieldHandle(scope.Reader, $"{TestDataPaths.SamplesNamespace}.FieldHolder", "Data");

        var result = new FieldAccessScanner(scope.Pe).Scan(target);

        Assert.Contains(result.Reads, r => r.StartsWith($"{TestDataPaths.SamplesNamespace}.FieldUser::"));
        Assert.Contains(result.Writes, w => w.StartsWith($"{TestDataPaths.SamplesNamespace}.FieldWriter::"));
        Assert.Empty(result.Addresses);
    }

    [Fact]
    public void FieldHolder_Data_读取段行含成员签名()
    {
        using var scope = new MetadataScope();
        var target = FieldHandle(scope.Reader, $"{TestDataPaths.SamplesNamespace}.FieldHolder", "Data");

        var result = new FieldAccessScanner(scope.Pe).Scan(target);

        Assert.Contains(result.Reads, r => r.Contains("public int Read("));
        Assert.Contains(result.Writes, w => w.Contains("public void Write("));
    }

    [Fact]
    public void FieldHolder_Data_泛型实例化同名字段访问不误归因()
    {
        using var scope = new MetadataScope();
        var target = FieldHandle(scope.Reader, $"{TestDataPaths.SamplesNamespace}.FieldHolder", "Data");

        var result = new FieldAccessScanner(scope.Pe).Scan(target);

        // GenericFieldUser.TouchBox 访问的是 GenericFieldBox<FieldHolder>.Data（MemberRef parent 为
        // TypeSpec GenericFieldBox<FieldHolder>），FieldHolder 只是泛型实参，不得归因到 FieldHolder.Data
        Assert.DoesNotContain(result.Reads, r => r.Contains("TouchBox"));
    }

    [Fact]
    public void GenericFieldBox_Data_泛型实例化字段访问归因到容器自身字段()
    {
        using var scope = new MetadataScope();
        var target = FieldHandle(scope.Reader, $"{TestDataPaths.SamplesNamespace}.GenericFieldBox`1", "Data");

        var result = new FieldAccessScanner(scope.Pe).Scan(target);

        // GenericFieldUser.TouchBox 的 GenericFieldBox<FieldHolder>.Data 访问应归因到 GenericFieldBox.Data
        Assert.Contains(result.Reads, r => r.Contains("TouchBox"));
    }

    /// <summary>
    /// 取指定类型指定名字段的定义句柄。
    /// </summary>
    private static FieldDefinitionHandle FieldHandle(MetadataReader reader, string typeName, string fieldName)
    {
        var typeHandle = MetadataNaming.FindType(reader, typeName);
        Assert.True(typeHandle.HasValue, $"测试程序集中未找到 {typeName}");
        foreach (var fieldHandle in reader.GetTypeDefinition(typeHandle.Value).GetFields())
        {
            if (reader.GetString(reader.GetFieldDefinition(fieldHandle).Name) == fieldName)
            {
                return fieldHandle;
            }
        }
        throw new InvalidOperationException($"{typeName} 未找到字段 {fieldName}");
    }

    /// <summary>
    /// 持有打开的 PEReader 与元数据读取器，保证 reader 在断言期间有效（PE 释放后 reader 访问会崩）。
    /// </summary>
    private sealed class MetadataScope : IDisposable
    {
        private readonly FileStream _fs;
        private readonly PEReader _pe;

        public MetadataScope()
        {
            _fs = File.OpenRead(TestDataPaths.TestSamplesDll);
            _pe = new PEReader(_fs);
            Reader = _pe.GetMetadataReader();
        }

        public PEReader Pe => _pe;

        public MetadataReader Reader { get; }

        public void Dispose()
        {
            _pe.Dispose();
            _fs.Dispose();
        }
    }
}