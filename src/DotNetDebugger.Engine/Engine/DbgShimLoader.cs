using System.Runtime.InteropServices;
using ClrDebug;

namespace DotNetDebugger.Engine.Engine;

/// <summary>定位并加载 dbgshim.dll，产出 DbgShim 实例。</summary>
public static class DbgShimLoader
{
    /// <summary>
    /// 加载 dbgshim：依次尝试 ① 目标 runtime 目录（若提供）② 应用目录 runtimes/win-x64/native
    /// ③ 应用目录 ④ dotnet 主目录。找不到抛 FileNotFoundException。
    /// spike 已实测：Microsoft.Diagnostics.DbgShim.win-x64 包复制到 runtimes/win-x64/native/dbgshim.dll。
    /// </summary>
    public static DbgShim Load(string? targetRuntimeDir = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(targetRuntimeDir))
        {
            candidates.Add(Path.Combine(targetRuntimeDir, "dbgshim.dll"));
        }
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "runtimes", "win-x64", "native", "dbgshim.dll"));
        candidates.Add(Path.Combine(baseDir, "runtimes", "win-x86", "native", "dbgshim.dll"));
        candidates.Add(Path.Combine(baseDir, "dbgshim.dll"));
        // Windows .NET 安装目录（dotnet 主目录常含 dbgshim）
        var dotnetRoot = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (!string.IsNullOrEmpty(dotnetRoot)) candidates.Add(Path.Combine(dotnetRoot, "dbgshim.dll"));

        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                var h = NativeLibrary.Load(c);
                return new DbgShim(h);
            }
        }
        throw new FileNotFoundException($"未找到 dbgshim.dll（搜索：{string.Join("; ", candidates)}）。" +
            "可安装 Microsoft.Diagnostics.DbgShim.win-x64 包或指定目标 runtime 目录。");
    }
}
