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
        foreach (var entry in (dynamic)json["minecraft"]!["modLoaders"]! ?? Array.Empty<JsonNode>())
        {
            string id = (entry["id"] ?? "").ToString().ToLower();
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
        foreach (var modEntry in (dynamic)json["files"]! ?? Array.Empty<JsonNode>())
        {
            // CurseForge 官方示例用 projectID / fileID，但部分打包工具写成 projectId / fileId。
            var projectIdNode = modEntry["projectID"] ?? modEntry["projectId"];
            var fileIdNode = modEntry["fileID"] ?? modEntry["fileId"];
            if (projectIdNode is null || fileIdNode is null)
            {
                HintService.Show(Hints.Info(Lang.Text(
                        "Minecraft.Download.Modpack.ModMissingRequiredInfoSkipped",
                        modEntry)));
                continue;
            }

            var fileId = fileIdNode.ToObject<int>();
            modList.Add(new CurseForgeModRef(projectIdNode.ToObject<int>(), fileId));
            if (modEntry["required"] is JsonNode requiredNode && !requiredNode.ToObject<bool>())
                modOptionalList.Add(fileId);
        }

        return (modList, modOptionalList);
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
                    if (node["id"] is not null)
                        fetched[node["id"]!.ToObject<int>()] = node;
            Common.Log($"[Modpack] 批量接口返回 {fetched.Count}/{refs.Count} 个模组文件信息");
        }
        catch (Exception ex)
        {
            Common.Log(ex, "[Modpack] 批量获取 CurseForge 模组文件信息失败，改为逐个查询");
        }

        // 逐个查询兜底：GET /v1/mods/{projectID}/files/{fileID}。
        // 未配置 API Key 时官方批量接口不可用，而镜像普遍支持该 GET 端点。
        var missing = refs.Where(r => !fetched.ContainsKey(r.FileId)).ToList();
        if (missing.Count != 0)
        {
            Common.Log($"[Modpack] 逐个查询 {missing.Count} 个模组文件信息");
            var failures = new ConcurrentBag<(CurseForgeModRef Ref, Exception Error)>();
            Parallel.ForEach(
                missing,
                new ParallelOptions { MaxDegreeOfParallelism = 6 },
                modRef =>
                {
                    try
                    {
                        var json = _FetchCurseForgeModFile(modRef);
                        if (json["data"] is JsonObject fileJson && fileJson["id"] is not null)
                            fetched[fileJson["id"]!.ToObject<int>()] = fileJson;
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
            throw new Exception(Lang.Text("Minecraft.Download.Modpack.SomeModsDeleted"));

        if (fetched.Count < refs.Count)
            Common.Log($"[Modpack] 有 {refs.Count - fetched.Count} 个模组文件信息缺失，已跳过这些模组");

        var files = new JsonArray();
        foreach (var file in fetched.Values)
            files.Add(file.DeepClone());

        task.output = files;
    }

    /// <summary>
    ///     获取单个模组文件的详细信息。优先走镜像：未配置 API Key 时官方源必然返回 403，
    ///     若逐个查询都先等官方超时，导入过程会被拖得非常慢。
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
                official,
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
            var id = modJson["id"].ToObject<int>();
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
        if (modJson["modules"]!.AsArray().Count == 0)
            return ("mods", Comp.CompType.Mod);

        var moduleNames = modJson["modules"]!
            .AsArray()
            .Select(l => l!["name"]!.ToString())
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