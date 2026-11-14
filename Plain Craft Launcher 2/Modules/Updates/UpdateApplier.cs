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
        if (!TryParse(args, out var parentPid, out var target, out var source, out var restart))
            return false;

        // 更新器自身在 PCL\Update\ 下运行，日志要落到安装目录的 PCL\Log，别跟着跑偏
        _logDirectory = Path.Combine(Path.GetDirectoryName(target) ?? AppContext.BaseDirectory, "PCL", "Log");

        var exitCode = 0;
        try
        {
            Log($"[Update] 更新器启动：pid={parentPid}, target={target}, source={source}, restart={restart}");
            if (!File.Exists(source))
                throw new FileNotFoundException("找不到已下载的新版本文件", source);

            WaitForExit(parentPid, TimeSpan.FromSeconds(120));
            Replace(target, source);
            // 只覆盖调用方传入的路径（启动器传的是 Basics.ExecutablePath，即当前正在运行的那个 exe），
            // 文件名与所在目录一律不改：用户自定义的程序名、桌面/开始菜单快捷方式继续有效。
            Log($"[Update] 替换完成，按原文件名保留：{Path.GetFileName(target)}，启动新版本");
            if (restart) StartProgram(target);
        }

        catch (Exception ex)
        {
            exitCode = 1;
            Log("[Update] 更新失败：" + ex);
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
    ///         <item><c>--apply-update &lt;pid&gt; &lt;target&gt; &lt;source&gt; &lt;1|0&gt;</c>：本版本自己发起的更新</item>
    ///         <item><c>update &lt;pid&gt; &lt;target&gt; &lt;source&gt; &lt;true|false&gt;</c>：旧版本沿用的上游格式。
    ///         必须兼容，否则用户从旧版升上来的第一次强制更新会「下载完却装不上」——旧版正是这样调用新 exe 的。</item>
    ///     </list>
    /// </summary>
    private static bool TryParse(string[] args, out int parentPid, out string target, out string source, out bool restart)
    {
        parentPid = 0;
        target = "";
        source = "";
        restart = true;

        var index = Array.IndexOf(args, Verb);
        if (index >= 0)
        {
            if (args.Length < index + 4) return false;
            int.TryParse(args[index + 1], out parentPid);
            target = args[index + 2];
            source = args[index + 3];
            restart = args.Length <= index + 4 || args[index + 4] is "1" or "true";
            return true;
        }

        // 旧格式：update <pid> <target> <source> <restart>
        if (args.Length >= 5 && string.Equals(args[0], "update", StringComparison.OrdinalIgnoreCase))
        {
            int.TryParse(args[1], out parentPid);
            target = args[2];
            source = args[3];
            restart = !string.Equals(args[4], "false", StringComparison.OrdinalIgnoreCase);
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
    ///     覆盖目标 exe。Windows 上正在运行的 exe 不能直接写，
    ///     所以先把它改名成 .old 再复制；杀软或索引服务短暂占用时重试。
    /// </summary>
    private static void Replace(string target, string source)
    {
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var backup = target + ".old";
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                TryDelete(backup);
                if (File.Exists(target)) File.Move(target, backup);
                File.Copy(source, target, true);
                // 覆盖已经成功，收尾属于「能做就做」：旧 exe 若仍被占用（例如更新器替换的是自己）
                // 删不掉也不能算失败，下次更新开头会再清一次。
                TryDelete(backup);
                TryDelete(source);
                return;
            }

            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 20) throw new IOException($"替换 {target} 失败（重试 20 次）：{ex.Message}", ex);
                Thread.Sleep(500);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }

        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件仍被占用：留着即可，不阻断更新
        }
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
