using System.Text;

namespace DotNetDebuggerMcp.Client;

/// <summary>
/// 读取正在追加的事件日志。夹具写端以 FileShare.Read 持有文件，读端必须允许写共享，否则默认共享模式会因共享冲突失败；
/// 只解码以换行结束的完整行，尾部半行与未完成 UTF-8 留到下一次读取，不能当作已发生的事件。
/// </summary>
internal static class FixtureEventLog
{
    internal static async Task<string[]> ReadCompleteLinesAsync(string path, CancellationToken cancellationToken = default)
    {
        byte[] content;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            content = new byte[checked((int)stream.Length)];
            var offset = 0;
            while (offset < content.Length)
            {
                var read = await stream.ReadAsync(content.AsMemory(offset), cancellationToken);
                if (read == 0) break;
                offset += read;
            }
            if (offset < content.Length) Array.Resize(ref content, offset);
        }
        var end = Array.LastIndexOf(content, (byte)'\n');
        if (end < 0) return [];
        return Encoding.UTF8.GetString(content, 0, end).Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToArray();
    }
}
