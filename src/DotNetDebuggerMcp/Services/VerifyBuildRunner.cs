using System.Diagnostics;

namespace DotNetDebuggerMcp.Services;

/// <summary>编译结果。Ok=false 时 Summary 为中文失败摘要（含进程尾部输出/超时/工程不存在）；ExitCode 失败时非 0（超时/取消为 -1）。</summary>
internal sealed record BuildOutcome(bool Ok, string Summary, int ExitCode);

/// <summary>
/// V1 debug_verify 重编译器：dotnet build（项目默认输出，不设置 OutputPath）+ 产物自动定位
/// （dotnet msbuild -getProperty:TargetPath）。子进程 stdout/stderr 持续排空（ReadToEndAsync，防管道阻塞卡死），
/// 超时 Kill 整棵进程树。失败统一中文摘要（保留尾部行供 agent 定位）。
/// </summary>
internal static class VerifyBuildRunner
{
    /// <summary>编译错误摘要保留的尾部行数。</summary>
    public const int MaxSummaryLines = 40;

    /// <summary>GetTargetPathAsync / build 的进程超时秒数兜底（正常场景毫秒级，防 msbuild 挂死）。</summary>
    private const int ProcessTimeoutSeconds = 90;

    /// <summary>
    /// 编译工程（默认输出路径）。成功：Ok=true，Summary=「编译成功」；失败：Ok=false，Summary 含中文原因 +
    /// stdout/stderr 尾部（工程不存在/编译错误/超时/取消）。绝不启动旧产物由调用方契约保证（Ok=false 即停）。
    /// </summary>
    public static async Task<BuildOutcome> RunAsync(string project, string configuration, int timeoutSeconds, CancellationToken ct)
    {
        if (!File.Exists(project))
            return new BuildOutcome(false, $"工程文件不存在：{project}（build.project 需为 .csproj 等工程文件路径）。", -1);
        if (timeoutSeconds <= 0) timeoutSeconds = 120;

        var (exitCode, stdout, stderr, timedOut) = await RunDotnetAsync(
            ["build", project, "-c", configuration, "--nologo"],
            Path.GetDirectoryName(project)!, timeoutSeconds, ct);

        if (ct.IsCancellationRequested)
            return new BuildOutcome(false, "编译已取消。", -1);
        if (timedOut)
            return new BuildOutcome(false, $"编译超时（{timeoutSeconds} 秒）：{project}。", -1);
        if (exitCode != 0)
        {
            var tail = TailText(stdout, stderr);
            return new BuildOutcome(false, $"编译失败（exitCode={exitCode}）：{Path.GetFileName(project)} 未产出可用产物——绝不用旧产物复验。{tail}", exitCode);
        }
        return new BuildOutcome(true, "编译成功。", 0);
    }

    /// <summary>
    /// 编译产物自动定位：dotnet msbuild -getProperty:TargetPath 取项目默认输出路径（绝对路径，SDK 现算，
    /// agent 不写路径/不查 TFM）。console 工程返回管理程序集 dll（apphost exe 在同目录同名）；
    /// 失败（工程不存在/退出码非 0/超时/取消）返回 null。
    /// </summary>
    public static async Task<string?> GetTargetPathAsync(string project, string configuration, CancellationToken ct)
    {
        if (!File.Exists(project) || ct.IsCancellationRequested) return null;
        var projectDir = Path.GetDirectoryName(project)!;

        var (exitCode, stdout, _, timedOut) = await RunDotnetAsync(
            ["msbuild", project, $"-p:Configuration={configuration}", "-getProperty:TargetPath", "-nologo"],
            projectDir, ProcessTimeoutSeconds, ct);
        if (exitCode != 0 || timedOut || ct.IsCancellationRequested) return null;

        foreach (var line in stdout.Split('\n'))
        {
            var candidate = line.Trim().Trim('"');
            if (candidate.Length == 0) continue;
            var full = Path.IsPathRooted(candidate) ? candidate : Path.Combine(projectDir, candidate);
            if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            return File.Exists(full) ? full : null;
        }
        return null;
    }

    /// <summary>解析实际可启动文件：console 工程 TargetPath 是管理 dll——同目录存在 apphost exe 时用 exe 启动
    /// （纯库/无 apphost 时原样返回，调用方自行判定）。</summary>
    public static string ResolveLaunchExecutable(string targetPath)
    {
        if (targetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var exe = Path.ChangeExtension(targetPath, ".exe");
            if (File.Exists(exe)) return exe;
        }
        return targetPath;
    }

    /// <summary>commandLine 首段文件名与编译产物是否同名（忽略扩展名与大小写：产物可能是 .dll/.exe，
    /// agent 写 .exe 或 .dll 均可）。引号/路径前缀剥除后只比文件名主干。</summary>
    public static bool ArtifactFileMatches(string commandLineFirstToken, string targetPath)
    {
        var first = commandLineFirstToken.Trim().Trim('"');
        if (first.Length == 0) return false;
        var name = Path.GetFileName(first);
        return string.Equals(Path.GetFileNameWithoutExtension(name), Path.GetFileNameWithoutExtension(targetPath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把（可能多行的）stdout/stderr 尾部拼成失败摘要段（各保留尾部若干行）。</summary>
    public static string TailText(string stdout, string stderr)
    {
        var lines = new List<string>();
        AppendTail(lines, stdout);
        AppendTail(lines, stderr);
        if (lines.Count == 0) return "";
        var tail = string.Join(Environment.NewLine, lines);
        return Environment.NewLine + tail;
    }

    private static void AppendTail(List<string> lines, string text)
    {
        var parts = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var take = Math.Min(parts.Length, MaxSummaryLines);
        for (var i = parts.Length - take; i < parts.Length; i++)
            lines.Add(parts[i]);
    }

    /// <summary>dotnet 子进程统一执行（排空输出 + 超时 Kill 进程树）；返回尾部是否超时/取消。</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr, bool TimedOut)> RunDotnetAsync(
        IReadOnlyList<string> args, string workingDirectory, int timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 dotnet 进程。");
        // 持续排空 stdout/stderr（仓库纪律：子进程输出不排空会卡死管道）
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), ct);

        var winner = await Task.WhenAny(exitTask, timeoutTask).ConfigureAwait(false);
        var timedOut = winner != exitTask;
        if (timedOut)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已退出：忽略 */ }
        }
        try { await exitTask.ConfigureAwait(false); } catch { /* 已被 Kill */ }
        string stdout, stderr;
        try { stdout = await stdoutTask.ConfigureAwait(false); } catch { stdout = ""; }
        try { stderr = await stderrTask.ConfigureAwait(false); } catch { stderr = ""; }
        int exitCode;
        try { exitCode = process.ExitCode; } catch { exitCode = -1; }
        return (exitCode, stdout, stderr, timedOut);
    }
}
