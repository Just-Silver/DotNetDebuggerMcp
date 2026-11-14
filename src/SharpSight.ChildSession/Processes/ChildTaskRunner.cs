using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using SharpSight.ChildSession.Contracts;

namespace SharpSight.ChildSession.Processes;

public sealed class ChildTaskRunner(string instance, string root, int sessionId) : IAsyncDisposable
{
    private readonly Dictionary<string, ChildTaskJournal> _tasks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1);
    private readonly ChildLogQuota _quota = new();

    public async Task<ChildReply> ExecuteAsync(ChildRequest request, CancellationToken ct)
    {
        try
        {
            if (request.Instance != instance || Process.GetCurrentProcess().SessionId != sessionId)
                throw new InvalidOperationException("任务执行实例或实际会话不符。");
            var action = Text(request.Arguments, "action", "status");
            await _gate.WaitAsync(ct);
            try
            {
                if (action == "start") return Start(request);
                if (action == "list") return ChildReply.Create(request, "completed", "自有任务清单。",
                    new { tasks = _tasks.Values.Select(t => t.Snapshot()).ToArray() });
                var id = Text(request.Arguments, "taskId");
                if (!_tasks.TryGetValue(id, out var task)) throw new ArgumentException("未知taskId，请先list。");
                if (action == "cancel")
                {
                    task.Job.Cancel();
                    task.Cancelled = true;
                    return ChildReply.Create(request, "completed", "已取消自有Job；不保证关闭被转交的外部应用。", task.Snapshot());
                }
                if (action == "status") return ChildReply.Create(request, "completed", "任务状态。", task.Snapshot());
                if (action == "output")
                {
                    var data = await task.Output.ReadAsync(
                        request.Arguments.TryGetProperty("cursor", out var cursor) ? cursor.GetInt64() : 0,
                        request.Arguments.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 8192, ct);
                    return ChildReply.Create(request, "completed", "任务增量输出。", data);
                }
                throw new ArgumentException("合法action：start/list/status/output/cancel。");
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex) { return ChildReply.Create(request, "not-executed", ex.Message); }
    }

    private ChildReply Start(ChildRequest request)
    {
        if (_tasks.Count >= 256 || _quota.Full) throw new InvalidOperationException("本实例任务或日志达到上限，拒绝新任务。");
        var executable = Text(request.Arguments, "executable");
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new ArgumentException("executable必须已解析为存在的绝对路径。");
        var args = JsonSerializer.Deserialize<string[]>(Text(request.Arguments, "arguments", "[]"))
            ?? throw new ArgumentException("arguments必须为字符串数组JSON。");
        if (args.Any(a => a is null || a.Contains('\0'))) throw new ArgumentException("参数不能为null或包含NUL。");
        var directory = Text(request.Arguments, "workingDirectory");
        if (string.IsNullOrEmpty(directory)) directory = Path.GetDirectoryName(executable)!;
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new ArgumentException("工作目录无效。");
        var environment = ReadEnvironment(Text(request.Arguments, "environment", "{}"));
        var id = Guid.NewGuid().ToString("N");
        var job = new WindowsJob(executable, args, directory, environment);
        var output = new ChildOutputStore(Path.Combine(root, "tasks", id), _quota);
        var task = new ChildTaskJournal(id, job, output);
        _tasks.Add(id, task);
        task.Completion = MonitorAsync(task);
        return ChildReply.Create(request, "completed", "任务已启动，不等待应用退出。", task.Snapshot());
    }

    private static Dictionary<string, string> ReadEnvironment(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
            result[(string)item.Key] = (string)item.Value!;
        var additions = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new ArgumentException("environment必须为字符串字典JSON。");
        foreach (var pair in additions)
        {
            if (string.IsNullOrEmpty(pair.Key) || pair.Key.Contains('=') || pair.Key.Contains('\0') ||
                pair.Value is null || pair.Value.Contains('\0')) throw new ArgumentException("环境变量键或值无效。");
            result[pair.Key] = pair.Value;
        }
        return result;
    }

    private static async Task MonitorAsync(ChildTaskJournal task)
    {
        try
        {
            await Task.WhenAll(DrainAsync(task.Job.Output, "stdout", task.Output),
                DrainAsync(task.Job.Error, "stderr", task.Output), task.Job.Process.WaitForExitAsync());
        }
        catch (Exception ex) { task.Failure = ex.Message; Console.Error.WriteLine($"任务输出读取失败：{ex.Message}"); }
    }

    private static async Task DrainAsync(StreamReader reader, string stream, ChildOutputStore output)
    {
        var chars = new char[1024];
        var carry = "";
        int count;
        while ((count = await reader.ReadAsync(chars)) != 0)
        {
            var text = carry + new string(chars, 0, count);
            carry = char.IsHighSurrogate(text[^1]) ? text[^1..] : "";
            if (carry.Length != 0) text = text[..^1];
            await output.AppendAsync(stream, text);
        }
        if (carry.Length != 0) await output.AppendAsync(stream, carry);
    }

    private static string Text(JsonElement args, string name, string fallback = "")
        => args.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var task in _tasks.Values) task.Job.Cancel();
            foreach (var task in _tasks.Values)
            {
                await task.Completion;
                task.Job.Dispose();
                await task.Output.DisposeAsync();
            }
            _tasks.Clear();
        }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
