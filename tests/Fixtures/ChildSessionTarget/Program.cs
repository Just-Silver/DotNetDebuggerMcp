using System.Diagnostics;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = Encoding.UTF8;
switch (args.FirstOrDefault())
{
    case "report":
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            arguments = args.Skip(1).ToArray(), directory = Environment.CurrentDirectory,
            sample = Environment.GetEnvironmentVariable("CHILD_TEST_SAMPLE"),
            session = Process.GetCurrentProcess().SessionId
        }));
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
    case "spawn":
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("sleep");
        using (var child = Process.Start(info)!)
        {
            Console.WriteLine(child.Id);
            await child.WaitForExitAsync();
        }
        break;
    default:
        Console.Error.WriteLine("模式：report/flood/sleep/spawn");
        return 1;
}
return 0;
