using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>
///     云端「更新」配置页对应的更新源，读取 <c>{基地址}apiv2/updateinfo.json</c>。
///     <para>
///         该文件由 cloud/pcl-cloud 的 Web 后台「更新」独立配置页保存后立即生成，
///         包含：是否强制更新、最新版本、强制最低版本、下载直链、更新日志与公告。
///         与旧的 updates-{通道}{架构}.json 清单不同，不依赖 cache.json 与补丁文件。
///     </para>
/// </summary>
public class UpdatesCloudModel : IUpdateSource
{
    private readonly string _baseUrl;

    private UpdateInfoModel _info;

    public UpdatesCloudModel(string baseUrl, string name = "PCLCE-NS Cloud")
    {
        // 后续拼接的是 "{baseUrl}apiv2/..."，基地址少了结尾斜杠会拼错
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) || baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        SourceName = name;
    }

    public string SourceName { get; set; }

    /// <summary>当前已获取的更新日志正文（需先 <see cref="RefreshCache" />）。</summary>
    public string Changelog => _info?.Changelog ?? "";

    public bool IsAvailable()
    {
        return !string.IsNullOrWhiteSpace(_baseUrl);
    }

    public bool RefreshCache()
    {
        var content = Requester.FetchString($"{_baseUrl}apiv2/updateinfo.json", RequestParam.WithRetry);
        var info = Common.GetJson(content).ToObject<UpdateInfoModel>();
        if (info is null || string.IsNullOrWhiteSpace(info.LatestVersion))
            throw new Exception("updateinfo.json 缺少 latest_version 字段！");
        _info = info;
        return true;
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        if (_info is null)
            RefreshCache();
        return new VersionDataModel
        {
            VersionName = _info.LatestVersion,
            VersionCode = 0,
            Sha256 = "", // 下载直链不附带哈希，更新校验会自动跳过
            Source = SourceName,
            Changelog = _info.Changelog ?? "",
            ForceUpdate = _info.ForceUpdate,
            MinVersion = _info.MinVersion ?? ""
        };
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        if (_info is null)
            RefreshCache();
        return currentVersion >= SemVer.Parse(_info.LatestVersion);
    }

    public VersionAnnouncementDataModel GetAnnouncementList()
    {
        if (_info is null)
            RefreshCache();
        var result = new VersionAnnouncementDataModel { Content = new List<VersionAnnouncementContentModel>() };
        var body = _info.AnnounceBody ?? "";
        if (string.IsNullOrWhiteSpace(body))
            return result; // 正文留空 = 不发公告
        var title = string.IsNullOrWhiteSpace(_info.AnnounceTitle) ? "PCLCE-NS" : _info.AnnounceTitle;
        // Id 取内容摘要：公告内容变化后 Id 随之变化，会作为新公告再次展示
        var id = "cloud-" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(title + "\n" + body)))
            .ToLowerInvariant();
        result.Content.Add(new VersionAnnouncementContentModel
        {
            Title = title,
            Detail = body,
            Id = id,
            Date = ""
        });
        return result;
    }

    public List<Loading.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch,
        string output)
    {
        if (_info is null)
            RefreshCache();
        var url = (_info.DownloadURL ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
            throw new Exception(Lang.Text("Update.Error.UnableToGetUpdate"));
        var loaders = new List<Loading.LoaderBase>
        {
            new Loading.LoaderTask<int, List<DownloadFile>>(Lang.Text("Update.Task.GetVersionInfo"), load =>
            {
                load.output = new List<DownloadFile> { new(new[] { url }, output) };
            }),
            new LoaderDownload(Lang.Text("Update.Task.DownloadFile"), new List<DownloadFile>())
        };
        return loaders;
    }

    private class UpdateInfoModel
    {
        [JsonPropertyName("force_update")]
        public bool ForceUpdate { get; set; }

        [JsonPropertyName("latest_version")]
        public string LatestVersion { get; set; }

        [JsonPropertyName("min_version")]
        public string MinVersion { get; set; }

        [JsonPropertyName("download_url")]
        public string DownloadURL { get; set; }

        [JsonPropertyName("changelog")]
        public string Changelog { get; set; }

        [JsonPropertyName("announce_title")]
        public string AnnounceTitle { get; set; }

        [JsonPropertyName("announce_body")]
        public string AnnounceBody { get; set; }
    }
}
