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
    ///     采集 manifest 里要下载的模组。与上游保持一致：只记 fileID，
    ///     后续统一用批量接口查询，不再按 projectID 逐个补查。
    /// </summary>
    private static (List<int> ModList, List<int> OptionalList) _CollectCurseForgeModIds(
        JsonObject json)
    {
        var modList = new List<int>();
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
                if (!_TryGetModId(projectIdNode, out _) || !_TryGetModId(fileIdNode, out var fileId))
                {
                    HintService.Show(Hints.Info(Lang.Text(
                        "Minecraft.Download.Modpack.ModMissingRequiredInfoSkipped",
                        modEntry["slug"] ?? modEntry["projectID"] ?? modEntry.ToJsonString())));
                    continue;
                }

                modList.Add(fileId);
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
        List<int> modList,
        List<int> modOptionalList,
        JsonObject json,
        string instanceName,
        Comp.CompLoaderType modLoader)
    {
        // 「取信息」与「构造任务」拆成两个加载器只是为了进度条好看，文件数组改用闭包共享：
        // 不再依赖加载器之间的 output → input 传递。那一环一旦没接上，整合包会「安装成功但一个模组都不下载」，
        // 现场日志里只能看到「下载模组 0 个任务」，完全定位不到原因。
        var fetchedModFiles = new JsonArray();
        var modDownloadLoaders = new List<LoaderBase>
        {
            new LoaderTask<int, JsonArray>(
                Lang.Text("Minecraft.Download.Modpack.Stage.PrepareModsDownloadInfo"),
                task => _FetchCurseForgeModFiles(modList, task, fetchedModFiles))
            {
                ProgressWeight = modList.Count / 10d
            },
            new LoaderTask<JsonArray, List<DownloadFile>>(
                Lang.Text("Minecraft.Download.Modpack.Stage.BuildModsDownloadInfo"),
                task =>
                    _BuildCurseForgeDownloadFiles(
                        task,
                        fetchedModFiles,
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

    /// <summary>
    ///     批量查询模组文件信息。与上游实现保持一致：只用一次 POST /v1/mods/files 拿全部，
    ///     镜像源返回的数量不足时改为强制走官方源重试，而不是逐个 GET 补查——
    ///     逐个补查拿到的 JSON 字段不全（缺 modules / fileName），会把模组塞错目录或漏装。
    /// </summary>
    private static void _FetchCurseForgeModFiles(
        List<int> modList,
        LoaderTask<int, JsonArray> task,
        JsonArray sink)
    {
        var fileIds = modList.Distinct().ToList();
        var allowMirror = true;
        JsonArray files;
        var tryCount = 0;
        do
        {
            tryCount += 1;
            var response = (JsonObject)Common.GetJson(
                GameDownload.DlModRequest(
                    "https://api.curseforge.com/v1/mods/files",
                    "POST",
                    $"{{\"fileIds\": [{fileIds.Join(",")}]}}",
                    "application/json",
                    allowMirror));
            files = response["data"] as JsonArray ?? new JsonArray();
            if (fileIds.Count <= files.Count)
            {
                Common.Log($"[Modpack] 已获取 {files.Count} 个模组文件信息，开始进行下一步");
                break;
            }

            allowMirror = false;
            Common.Log($"[Modpack] 获取到的模组数量不达标（{files.Count}/{fileIds.Count}），" +
                       $"重试时禁用镜像源（第 {tryCount} 次）");
            if (tryCount > 3)
                throw new Exception(Lang.Text("Minecraft.Download.Modpack.SomeModsDeleted"));
        } while (true);

        // 写进共享数组：DeepClone 是因为这些节点还挂在响应文档上，带父节点直接 Add 会抛异常。
        sink.Clear();
        foreach (var node in files)
            sink.Add(node?.DeepClone());
        task.output = sink;
    }

    private static List<DownloadFile> _BuildCurseForgeDownloadFiles(
        LoaderTask<JsonArray, List<DownloadFile>> task,
        JsonArray modFiles,
        List<int> modList,
        List<int> modOptionalList,
        JsonObject json,
        string instanceName,
        Comp.CompLoaderType modLoader)
    {
        var fileList = new Dictionary<int, DownloadFile>();
        var candidateCount = 0;
        var emptyNameCount = 0;
        var noUrlCount = 0;
        var minecraftVersion = json["minecraft"]!["version"]!.ToString();
        var installFolder = $@"{GameFolders.mcFolderSelected}versions\{instanceName}\";

        // 优先读闭包里的数组；万一没写进去（加载器被重启等），再退回加载器输入。
        var source = modFiles.Count != 0 ? modFiles : task.input;
        foreach (var modJson in source?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            candidateCount += 1;
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
            var localAddress = $@"{installFolder}{targetFolder}\";
            var file = new Comp.CompFile(modJson, type);
            if (file.Available)
            {
                fileList.Add(
                    id,
                    file.ToNetFile(
                        localAddress,
                        Comp.DownloadReason.ModPack,
                        minecraftVersion,
                        modLoader));
                task.Progress += 1d / (1 + modList.Count);
                continue;
            }

            // 兜底：CompFile 认为下载信息不全时，只要还有文件名，就自己按 fileId 拼 CurseForge CDN 地址。
            // 少了这一步，镜像少给一个 downloadUrl 就会让整个整合包「一个模组都不下载」。
            if (string.IsNullOrEmpty(file.FileName))
            {
                emptyNameCount += 1;
                Common.Log($"[Modpack] 跳过模组文件 {id}：响应里没有文件名");
                continue;
            }

            var fallbackUrls = _BuildCurseForgeFallbackUrls(id, file.FileName);
            if (fallbackUrls.Count == 0)
            {
                noUrlCount += 1;
                Common.Log($"[Modpack] 跳过模组文件 {id}：无法推出下载地址");
                continue;
            }

            fileList.Add(
                id,
                new DownloadFile(
                    Comp.CompFile.HandleModrinthDownloadUrls(
                        fallbackUrls,
                        Comp.DownloadReason.ModPack,
                        minecraftVersion,
                        modLoader),
                    localAddress + Comp.CompFileNameSanitize(file.FileName),
                    new Common.FileChecker(hash: file.Hash),
                    true));
            Common.Log($"[Modpack] 模组文件 {id} 缺下载地址，改用 CDN 兜底：{fallbackUrls[0]}");
            task.Progress += 1d / (1 + modList.Count);
        }

        Common.Log(
            $"[Modpack] 模组文件共 {candidateCount} 条，可用 {fileList.Count} 条"
            + (emptyNameCount + noUrlCount == 0
                ? ""
                : $"（缺文件名 {emptyNameCount} 条，无可用下载地址 {noUrlCount} 条）"));

        // 拿到了文件信息却一条任务都没构造出来，说明字段与预期不符。
        // 这里必须报错：静默「安装成功但没装模组」比失败更难排查。
        if (fileList.Count == 0 && candidateCount != 0)
            throw new Exception(Lang.Text("Minecraft.Download.Modpack.SomeModsDeleted"));

        return [.. fileList.Values];
    }

    /// <summary>
    ///     按 CurseForge CDN 的目录规则拼兜底下载地址：fileId 除 1000 为目录、余数为文件名编号。
    /// </summary>
    private static List<string> _BuildCurseForgeFallbackUrls(int fileId, string fileName)
    {
        if (fileId <= 0)
            return [];

        var url =
            $"https://edge.forgecdn.net/files/{fileId / 1000}/{fileId % 1000}/{Uri.EscapeDataString(fileName)}";
        return GameDownload.DlSourceModDownloadGet(Comp.CompFile.HandleCurseForgeDownloadUrls(url));
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
            // CurseForge 返回的字段名是小驼峰 fileName，写成 FileName 永远匹配不到 .jar
            || (modJson["fileName"]?.ToString()?.EndsWithF(".jar", true)).GetValueOrDefault())
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