using PCL.Core.App.Localization;
using PCL.Core.UI.Hints;
using PCL.Core.App.Region;

namespace PCL;

/// <summary>
///     区域功能策略，是 Minecraft 下载、补全与整合包安装等功能的统一门禁。
///     本构建已禁用该策略：所有检查恒返回可用，不再按区域拦截任何功能。
/// </summary>
internal static class RestrictedFeaturePolicy
{
    /// <summary>
    ///     区域限制策略开关。本构建为 <c>false</c>；置为 <c>true</c> 即可恢复上游 PCL CE 的原始行为。
    ///     使用 <c>static readonly</c> 而非 <c>const</c>，以免编译器将后续分支判定为不可达代码。
    /// </summary>
    private static readonly bool PolicyEnabled = false;

    internal static RegionAssessment Assess() => RegionPolicy.Evaluate(RegionPolicy.ReadWindowsSignals());

    /// <summary>
    ///     当前是否处于受限区域。策略禁用时恒为 <c>false</c>。
    /// </summary>
    internal static bool IsRestricted => PolicyEnabled && Assess().IsRestricted;

    internal static bool Check(string feature, bool showHint = true)
    {
        if (!PolicyEnabled) return true;

        var assessment = Assess();
        if (!assessment.IsRestricted) return true;

        Common.Log($"[RegionPolicy] 已阻止功能 {feature}，判定原因：{assessment.Reason}");
        if (showHint)
        {
            HintService.Show(Hints.Error(Lang.Text(assessment.Classification == RegionClassification.Unknown
                ? "Region.Feature.Unknown"
                : "Region.Feature.Restricted")));
        }
        return false;
    }

    internal static void ThrowIfRestricted(string feature)
    {
        if (!PolicyEnabled) return;
        if (Check(feature, false)) return;
        throw new Common.CancelledException();
    }
}
