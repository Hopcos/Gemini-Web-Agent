namespace GeminiWebAgent.Configurations;

public class GeminiWebOptions
{
    /// <summary>
    /// 可选：对外暴露的网关 API Key。
    /// - 若配置了 ApiKey，客户端需传入 Authorization: Bearer <ApiKey> 进行访问控制
    /// - 若未配置 ApiKey，且后端已通过 Cookies/File 提供了凭据，则允许免密或传入任意 Key
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 静态 Cookies 字符串（如果为空，且 CookieSource 为 Manual，则使用 Bearer Token 透传）
    /// </summary>
    public string Cookies { get; set; } = string.Empty;

    /// <summary>
    /// Gemini Web 基础地址
    /// </summary>
    public string BaseUrl { get; set; } = "https://gemini.google.com";

    /// <summary>
    /// Cookie 获取模式开关:
    /// - "Manual": 手工配置或客户端请求头透传（默认模式）
    /// - "File": 从本地或 Docker 挂载文件加载，支持文件变动热重载
    /// </summary>
    public string CookieSource { get; set; } = "Manual";

    /// <summary>
    /// Cookie 文件路径（用于 File 模式及自动滚动续期时的回写持久化）
    /// </summary>
    public string CookieFilePath { get; set; } = "cookies.json";

    /// <summary>
    /// 是否启用 Set-Cookie 响应拦截与 __Secure-1PSIDTS 滚动续期（实现永续保活）
    /// </summary>
    public bool EnableAutoRenewal { get; set; } = true;

    /// <summary>
    /// 后台保活巡检间隔（分钟，默认 120 分钟），主动访问以触发滚动 Cookie 刷新
    /// </summary>
    public int KeepAliveIntervalMinutes { get; set; } = 120;

    /// <summary>
    /// 调试开关：将发给 Gemini 的提示词尾部与模型原始回复写入日志（默认关闭，内容可能包含源码/敏感信息）
    /// </summary>
    public bool LogPayloads { get; set; }
}
