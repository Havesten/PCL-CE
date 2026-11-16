using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL;

/// <summary>
///     自更新的失败记忆。替换失败时由更新器写入，主程序下次启动前读取，
///     用于「同一个版本连续失败就不再自动重试」，避免部分设备陷入反复更新。
///     <para>
///         刻意不用 <c>Common.Log</c> / 配置系统：更新器进程里它们都还没初始化，
///         这里只做最朴素的文件读写，失败也不抛异常。
///     </para>
/// </summary>
internal static class UpdateStateStore
{
    private sealed class UpdateState
    {
        [JsonPropertyName("failed_version")] public string? FailedVersion { get; set; }

        [JsonPropertyName("fail_count")] public int FailCount { get; set; }

        [JsonPropertyName("last_attempt")] public DateTime? LastAttempt { get; set; }
    }

    /// <summary>自动重试的冷却时间：超过这个时间仍允许再试一次，避免一次偶发失败就永久放弃。</summary>
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromHours(12);

    /// <summary>连续失败达到这个次数后，自动更新让位给「请手动下载」提示。</summary>
    private const int MaxAutoAttempts = 2;

    private static string PathFor(string installDir) =>
        Path.Combine(installDir, "PCL", "Update", "update-state.json");

    /// <summary>
    ///     该版本是否已经自动失败到不该再自动重试的程度。
    /// </summary>
    public static bool ShouldSkipAutoUpdate(string installDir, string version)
    {
        var state = Read(installDir);
        if (state?.FailedVersion is null || state.FailCount < MaxAutoAttempts)
            return false;
        if (!string.Equals(state.FailedVersion, version, StringComparison.OrdinalIgnoreCase))
            return false;
        if (state.LastAttempt is { } last && DateTime.Now - last > RetryCooldown)
            return false; // 冷却结束，允许再试
        return true;
    }

    /// <summary>记录一次替换失败；同一个版本累加，换版本重新计数。</summary>
    public static void RecordFailure(string installDir, string version)
    {
        var state = Read(installDir) ?? new UpdateState();
        if (!string.Equals(state.FailedVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            state.FailedVersion = version;
            state.FailCount = 0;
        }

        state.FailCount++;
        state.LastAttempt = DateTime.Now;
        Write(installDir, state);
    }

    /// <summary>替换成功，清掉失败记忆。</summary>
    public static void Clear(string installDir)
    {
        var state = Read(installDir);
        if (state is null)
            return;
        try
        {
            File.Delete(PathFor(installDir));
        }

        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就把计数清零，效果一样
            state.FailCount = 0;
            state.FailedVersion = null;
            state.LastAttempt = null;
            Write(installDir, state);
        }
    }

    private static UpdateState? Read(string installDir)
    {
        try
        {
            var path = PathFor(installDir);
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(path));
        }

        catch
        {
            // 文件可能正在被写、也可能被用户改坏：当作没有记录
            return null;
        }
    }

    private static void Write(string installDir, UpdateState state)
    {
        try
        {
            var path = PathFor(installDir);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(state));
        }

        catch
        {
            // 状态文件写不进去不影响更新流程本身
        }
    }
}
