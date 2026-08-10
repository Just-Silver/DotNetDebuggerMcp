using ILSpyMcp.Configuration;

using System.Text.Json;

namespace ILSpyMcp.UpdateCheck;

/// <summary>
/// NuGet 包最新稳定版查询：经 flatcontainer 版本清单 API 拉取全部版本，取最大稳定版（排除预发布）。 网络失败/超时/解析异常一律返回 null（调用方静默跳过该检查项），绝不影响反编译等核心功能。
/// </summary>
public sealed class NuGetClient
{
    private readonly HttpClient _http;

    /// <summary>
    /// 以默认 HttpClient（走系统代理，超时取 <see cref="AppConfig.NuGetCheckTimeout"/>）构造。
    /// </summary>
    public NuGetClient()
        : this(new HttpClient { Timeout = AppConfig.NuGetCheckTimeout })
    {
    }

    /// <summary>
    /// 以可替换的消息处理链构造（测试注入 fake handler，避免真实网络请求）。
    /// </summary>
    /// <param name="handler">HTTP 消息处理链。</param>
    public NuGetClient(HttpMessageHandler handler)
        : this(new HttpClient(handler) { Timeout = AppConfig.NuGetCheckTimeout })
    {
    }

    private NuGetClient(HttpClient http)
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppConfig.NuGetPackageId);
        _http = http;
    }

    /// <summary>
    /// 查询指定包的最新稳定版本；网络/超时/解析异常返回 null。
    /// </summary>
    /// <param name="packageId">NuGet 包 id，如 <see cref="AppConfig.NuGetPackageId"/>。</param>
    /// <returns>最新稳定版本号（如 "1.1.0"）；失败为 null。</returns>
    public async Task<string?> GetLatestStableVersionAsync(string packageId)
    {
        try
        {
            var url = $"{AppConfig.NuGetVersionListUrlPrefix}{packageId}/index.json";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            // flatcontainer 版本清单按发布时间升序，最后一个即最新：从尾部向前取第一个稳定版（排除预发布）
            var versions = doc.RootElement.GetProperty("versions").EnumerateArray().Select(e => e.GetString());
            foreach (var v in versions.Reverse())
            {
                if (v is null || v.Contains('-')) continue; // 排除预发布（如 1.2.0-beta）
                if (Version.TryParse(v, out var ver)) return ver.ToString(3);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}