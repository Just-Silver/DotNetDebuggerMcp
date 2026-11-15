using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;

Console.OutputEncoding = Encoding.UTF8;
switch (args.FirstOrDefault())
{
    case "holder-parent":
        var platform = new SharpSight.ChildSession.Lifecycle.ChildSessionPlatform(
            new SharpSight.ChildSession.Lifecycle.ChildHostCommand(Environment.ProcessPath!, ["sleep"]));
        var holder = platform.StartHolder(args[1], "output-probe");
        Console.WriteLine(JsonSerializer.Serialize(new { holder.ProcessId, holder.StartedTicks }));
        break;
    case "report":
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            arguments = args.Skip(1).ToArray(), directory = Environment.CurrentDirectory,
            sample = Environment.GetEnvironmentVariable("CHILD_TEST_SAMPLE"),
            session = Process.GetCurrentProcess().SessionId
        }));
        break;
    case "native-launch":
        return ChildSessionTarget.NativeLaunch.Run(int.Parse(args[1]), args[2], args.Skip(3).ToArray());
    case "report-out":
        var reportJson = JsonSerializer.Serialize(new
        {
            arguments = args.Skip(2).ToArray(), directory = Environment.CurrentDirectory,
            session = Process.GetCurrentProcess().SessionId
        });
        await File.WriteAllTextAsync(args[1], reportJson);
        break;
    case "flood":
        for (var i = 0; i < 1800; i++) Console.Write(new string('中', 4096));
        Console.Error.WriteLine("排空完成");
        break;
    case "sleep":
        Console.WriteLine("运行中");
        await Task.Delay(TimeSpan.FromMinutes(5));
        break;
    case "stdin":
        await Console.In.ReadToEndAsync();
        Console.WriteLine("输入结束");
        break;
    case "marker":
        await ChildSessionTarget.MarkerWindow.RunAsync(args.ElementAtOrDefault(1) ?? "lime");
        break;
    case "gui":
        if (args.ElementAtOrDefault(1) is not { Length: > 0 } outputDirectory)
        {
            Console.Error.WriteLine("用法：gui <outputDirectory>");
            return 1;
        }
        await ChildSessionTarget.ProbeForm.RunAsync(outputDirectory);
        break;
    case "spawn":
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("sleep");
        using (var child = Process.Start(info)!)
        {
            Console.WriteLine(child.Id);
            await child.WaitForExitAsync();
        }
        break;
    case "ipc-client":
        return await RunIpcClientAsync();
    default:
        Console.Error.WriteLine("模式：report/flood/sleep/spawn");
        return 1;
}
return 0;

static async Task<int> RunIpcClientAsync()
{
    // 凭据只经标准输入传递，不吃命令行；按 UTF-8 读取，避免依赖控制台编码。
    using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
    using var payload = JsonDocument.Parse(await input.ReadToEndAsync());
    var root = payload.RootElement;
    var reply = root.TryGetProperty("forgeImage", out var forged)
        ? await SendForgedEnvelopeAsync(root, forged.GetString()!)
        : await new ChildPipeClient(root.GetProperty("pipeName").GetString()!,
            root.GetProperty("secret").GetString()!, ReadServer(root)).SendAsync(CreateRequest(root));
    await File.WriteAllTextAsync(root.GetProperty("output").GetString()!, $"{reply.Status}\n{reply.Message}");
    Console.WriteLine(reply.Status);
    return reply.Status == "completed" ? 0 : 2;
}

static ChildPeer ReadServer(JsonElement root)
{
    var server = root.GetProperty("server");
    return new ChildPeer(server.GetProperty("processId").GetInt32(),
        server.GetProperty("startedTicks").GetInt64(), server.GetProperty("sessionId").GetInt32(),
        server.GetProperty("sid").GetString()!, server.GetProperty("image").GetString()!);
}

static ChildRequest CreateRequest(JsonElement root) =>
    new(root.GetProperty("instance").GetString()!, Guid.NewGuid().ToString("N"), "ui_input",
        JsonSerializer.SerializeToElement(new { value = root.GetProperty("value").GetString() }),
        DateTimeOffset.UtcNow.AddSeconds(10));

static async Task<ChildReply> SendForgedEnvelopeAsync(JsonElement root, string forgedImage)
{
    var envelope = JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1,
        secret = root.GetProperty("secret").GetString(),
        peer = ChildPeer.Current with { Image = forgedImage },
        request = CreateRequest(root)
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    await using var pipe = new NamedPipeClientStream(".", root.GetProperty("pipeName").GetString()!,
        PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(5000);
    var prefix = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(prefix, envelope.Length);
    await pipe.WriteAsync(prefix);
    await pipe.WriteAsync(envelope);
    await pipe.FlushAsync();
    await pipe.ReadExactlyAsync(prefix);
    var data = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix)];
    await pipe.ReadExactlyAsync(data);
    return JsonSerializer.Deserialize<ChildReply>(data, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("伪造信封回复为空。");
}
