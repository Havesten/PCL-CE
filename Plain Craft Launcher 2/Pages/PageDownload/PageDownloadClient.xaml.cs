using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageDownloadClient
{
    public PageDownloadClient()
    {
        Initialized += (_, _) => LoaderInit();
        Loaded += (_, _) => Init();
        InitializeComponent();
    }

    private void LoaderInit()
    {
        PageLoaderInit(Load, PanLoad, PanBack, null, GameDownload.dlClientListLoader,
            _ => Load_OnFinish());
    }

    private void Init()
    {
        PanBack.ScrollToHome();
    }

    private void Load_OnFinish()
    {
        // 结果数据化
        try
        {
            var categoryOrder = new[]
            {
                McVersionCategory.Release,
                McVersionCategory.Snapshot,
                McVersionCategory.BeforeRelease,
                McVersionCategory.AprilFools
            };

            var dict = categoryOrder.ToDictionary(
                category => category,
                _ => new List<JsonObject>()
            );

            var versions = GameDownload.dlClientListLoader.output.Value?["versions"] as JsonArray;
            if (versions is null || versions.Count == 0)
                throw new Exception("版本列表返回为空");
            foreach (JsonObject Version in versions)
            {
                var cat = McVersionClassifier.ClassifyVersion(Version);
                dict[cat].Add(Version);
            }

            foreach (var category in categoryOrder)
                dict[category] = dict[category]
                    .OrderByDescending(McVersionClassifier.GetReleaseTime)
                    .ToList();

            PanMain.Children.Clear();

            var cardInfo = new MyCard
            {
                Margin = new Thickness(0d, 0d, 0d, 15d)
            };
            LocalizedText.TryBind(
                cardInfo,
                "Title",
                () => Lang.Text("Download.Version.Latest.Title"));
            var topestVersions = new List<JsonObject>();
            var releases = dict[McVersionCategory.Release];
            var snapshots = dict[McVersionCategory.Snapshot];
            if (releases.Count > 0)
            {
                var release = (JsonObject)releases[0].DeepClone();
                release["lore"] = "Download.Version.Latest.Release";
                release["loreArgs"] = new JsonArray(
                    McVersionClassifier.GetReleaseTime(release).ToString("O"));
                release["loreArgsType"] = "date";
                topestVersions.Add(release);
                if (snapshots.Count > 0 &&
                    McVersionClassifier.GetReleaseTime(releases[0]) <
                    McVersionClassifier.GetReleaseTime(snapshots[0]))
                {
                    var snapshot = (JsonObject)snapshots[0].DeepClone();
                    snapshot["lore"] = "Download.Version.Latest.Development";
                    snapshot["loreArgs"] = new JsonArray(
                        McVersionClassifier.GetReleaseTime(snapshot).ToString("O"));
                    snapshot["loreArgsType"] = "date";
                    topestVersions.Add(snapshot);
                }
            }

            void PutMethod(StackPanel stack)
            {
                foreach (var item in (IEnumerable)stack.Tag)
                    stack.Children.Add(DownloadUi.McDownloadListItem((JsonObject)item,
                        DownloadUi.McDownloadMenuSave, true));
            }

            if (topestVersions.Count > 0)
            {
                var panInfo = new StackPanel
                {
                    Margin = new Thickness(20d, MyCard.SwapedHeight, 18d, 0d),
                    VerticalAlignment = VerticalAlignment.Top,
                    RenderTransform = new TranslateTransform(0d, 0d), Tag = topestVersions
                };

                MyCard.StackInstall(ref panInfo, PutMethod);
                cardInfo.Children.Add(panInfo);
                PanMain.Children.Add(cardInfo);
            }

            foreach (var Pair in dict)
            {
                if (!Pair.Value.Any())
                    continue;

                var newCard = new MyCard { Margin = new Thickness(0d, 0d, 0d, 15d) };
                var categoryKey = McVersionClassifier.GetCategoryKey(Pair.Key);
                var categoryCount = Pair.Value.Count;
                LocalizedText.TryBind(
                    newCard,
                    "Title",
                    () => $"{Lang.Text(categoryKey)} ({categoryCount})");
                var newStack = new StackPanel
                {
                    Margin = new Thickness(20d, MyCard.SwapedHeight, 18d, 0d),
                    VerticalAlignment = VerticalAlignment.Top, RenderTransform = new TranslateTransform(0d, 0d),
                    Tag = Pair.Value
                };
                newCard.Children.Add(newStack);
                newCard.SwapControl = newStack;
                newCard.InstallMethod = PutMethod;
                newCard.IsSwapped = true;
                PanMain.Children.Add(newCard);
            }

            // 一张卡都没建出来说明分类结果异常，宁可报错让用户点重试，也不要留个空白页
            if (PanMain.Children.Count == 0)
                throw new Exception("版本列表里没有任何可用的版本");
        }
        catch (Exception ex)
        {
            Common.Log(
                ex,
                "可视化 MC 版本列表出错",
                Common.LogLevel.Feedback,
                userSummary: Lang.Text("Download.Error.OperationFailed"));
        }
    }
}