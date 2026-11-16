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
    private static LoaderCombo<string> InstallPackModrinth(
        string fileAddress,
        ZipArchive archive,
        string archiveBaseFolder,
        string? instanceName = null,
        string? logo = null,
        string? resourceId = null,
        bool isOnlineInstall = false)
    {
        var json = _ReadModrinthManifest(archive, archiveBaseFolder);
        var dependencies = json["dependencies"] as JsonObject;
        if (dependencies?["minecraft"] is null)
            throw new Exception("Modrinth 整合包未提供 Minecraft 版本信息");

        var loaderVersions = _ParseModrinthDependencies(dependencies);
        instanceName = _ResolveModpackInstanceName(instanceName, json["name"]?.ToString() ?? "");

        var installTemp = Launcher.RequestTaskTempFolder();
        var installLoaders = new List<LoaderBase>
        {
            _CreateModrinthExtractLoader(fileAddress, archiveBaseFolder, instanceName, installTemp)
        };
        var fileList = _BuildModrinthDownloadFiles(
            json,
            instanceName,
            loaderVersions.MinecraftVersion,
            loaderVersions.ModLoader);
        if (fileList.Any())
            installLoaders.Add(
                new LoaderDownload(
                    Lang.Text("Minecraft.Download.Modpack.Stage.DownloadAdditions"),
                    fileList)
                {
                    ProgressWeight = fileList.Count * 1.5d
                });

        var request = new DownloadUi.McInstallRequest
        {
            targetInstanceName = instanceName,
            targetInstanceFolder = $@"{GameFolders.mcFolderSelected}versions\{instanceName}\",
            minecraftName = loaderVersions.MinecraftVersion,
            forgeVersion = loaderVersions.ForgeVersion,
            neoForgeVersion = loaderVersions.NeoForgeVersion,
            fabricVersion = loaderVersions.FabricVersion
        };
        var mergeLoaders = DownloadUi.McInstallLoader(request);
        var loaders = _CreateModrinthLoaderList(
            installLoaders,
            mergeLoaders,
            fileAddress,
            json,
            instanceName,
            logo,
            resourceId);

        var loaderName = Lang.Text("Minecraft.Download.Modpack.Task.ModrinthInstall", instanceName);
        var loaderIdentifier = $"Modpack:Modrinth:{instanceName}";
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
        LoaderTaskbarAdd(loader);
        Launcher.frmMain!.BtnExtraDownload.ShowRefresh();
        if (!isOnlineInstall)
            Common.RunInUi(() => Launcher.frmMain.PageChange(FormMain.PageType.TaskManager));
        return loader;
    }

    private static JsonObject _ReadModrinthManifest(ZipArchive archive, string archiveBaseFolder)
    {
        try
        {
            return (JsonObject)
                Common.GetJson(
                    Common.ReadFile(
                        archive.GetEntry($"{archiveBaseFolder}modrinth.index.json")!.Open()));
        }
        catch (Exception ex)
        {
            throw new Exception("Modrinth 整合包安装信息存在问题", ex);
        }
    }

    private static ModrinthLoaderVersions _ParseModrinthDependencies(JsonObject dependencies)
    {
        string? minecraftVersion = null;
        string? forgeVersion = null;
        string? neoForgeVersion = null;
        string? fabricVersion = null;

        var modLoader = Comp.CompLoaderType.Any;
        foreach (var entry in dependencies)
            switch (entry.Key.ToLower() ?? "")
            {
                case "minecraft":
                    minecraftVersion = entry.Value?.ToObject<string>();
                    break;

                case "forge":
                    forgeVersion = entry.Value?.ToObject<string>();
                    modLoader = Comp.CompLoaderType.Forge;
                    Common.Log($"[ModPack] 整合包 Forge 版本：{forgeVersion}");
                    break;

                case "neoforge":
                case "neo-forge":
                    neoForgeVersion = entry.Value?.ToObject<string>();
                    modLoader = Comp.CompLoaderType.NeoForge;
                    Common.Log($"[ModPack] 整合包 NeoForge 版本：{neoForgeVersion}");
                    break;

                case "fabric-loader":
                    fabricVersion = entry.Value?.ToObject<string>();
                    modLoader = Comp.CompLoaderType.Fabric;
                    Common.Log($"[ModPack] 整合包 Fabric 版本：{fabricVersion}");
                    break;

                case "quilt-loader":
                    throw new Exception(Lang.Text("Minecraft.Download.Modpack.QuiltUnsupported"));

                default:
                    HintService.Show(Hints.Error(Lang.Text(
                            "Minecraft.Download.Modpack.UnknownLoader",
                            entry.Key,
                            entry.Value
                        )));
                    break;
            }

        return new ModrinthLoaderVersions(
            minecraftVersion,
            forgeVersion,
            neoForgeVersion,
            fabricVersion,
            modLoader);
    }

    private static LoaderBase _CreateModrinthExtractLoader(
        string fileAddress,
        string archiveBaseFolder,
        string instanceName,
        string installTemp)
    {
        return new LoaderTask<string, int>(
            Lang.Text("Minecraft.Download.Modpack.Stage.ExtractModpack"),
            task =>
            {
                ExtractModpackFiles(installTemp, fileAddress, task, 0.5d);
                CopyOverrideDirectory(
                    Path.Combine(installTemp, archiveBaseFolder, "overrides"),
                    Path.Combine(GameFolders.mcFolderSelected!, "versions", instanceName),
                    task,
                    0.4d);
                CopyOverrideDirectory(
                    Path.Combine(installTemp, archiveBaseFolder, "client-overrides"),
                    Path.Combine(GameFolders.mcFolderSelected!, "versions", instanceName),
                    task,
                    0.1d);
            })
        {
            ProgressWeight = new FileInfo(fileAddress).Length / 1024d / 1024d / 6d,
            block = false
        };
    }

    private static List<DownloadFile> _BuildModrinthDownloadFiles(
        JsonObject json,
        string instanceName,
        string? minecraftVersion,
        Comp.CompLoaderType modLoader)
    {
        var fileList = new List<DownloadFile>();
        // 与 CurseForge 侧同一约定：一律强类型遍历，不要用 dynamic 访问 JsonNode，
        // 否则 ToObject 这类扩展方法在运行时无法绑定。
        if (json["files"] is not JsonArray files)
        {
            Common.Log("[ModPack] modrinth.index.json 中没有可用的 files 数组");
            return fileList;
        }

        foreach (var file in files.OfType<JsonObject>())
        {
            if (!_ShouldIncludeModrinthFile(file))
                continue;

            var urls = (file["downloads"] as JsonArray ?? new JsonArray())
                .Select(x => Comp.CompFile.HandleCurseForgeDownloadUrls(x?.ToString() ?? ""))
                .Where(x => !string.IsNullOrEmpty(x))
                .ToList();
            urls = [.. urls.SelectMany(GameDownload.DlSourceModDownloadGet)];
            var filePath = file["path"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(filePath))
            {
                Common.Log("[ModPack] 跳过一条没有路径信息的 Modrinth 文件记录");
                continue;
            }

            var targetPath =
                $@"{GameFolders.mcFolderSelected}versions\{instanceName}\{filePath}";
            if (!Path
                    .GetFullPath(targetPath)
                    .StartsWithF($@"{GameFolders.mcFolderSelected}versions\{instanceName}\", true))
            {
                DialogService.Instance.Show(new TextDialogRequest
                {
                    Title = Lang.Text("Minecraft.Download.Modpack.PathOutsideInstance.Title"),
                    Content = Lang.Text("Minecraft.Download.Modpack.PathOutsideInstance.Message", targetPath),
                    Buttons = DialogButtons.Ok,
                    IsWarn = true,
                    ForceWait = true
                });
                throw new Common.CancelledException();
            }

            fileList.Add(
                new DownloadFile(
                    Comp.CompFile.HandleModrinthDownloadUrls(
                        urls,
                        Comp.DownloadReason.ModPack,
                        minecraftVersion,
                        modLoader
                    ),
                    targetPath,
                    new Common.FileChecker(
                        actualSize: _ReadFileSize(file["fileSize"]),
                        hash: file["hashes"]?["sha1"]?.ToString() ?? ""
                    ),
                    true));
        }

        return fileList;
    }

    /// <summary>
    ///     fileSize 理论上是数字，个别整合包写成字符串，这里两种都接受；拿不到就按 0（跳过大小校验）。
    /// </summary>
    private static long _ReadFileSize(JsonNode? node)
    {
        if (node is null)
            return 0L;
        try
        {
            return node.ToObject<long>();
        }
        catch (Exception ex)
        {
            Common.Log(ex, $"[ModPack] 无法解析 Modrinth 文件大小：{node.ToJsonString()}");
            return 0L;
        }
    }

    private static bool _ShouldIncludeModrinthFile(JsonNode file)
    {
        // env.client 可能整体缺失（只写了 server），此时按「客户端要装」处理
        var client = file["env"]?["client"]?.ToString();
        if (client is null)
            return true;

        return client switch
        {
            "optional" => DialogService.Instance.Show(new TextDialogRequest
                {
                    Title = Lang.Text("Minecraft.Download.Modpack.OptionalFile.Title"),
                    Content = Lang.Text(
                        "Minecraft.Download.Modpack.OptionalFile.Message",
                        Common.GetFileNameFromPath(file["path"]?.ToString() ?? "")
                    ),
                    Buttons = DialogButtons.From(
                        DialogButton.Create(Lang.Text("Minecraft.Download.Modpack.OptionalFile.Download")),
                        DialogButton.Create(Lang.Text("Minecraft.Download.Modpack.OptionalFile.Skip"))),
                    ForceWait = true
                }).Result
                ?.Button != 2,
            "unsupported" => false,
            _ => true
        };
    }

    private static List<LoaderBase> _CreateModrinthLoaderList(
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
                    "Modrinth",
                    json["versionId"]?.ToString()))
            {
                ProgressWeight = 0.1d,
                show = false
            });
        return loaders;
    }

    private sealed record ModrinthLoaderVersions(
        string? MinecraftVersion,
        string? ForgeVersion,
        string? NeoForgeVersion,
        string? FabricVersion,
        Comp.CompLoaderType ModLoader);
}