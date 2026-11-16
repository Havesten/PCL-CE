using System.Diagnostics;
using System.IO;
using System.Text;
using PCL.Core.App;

namespace PCL;

/// <summary>
///     自更新落地：等旧进程退出后，把下载好的新 exe 覆盖到安装路径。
///     <para>
///         由启动器在退出前启动自身的一个副本（<c>PCL\Update\Updater.exe</c>）来执行，
///         参数为 <c>--apply-update &lt;pid&gt; &lt;target&gt; &lt;source&gt; &lt;restart&gt;</c>。
///         用「自身副本 + 参数」而不是批处理或第三方组件，是为了不依赖外部程序，
///         也不至于被安全软件当成脚本行为拦截。
///     </para>
///     <para>
///         必须在 <see cref="Program.Main" /> 的最前面拦截：此时 WPF 尚未初始化，
///         进程体积小、没有文件占用，替换完成即可直接退出。
///     </para>
/// </summary>
internal static class UpdateApplier
{
    private const string Verb = "--apply-update";

    /// <summary>日志目录，指向「安装目录\PCL\Log」，与启动器自身日志放在一起便于排查。</summary>
    private static string? _logDirectory;

    /// <summary>
    ///     命令行带更新参数时执行替换并结束进程。
    ///     返回 true 表示本进程只是更新器，不应继续启动界面。
    /// </summary>
    public static bool TryApply()
    {
        var args = Basics.CommandLineArguments;
        if (!TryParse(args, out var parentPid, out var target, out var source, out var restart, out var version))
            return false;

        // 更新器自身在 PCL\Update\ 下运行，日志要落到安装目录的 PCL\Log，别跟着跑偏
        var installDir = Path.GetDirectoryName(target) ?? AppContext.BaseDirectory;
        _logDirectory = Path.Combine(installDir, "PCL", "Log");

        // 同一时刻只允许一个更新器动手：连点「重启并安装」或旧更新器残留时，
        // 两个进程同时改名/覆盖会互相踩，最终谁都装不上。
        using var single = new Mutex(false, @"Local\PCLCE-NS-UpdateApplier");
        var acquired = false;
        try
        {
            acquired = single.WaitOne(TimeSpan.FromSeconds(90));
        }

        catch (AbandonedMutexException)
        {
            acquired = true; // 上一个更新器异常退出，互斥体已归本进程
        }

        if (!acquired)
        {
            // 另一个更新器还在动手：不跟它抢文件，但也不能让用户双击没反应。
            // 等它把互斥体放开（说明替换已经收尾），如果此时启动器没在跑就顺手拉起来。
            Log("[Update] 另一个更新器正在执行，等待其结束");
            try
            {
                single.WaitOne(TimeSpan.FromSeconds(60));
            }

            catch (AbandonedMutexException)
            {
                // 对方异常退出，继续判断目标状态即可
            }

            if (restart && !IsTargetRunning(target))
            {
                try
                {
                    StartProgram(target);
                }

                catch (Exception ex)
                {
                    Log("[Update] 启动程序失败：" + ex.Message);
                }
            }

            return true;
        }

        var exitCode = 0;
        try
        {
            Log($"[Update] 更新器启动：pid={parentPid}, target={target}, source={source}, " +
                $"restart={restart}, version={version}");
            if (!File.Exists(source))
                throw new FileNotFoundException("找不到已下载的新版本文件", source);

            WaitForExit(parentPid, TimeSpan.FromSeconds(180));
            Replace(target, source);
            // 只覆盖调用方传入的路径（启动器传的是 Basics.ExecutablePath，即当前正在运行的那个 exe），
            // 文件名与所在目录一律不改：用户自定义的程序名、桌面/开始菜单快捷方式继续有效。
            UpdateStateStore.Clear(installDir);
            Log($"[Update] 替换完成，按原文件名保留：{Path.GetFileName(target)}，启动新版本");
            if (restart) StartProgram(target);
        }

        catch (Exception ex)
        {
            exitCode = 1;
            Log("[Update] 更新失败：" + ex);
            // 记下这次失败：同一个版本连续失败多次后，主程序不再自动重试，
            // 改为提示用户手动替换，避免「更新 → 失败 → 再更新」的循环。
            if (!string.IsNullOrWhiteSpace(version))
                UpdateStateStore.RecordFailure(installDir, version);
            // 替换没成功也要把用户拉回启动器，否则双击图标毫无反应更糟
            try
            {
                StartProgram(target);
            }

            catch (Exception inner)
            {
                Log("[Update] 回退启动也失败：" + inner.Message);
            }
        }

        Environment.Exit(exitCode);
        return true;
    }

    /// <summary>
    ///     两种参数格式都认：
    ///     <list type="bullet">
    ///         <item><c>--apply-update &lt;pid&gt; &lt;target&gt; &lt;source&gt; &lt;1|0&gt; [version]</c>：本版本自己发起的更新</item>
    ///         <item><c>update &lt;pid&gt; &lt;target&gt; &lt;source&gt; &lt;true|false&gt;</c>：旧版本沿用的上游格式。
    ///         必须兼容，否则用户从旧版升上来的第一次强制更新会「下载完却装不上」——旧版正是这样调用新 exe 的。</item>
    ///     </list>
    ///     <paramref name="version" /> 只用于失败记账，缺省不影响替换。
    /// </summary>
    private static bool TryParse(string[] args, out int parentPid, out string target, out string source,
        out bool restart, out string version)
    {
        parentPid = 0;
        target = "";
        source = "";
        restart = true;
        version = "";

        var index = Array.IndexOf(args, Verb);
        if (index >= 0)
        {
            if (args.Length < index + 4) return false;
            int.TryParse(args[index + 1], out parentPid);
            target = args[index + 2];
            source = args[index + 3];
            restart = args.Length <= index + 4 || args[index + 4] is "1" or "true";
            if (args.Length > index + 5)
                version = args[index + 5];
            return true;
        }

        // 旧格式：update <pid> <target> <source> <restart>
        if (args.Length >= 5 && string.Equals(args[0], "update", StringComparison.OrdinalIgnoreCase))
        {
            int.TryParse(args[1], out parentPid);
            target = args[2];
            source = args[3];
            restart = !string.Equals(args[4], "false", StringComparison.OrdinalIgnoreCase);
            if (args.Length > 5)
                version = args[5];
            return true;
        }

        return false;
    }

    private static void WaitForExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                Log("[Update] 等待旧进程退出超时，仍尝试替换");
        }

        catch (ArgumentException)
        {
            // 进程已经不在了，正常情况
        }

        catch (Exception ex)
        {
            Log("[Update] 等待旧进程时出错（忽略并继续）：" + ex.Message);
        }
    }

    /// <summary>
    ///     覆盖目标 exe。分三步走，任何一步失败都不会让用户没有启动器：
    ///     <list type="number">
    ///         <item>把新版本复制到同目录的 <c>.new</c> 暂存并校验大小；</item>
    ///         <item>把旧的 exe 改名成 <c>.old</c>（名字被占用就换一个，不死等）；</item>
    ///         <item>把 <c>.new</c> 移正，失败则把 <c>.old</c> 搬回去。</item>
    ///     </list>
    ///     旧实现直接「改名后立刻 copy」，一旦 copy 因杀软或磁盘问题失败，
    ///     目标文件就没了；而 <c>.old</c> 删不掉时改名会一直失败，
    ///     重试 20 次后放弃 —— 这就是部分设备反复更新的原因。
    /// </summary>
    private static void Replace(string target, string source)
    {
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var sourceInfo = new FileInfo(source);
        if (!sourceInfo.Exists || sourceInfo.Length == 0)
            throw new IOException("新版本文件缺失或为空：" + source);

        // ① 同目录暂存：既能在落地前校验完整性，也保证后面的 Move 是同卷操作
        var staging = target + ".new";
        CopyWithRetry(source, staging);
        var stagingSize = new FileInfo(staging).Length;
        if (stagingSize != sourceInfo.Length)
            throw new IOException($"新版本暂存不完整：{stagingSize} != {sourceInfo.Length}");

        // ② 挪走旧文件
        var backup = target + ".old";
        var hasBackup = false;
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            try
            {
                if (!File.Exists(target))
                    break; // 目标已不存在（上次更新中途失败过），直接落地即可

                if (File.Exists(backup) && !TryDelete(backup))
                {
                    // .old 被杀软/索引占用：换个唯一名字绕开，不要在同一路径上重试到超时
                    backup = target + $".old-{DateTime.Now:HHmmssfff}";
                    Log("[Update] 旧的备份文件删不掉，改用 " + Path.GetFileName(backup));
                }

                File.Move(target, backup);
                hasBackup = true;
                break;
            }

            catch (UnauthorizedAccessException ex)
            {
                // 目录本身不可写：重试没有意义，交给上层提示「用管理员运行或手动替换」
                TryDelete(staging);
                throw new IOException($"没有权限写入启动器所在目录：{directory}", ex);
            }

            catch (IOException ex)
            {
                if (attempt == 30)
                {
                    TryDelete(staging);
                    throw new IOException($"无法移走旧版本文件（已重试 30 次）：{ex.Message}", ex);
                }

                Thread.Sleep(RetryDelay(attempt));
            }
        }

        // ③ 落地
        var placed = false;
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            try
            {
                File.Move(staging, target);
                placed = true;
                break;
            }

            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 30) break;
                Thread.Sleep(RetryDelay(attempt));
            }
        }

        if (!placed)
        {
            // 落地失败：把旧文件搬回去，用户至少还能启动旧版本
            if (hasBackup && !File.Exists(target) && File.Exists(backup))
                try
                {
                    File.Move(backup, target);
                    Log("[Update] 落地失败，已回滚到旧版本");
                }

                catch (Exception rollback)
                {
                    Log("[Update] 回滚失败，旧版本残留在 " + backup + "：" + rollback.Message);
                }

            TryDelete(staging);
            throw new IOException("新版本落位失败：" + target);
        }

        // 校验最终文件大小，防止「看起来成功了其实是半截文件」
        var finalSize = new FileInfo(target).Length;
        if (finalSize != sourceInfo.Length)
            throw new IOException($"替换后文件大小异常：{finalSize} != {sourceInfo.Length}");

        // 收尾属于「能做就做」：旧 exe 或下载包若仍被占用，删不掉也不算失败
        TryDelete(backup);
        TryDelete(source);
    }

    /// <summary>重试间隔递增，总等待约 20 秒，给杀软扫描留时间但不会让人以为卡死。</summary>
    private static int RetryDelay(int attempt) => attempt <= 5 ? 200 : attempt <= 15 ? 500 : 1000;

    private static void CopyWithRetry(string source, string destination)
    {
        for (var attempt = 1; attempt <= 10; attempt++)
            try
            {
                File.Copy(source, destination, true);
                return;
            }

            catch (UnauthorizedAccessException ex)
            {
                throw new IOException("无法写入暂存文件：" + destination, ex);
            }

            catch (IOException ex)
            {
                if (attempt == 10)
                    throw new IOException($"复制新版本失败（已重试 10 次）：{ex.Message}", ex);
                Thread.Sleep(RetryDelay(attempt));
            }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            File.Delete(path);
            return true;
        }

        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件仍被占用：留着即可，不阻断更新
            return false;
        }
    }

    /// <summary>目标 exe 是否正在运行（避免同一份程序被拉起两个实例）。</summary>
    private static bool IsTargetRunning(string target)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(target);
            foreach (var process in Process.GetProcessesByName(name))
                try
                {
                    if (string.Equals(process.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                catch
                {
                    // 权限不足读不到模块信息，当作没在运行
                }
                finally
                {
                    process.Dispose();
                }
        }

        catch
        {
            // 查询进程失败不影响更新，按「没在运行」处理
        }

        return false;
    }

    private static void StartProgram(string path)
    {
        if (!File.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory
        });
    }

    /// <summary>
    ///     更新器没有界面，日志直接写文件；此时 <see cref="Common.Log" /> 的初始化尚未完成，不能用它。
    /// </summary>
    private static void Log(string message)
    {
        try
        {
            var dir = _logDirectory ?? Path.Combine(AppContext.BaseDirectory, "PCL", "Log");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "update.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}", Encoding.UTF8);
        }

        catch
        {
            // 日志写不进去不影响主流程
        }
    }
}
