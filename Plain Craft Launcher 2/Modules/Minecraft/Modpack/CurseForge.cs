using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using PCL.Core.App.Localization;
using PCL.Core.UI.Hints;
using PCL.Core.UI.Dialogs;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;
using static PCL.Loading;

namespace PCL;

public static partial class Modpack
{
    private static LoaderCombo<string> InstallPackCurseForge(
        string fileAddress,
        ZipArchive archive,
        string archiveBaseFolder,
        string? instanceName = null,
        string? logo = null,
        string? resourceId = null,
        bool isOnlineInstall = false)
    {
        var json = _ReadCurseForgeManifest(archive, archiveBaseFolder);
        if (json["minecraft"] is null || json["minecraft"]!["version"] is null)
            throw new Exception("CurseForge 整合包未提供 Minecraft 版本信息");

        instanceName = _ResolveModpackInstanceName(instanceName, json["name"]?.ToString() ?? "");
        var loaderVersions = _ParseCurseForgeLoaderVersions(json);

        var installTemp = Launcher.RequestTaskTempFolder();
        var installLoaders = new List<LoaderBase>();
        var overrideHome = json["overrides"]?.ToString() ?? "";
        if (!string.IsNullOrEmpty(overrideHome))
            installLoaders.Add(
                _CreateCurseForgeExtractLoader(
                    fileAddress,
                    archiveBaseFolder,
                    instanceName,
                    installTemp,
                    overrideHome));

        var (modList, modOptionalList) = _CollectCurseForgeModIds(json);
        if (modList.Count != 0)
            installLoaders.Add(
                _CreateCurseForgeModDownloadLoaders(
                    modList,
                    modOptionalList,
                    json,
                    instanceName,
                    loaderVersions.ModLoader));

        var request = _CreateCurseForgeInstallRequest(instanceName, json, loaderVersions);
        var mergeLoaders = DownloadUi.McInstallLoader(request);
        var loaders = _CreateCurseForgeLoaderList(
            installLoaders,
            mergeLoaders,
            fileAddress,
            json,
            instanceName,
            logo,
            resourceId);

        var loaderName = Lang.Text(
            "Minecraft.Download.Modpack.Task.CurseForgeInstall",
            instanceName);
        var loaderIdentifier = $"Modpack:CurseForge:{instanceName}";
        if (loaderTaskbar.Any(l => l.Identifier == loaderIdentifier))
        {
            HintService.Show(Hints.Error(Lang.Text("Minecraft.Download.Modpack.Installing")));
            throw new Common.CancelledException();
        }

        var loader = new LoaderCombo<string>(loaderName, loaders)
        {
            OnStateChanged = DownloadUi.McInstallState,
            Identifier = loaderIdentifier
        };
        loader.Start(request.targetInstanceFolder);
        // Start 在工作线程上是同步执行的，此处任务已到达终态。
        // 若安装失败，抛出真实原因，避免被后续的界面刷新异常掩盖。
        if (loader.State == Common.LoadState.Failed && loader.Error is not null)
            throw new Exception(loader.Error.Message, loader.Error);
        LoaderTaskbarAdd(loader);
        Launcher.frmMain!.BtnExtraDownload.ShowRefresh();
        if (!isOnlineInstall)
            Common.RunInUi(() => Launcher.frmMain.PageChange(FormMain.PageType.TaskManager));
        return loader;
    }

    private static JsonObject _ReadCurseForgeManifest(ZipArchive archive, string archiveBaseFolder)
    {
        try
        {
            return (JsonObject)
                Common.GetJson(
                    Common.ReadFile(archive.GetEntry($"{archiveBaseFolder}manifest.json")!.Open()));
        }
        catch (Exception ex)
        {
            throw new Exception("CurseForge 整合包安装信息存在问题", ex);
        }
    }

    private static CurseForgeLoaderVersions _ParseCurseForgeLoaderVersions(JsonObject json)
    {
        string? forgeVersion = null;
        string? neoForgeVersion = null;
        string? fabricVersion = null;
        var modLoader = Comp.CompLoaderType.Any;
        // 这里必须保持强类型遍历。JsonNode 上的 ToObject/StartsWithF 都是扩展方法，
        // 一旦把节点转成 dynamic，编译期就不再绑定扩展方法，只会在运行时抛 RuntimeBinderException。
        foreach (var entry in json["minecraft"]?["modLoaders"] as JsonArray ?? new JsonArray())
        {
            var id = (entry?["id"]?.ToString() ?? "").ToLowerInvariant();
            if (id.StartsWithF("forge-"))
            {
                if (id.Contains("recommended"))
                    throw new Exception(Lang.Text("Minecraft.Download.Modpack.TooOldUnsupported"));
                Common.Log($"[ModPack] 整合包 Forge 版本：{id}");
                forgeVersion = id.Replace("forge-", "");
                modLoader = Comp.CompLoaderType.Forge;
            }
            else if (id.StartsWithF("neoforge-"))
            {
                Common.Log($"[ModPack] 整合包 NeoForge 版本：{id}");
                neoForgeVersion = id.Replace("neoforge-", "");
                modLoader = Comp.CompLoaderType.NeoForge;
            }
            else if (id.StartsWithF("fabric-"))
            {
                try
                {
                    Common.Log($"[ModPack] 整合包 Fabric 版本：{id}");
                    fabricVersion = id.Replace("fabric-", "");
                    modLoader = Comp.CompLoaderType.Fabric;
                    break;
                }
                catch (Exception ex)
                {
                    Common.Log(ex, $"读取整合包 Fabric 版本失败：{id}");
                }
            }
            else if (id.StartsWithF("quilt-"))
            {
                throw new Exception(Lang.Text("Minecraft.Download.Modpack.QuiltUnsupported"));
            }
        }

        return new CurseForgeLoaderVersions(
            forgeVersion,
            neoForgeVersion,
            fabricVersion,
            modLoader);
    }

    private static LoaderBase _CreateCurseForgeExtractLoader(
        string fileAddress,
        string archiveBaseFolder,
        string instanceName,
        string installTemp,
        string overrideHome)
    {
        return new LoaderTask<string, int>(
            Lang.Text("Minecraft.Download.Modpack.Stage.ExtractModpack"),
            task =>
            {
                ExtractModpackFiles(installTemp, fileAddress, task, 0.6d);
                CopyOverrideDirectory(
                    Path.Combine(
                        installTemp,
                        archiveBaseFolder,
                        overrideHome is "." or "./" ? "" : overrideHome
                    ),
                    $@"{GameFolders.mcFolderSelected}versions\{instanceName}",
                    task,
                    0.4d);
            })
        {
            ProgressWeight = new FileInfo(fileAddress).Length / 1024d / 1024d / 6d,
            block = false
        };
    }

    /// <summary>
    ///     CurseForge 整合包 manifest 中的一条模组引用：projectID 用于按项目查询，fileID 用于定位具体版本。
    /// </summary>
    private readonly record struct CurseForgeModRef(int ProjectId, int FileId);

    private static (List<CurseForgeModRef> ModList, List<int> OptionalList) _CollectCurseForgeModIds(
        JsonObject json)
    {
        var modList = new List<CurseForgeModRef>();
        var modOptionalList = new List<int>();

        // 强类型遍历：manifest 里的 files 既可能缺失，也可能不是数组（个别打包工具写出对象），
        // 这种情况下按「没有可下载的模组」处理，交给后续流程报明确错误。
        if (json["files"] is not JsonArray files)
        {
            Common.Log("[ModPack] manifest.json 中没有可用的 files 数组");
            return (modList, modOptionalList);
        }

        foreach (var modEntry in files.OfType<JsonObject>())
        {
            try
            {
                // CurseForge 官方示例用 projectID / fileID，但部分打包工具写成 projectId / fileId。
                var projectIdNode = modEntry["projectID"] ?? modEntry["projectId"];
                var fileIdNode = modEntry["fileID"] ?? modEntry["fileId"];
                if (!_TryGetModId(projectIdNode, out var projectId) || !_TryGetModId(fileIdNode, out var fileId))
                {
                    HintService.Show(Hints.Info(Lang.Text(
                        "Minecraft.Download.Modpack.ModMissingRequiredInfoSkipped",
                        modEntry["slug"] ?? modEntry["projectID"] ?? modEntry.ToJsonString())));
                    continue;
                }

                modList.Add(new CurseForgeModRef(projectId, fileId));
                if (modEntry["required"] is JsonNode requiredNode && !_TryGetBool(requiredNode, true))
                    modOptionalList.Add(fileId);
            }
            catch (Exception ex)
            {
                Common.Log(ex, $"[ModPack] 跳过一条无法解析的模组记录：{modEntry.ToJsonString()}");
            }
        }

        return (modList, modOptionalList);
    }

    /// <summary>
    ///     读取 CurseForge 的数字 ID。少数整合包把它写成字符串，
    ///     这里统一按宽松方式解析，解析不出来就认为这条记录不可用。
    /// </summary>
    private static bool _TryGetModId(JsonNode? node, out int value)
    {
        value = 0;
        if (node is null)
            return false;
        try
        {
            value = node.ToObject<int>();
            return value > 0;
        }
        catch (Exception ex)
        {
            Common.Log(ex, $"[ModPack] 无法解析模组 ID：{node.ToJsonString()}");
            return false;
        }
    }

    private static bool _TryGetBool(JsonNode? node, bool fallback)
    {
        if (node is null)
            return fallback;
        try
        {
            return node.ToObject<bool>();
        }
        catch (Exception ex)
        {
            Common.Log(ex, $"[ModPack] 无法解析布尔字段：{node.ToJsonString()}");
            return fallback;
        }
    }

    private static LoaderBase _CreateCurseForgeModDownloadLoaders(
        List<CurseForgeModRef> modList,
        List<int> modOptionalList,
        JsonObject json,
        string instanceName,
        Comp.CompLoaderType modLoader)
    {
        var modDownloadLoaders = new List<LoaderBase>
        {
            new LoaderTask<int, JsonArray>(
                Lang.Text("Minecraft.Download.Modpack.Stage.PrepareModsDownloadInfo"),
                task => _FetchCurseForgeModFiles(modList, task))
            {
                ProgressWeight = modList.Count / 10d
            },
            new LoaderTask<JsonArray, List<DownloadFile>>(
                Lang.Text("Minecraft.Download.Modpack.Stage.BuildModsDownloadInfo"),
                task =>
                    _BuildCurseForgeDownloadFiles(
                        task,
                        modList,
                        modOptionalList,
                        json,
                        instanceName,
                        modLoader))
            {
                ProgressWeight = modList.Count / 200d,
                show = false
            },
            new LoaderDownload(Lang.Text("Minecraft.Download.Modpack.Stage.DownloadMods"), [])
            {
                ProgressWeight = modList.Count * 1.5d
            }
        };
        return new LoaderCombo<int>(
            Lang.Text("Minecraft.Download.Modpack.Stage.DownloadMods.MainLoader"),
            modDownloadLoaders)
        {
            show = false,
            ProgressWeight = modDownloadLoaders.Sum(l => l.ProgressWeight)
        };
    }

    private static void _FetchCurseForgeModFiles(List<CurseForgeModRef> modList,
        LoaderTask<int, JsonArray> task)
    {
        var refs = modList
            .GroupBy(r => r.FileId)
            .Select(g => g.First())
            .ToList();
        var fileIdList = refs.Select(r => r.FileId).ToList();
        var fetched = new ConcurrentDictionary<int, JsonObject>();

        // 批量接口：官方 api.curseforge.com 需要 x-api-key，镜像则不保证支持 POST。
        // 任一条件不满足时这里会抛异常，由下方的逐个查询兜底，避免整个导入直接失败。
        try
        {
            var response = (JsonObject)Common.GetJson(
                GameDownload.DlModRequest(
                    "https://api.curseforge.com/v1/mods/files",
                    "POST",
                    $"{{\"fileIds\": [{fileIdList.Join(",")}]}}",
                    "application/json",
                    true));
            if (response["data"] is JsonArray data)
                foreach (var node in data.OfType<JsonObject>())
                    if (_TryGetModId(node["id"], out var fileId))
                        fetched[fileId] = node;
            Common.Log($"[Modpack] 批量接口返回 {fetched.Count}/{refs.Count} 个模组文件信息");
        }
        catch (Exception ex)
        {
            Common.Log(ex, "[Modpack] 批量获取 CurseForge 模组文件信息失败，改为逐个查询");
        }

        // 逐个查询兜底：GET /v1/mods/{projectID}/files/{fileID}。
        // 未配置 API Key 时官方批量接口不可用，而镜像普遍支持该 GET 端点。
        var missing = refs.Where(r => !fetched.ContainsKey(r.FileId)).ToList();
        var failures = new ConcurrentBag<(CurseForgeModRef Ref, Exception Error)>();
        if (missing.Count != 0)
        {
            Common.Log($"[Modpack] 逐个查询 {missing.Count} 个模组文件信息");
            Parallel.ForEach(
                missing,
                new ParallelOptions { MaxDegreeOfParallelism = 6 },
                modRef =>
                {
                    try
                    {
                        var json = _FetchCurseForgeModFile(modRef);
                        // 按请求的 fileId 记账：上游返回的 id 偶有缺失，直接用返回 id 会漏项。
                        if (json["data"] is JsonObject fileJson)
                            fetched[modRef.FileId] = fileJson;
                    }
                    catch (Exception ex)
                    {
                        failures.Add((modRef, ex));
                    }
                });

            foreach (var (modRef, error) in failures)
                Common.Log(
                    error,
                    $"[Modpack] 获取模组文件信息失败（project={modRef.ProjectId}, file={modRef.FileId}）");
        }

        if (fetched.Count == 0)
        {
            // 全都拿不到时最常见的原因是云端 CurseForge 反代没配置密钥或网络不通，
            // 直接抛「模组已被删除」会把人误导到错误方向，这里把首个真实错误带上。
            var firstError = failures.Select(f => f.Error).FirstOrDefault(e => e is not null);
            throw new Exception(
                Lang.Text("Minecraft.Download.Modpack.SomeModsDeleted")
                + (firstError is null ? "" : $"\n{firstError.Message}"),
                firstError);
        }

        if (fetched.Count < refs.Count)
            Common.Log($"[Modpack] 有 {refs.Count - fetched.Count} 个模组文件信息缺失，已跳过这些模组");

        var files = new JsonArray();
        foreach (var file in fetched.Values)
            files.Add(file.DeepClone());

        task.output = files;
    }

    /// <summary>
    ///     获取单个模组文件的详细信息。优先走镜像：官方源需要 API Key，
    ///     若逐个查询都先等官方超时，导入过程会被拖得非常慢。
    ///     真正回落到官方时走自建云端（客户端不内置 Key），由云端注入密钥转发。
    /// </summary>
    private static JsonObject _FetchCurseForgeModFile(CurseForgeModRef modRef)
    {
        var official = $"https://api.curseforge.com/v1/mods/{modRef.ProjectId}/files/{modRef.FileId}";
        var mirror = GameDownload.DlSourceModGet(official);
        if (mirror != official)
            try
            {
                return (JsonObject)Common.GetJson(
                    Requester.FetchString(
                        mirror,
                        new RequestParam { Timeout = 10000, Retries = 2, UseBrowserUserAgent = true }));
            }
            catch (Exception ex)
            {
                Common.Log(
                    ex,
                    $"[Modpack] 镜像获取模组文件信息失败，回退官方源（project={modRef.ProjectId}, file={modRef.FileId}）");
            }

        return (JsonObject)Common.GetJson(
            Requester.FetchString(
                GameDownload.DlSourceModCloudGet(official),
                new RequestParam { Timeout = 15000, Retries = 2, UseBrowserUserAgent = true }));
    }

    private static List<DownloadFile> _BuildCurseForgeDownloadFiles(
        LoaderTask<JsonArray, List<DownloadFile>> task,
        List<CurseForgeModRef> modList,
        List<int> modOptionalList,
        JsonObject json,
        string instanceName,
        Comp.CompLoaderType modLoader)
    {
        var fileList = new Dictionary<int, DownloadFile>();
        foreach (var modJson in task.input!.OfType<JsonObject>())
        {
            // 上游响应偶尔缺 id；用负数补位，避免几条记录都落到同一个键上互相覆盖
            var id = _TryGetModId(modJson["id"], out var parsedId) ? parsedId : -fileList.Count - 1;
            if (fileList.ContainsKey(id))
                continue;
            if (modOptionalList.Contains(id)
                && DialogService.Instance.Show(new TextDialogRequest
                    {
                        Title = Lang.Text("Minecraft.Download.Modpack.OptionalFile.Title"),
                        Content = Lang.Text(
                            "Minecraft.Download.Modpack.OptionalFile.Message",
                            modJson["displayName"]
                        ),
                        Buttons = DialogButtons.From(
                            DialogButton.Create(Lang.Text("Minecraft.Download.Modpack.OptionalFile.Download")),
                            DialogButton.Create(Lang.Text("Minecraft.Download.Modpack.OptionalFile.Skip"))),
                        ForceWait = true
                    }).Result
                    ?.Button
                == 2)
                continue;

            var (targetFolder, type) = _ResolveCurseForgeTargetFolder(modJson);
            var file = new Comp.CompFile(modJson, type);
            if (!file.Available)
                continue;

            fileList.Add(
                id,
                file.ToNetFile(
                    $@"{GameFolders.mcFolderSelected}versions\{instanceName}\{targetFolder}\",
                    Comp.DownloadReason.ModPack,
                    json["minecraft"]!["version"]!.ToString(),
                    modLoader));
            task.Progress += 1d / (1 + modList.Count);
        }

        return [.. fileList.Values];
    }

    private static (string Folder, Comp.CompType Type) _ResolveCurseForgeTargetFolder(
        JsonObject modJson)
    {
        // modules 在部分镜像/精简响应里可能缺失，缺失时按普通模组处理
        if (modJson["modules"] is not JsonArray modules || modules.Count == 0)
            return ("mods", Comp.CompType.Mod);

        var moduleNames = modules
            .Select(l => l?["name"]?.ToString() ?? "")
            .ToList();
        if (moduleNames.Contains("META-INF")
            || moduleNames.Contains("mcmod.info")
            || (modJson["FileName"]?.ToString()?.EndsWithF(".jar", true)).GetValueOrDefault())
            return ("mods", Comp.CompType.Mod);
        if (moduleNames.Contains("pack.mcmeta"))
            return ("resourcepacks", Comp.CompType.ResourcePack);
        if (moduleNames.Contains("level.dat"))
            return ("saves", Comp.CompType.World);
        return ("shaderpacks", Comp.CompType.Shader);
    }

    private static DownloadUi.McInstallRequest _CreateCurseForgeInstallRequest(
        string instanceName,
        JsonObject json,
        CurseForgeLoaderVersions loaderVersions)
    {
        return new DownloadUi.McInstallRequest
        {
            targetInstanceName = instanceName,
            targetInstanceFolder = $@"{GameFolders.mcFolderSelected}versions\{instanceName}\",
            minecraftName = json["minecraft"]!["version"]!.ToString(),
            forgeVersion = loaderVersions.ForgeVersion,
            neoForgeVersion = loaderVersions.NeoForgeVersion,
            fabricVersion = loaderVersions.FabricVersion
        };
    }

    private static List<LoaderBase> _CreateCurseForgeLoaderList(
        List<LoaderBase> installLoaders,
        List<LoaderBase> mergeLoaders,
        string fileAddress,
        JsonObject json,
        string instanceName,
        string? logo,
        string? resourceId)
    {
        var loaders = _CreateModpackInstallLoaderList(installLoaders, mergeLoaders);
        loaders.Add(
            new LoaderTask<string, string>(
                Lang.Text("Minecraft.Download.Modpack.Stage.FinalizeFiles"),
                _ => _FinalizeModpackInstance(
                    fileAddress,
                    instanceName,
                    logo,
                    resourceId,
                    "CurseForge",
                    json["version"]?.ToString()))
            {
                ProgressWeight = 0.1d,
                show = false
            });
        return loaders;
    }

    private sealed record CurseForgeLoaderVersions(
        string? ForgeVersion,
        string? NeoForgeVersion,
        string? FabricVersion,
        Comp.CompLoaderType ModLoader);
}