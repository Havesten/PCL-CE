using System;

namespace PCL.Core.App;

/// <summary>
///     自建云端的唯一入口地址。
///     <para>
///         启动器所有在线服务（更新清单、公告、中继联机 API）都从这里派生，
///         中继服务端 <c>relay/config.yaml</c> 的 <c>cloud.base_url</c> 也指向同一个根地址。
///         换域名时只改这一处，避免更新源和中继各指一个地方。
///     </para>
/// </summary>
public static class CloudEndpoints
{
    private const string DefaultApiRoot = "https://api.fuukir.cn/pclce-api";

    /// <summary>
    ///     API 根地址，不带结尾斜杠。
    ///     <para>
    ///         设置环境变量 <c>PCL_CLOUD_API</c> 可临时指向本地或测试服务
    ///         （例如联调强制更新时指向 <c>http://127.0.0.1:18080</c>），
    ///         未设置时使用生产地址，与 <see cref="PCL.Core.Link.Relay.RelayApi" /> 的
    ///         <c>PCL_RELAY_API</c> 是同一套约定。
    ///     </para>
    /// </summary>
    public static string ApiRoot { get; } = ResolveApiRoot();

    /// <summary>
    ///     更新源基地址。更新模块拼接的是 <c>{基地址}apiv2/...</c>，所以这里必须带结尾斜杠。
    /// </summary>
    public static string UpdateRoot => ApiRoot + "/";

    private static string ResolveApiRoot()
    {
        var custom = Environment.GetEnvironmentVariable("PCL_CLOUD_API");
        return string.IsNullOrWhiteSpace(custom) ? DefaultApiRoot : custom.Trim().TrimEnd('/');
    }
}
