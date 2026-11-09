namespace PCL;

public class VersionDataModel
{
    public string Changelog { get; set; }
    public string Sha256 { get; set; }
    public string Source { get; set; }
    public int VersionCode { get; set; }
    public string VersionName { get; set; }

    /// <summary>云端「是否强制更新」开关：为 true 时不允许跳过该版本。</summary>
    public bool ForceUpdate { get; set; }

    /// <summary>云端「强制最低版本」：当前版本低于它的客户端强制更新（留空 = 不按版本强制）。</summary>
    public string MinVersion { get; set; }
}