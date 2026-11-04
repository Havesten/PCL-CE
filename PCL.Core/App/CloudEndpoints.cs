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
    /// <summary>API 根地址，不带结尾斜杠。</summary>
    public const string ApiRoot = "https://api.fuukir.cn/pclce-api";

    /// <summary>
    ///     更新源基地址。更新模块拼接的是 <c>{基地址}apiv2/...</c>，所以这里必须带结尾斜杠。
    /// </summary>
    public const string UpdateRoot = ApiRoot + "/";
}
