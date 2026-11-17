using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Utils;

namespace PCL.Core.Link.Scaffolding.EasyTier;

public class CliNetTest
{
    public enum NatType
    {
        Unknown,
        OpenInternet,
        NoPat,
        FullCone,
        Restricted,
        PortRestricted,
        SymmetricEasy,
        Symmetric,
        SymmetricFirewall,
        UdpBlocked
    }
    public record NetStatus
    {
        public required NatType UdpNatType;
        public required NatType TcpNatType;
        public required bool SupportIPv6;
    }

    public static async Task<NetStatus?> GetNetStatusAsync(CancellationToken cancellationToken = default)
    {
        // EasyTier 2.6.4 的独立 stun 命令自身限时 25 秒；额外限时覆盖进程和管道异常。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var cliProcess = new Process();
        cliProcess.StartInfo = new ProcessStartInfo
        {
            FileName = $"{EasyTierMetadata.EasyTierFilePath}\\easytier-cli.exe",
            WorkingDirectory = EasyTierMetadata.EasyTierFilePath,
            Arguments = $"-o json stun",
            ErrorDialog = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8
        };
        try
        {
            cliProcess.Start();
            cliProcess.StandardInput.Close();
            // 同时读两个管道，避免 stderr 填满导致子进程和调用方互相等待。
            var stdout = cliProcess.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = cliProcess.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr, cliProcess.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            if (cliProcess.ExitCode != 0)
            {
                LogWrapper.Warn("Link", "EasyTier NAT 检测失败: " + await stderr.ConfigureAwait(false));
                return null;
            }

            var stunInfo = JsonSerializer.Deserialize<StunInfo>(await stdout.ConfigureAwait(false),
                JsonCompat.SerializerOptions);
            if (stunInfo is null) return null;
            return new NetStatus
            {
                UdpNatType = GetNatTypeViaCode(stunInfo.UdpNatType),
                TcpNatType = GetNatTypeViaCode(stunInfo.TcpNatType),
                SupportIPv6 = stunInfo.Ips.Any(IsPublicIPv6)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "Link", "Failed to do net test");
            return null;
        }
        finally
        {
            try
            {
                if (!cliProcess.HasExited) cliProcess.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { /* 进程未启动或已退出 */ }
            catch (System.ComponentModel.Win32Exception ex) { LogWrapper.Warn(ex, "Link", "无法结束 NAT 检测进程"); }
        }
    }

    public static bool IsPublicIPv6(string ip) => IPAddress.TryParse(ip, out var address) &&
        address.AddressFamily == AddressFamily.InterNetworkV6 &&
        (address.GetAddressBytes()[0] & 0xe0) == 0x20 &&
        !address.ToString().StartsWith("2001:db8:", StringComparison.OrdinalIgnoreCase);

    // 与 EasyTier v2.6.4 common.proto 对齐，0 是 Unknown，不能把检测失败显示成公网。
    public static NatType GetNatTypeViaCode(int type) => type switch
    {
        1 => NatType.OpenInternet,
        2 => NatType.NoPat,
        3 => NatType.FullCone,
        4 => NatType.Restricted,
        5 => NatType.PortRestricted,
        6 => NatType.Symmetric,
        7 => NatType.SymmetricFirewall,
        8 or 9 => NatType.SymmetricEasy,
        _ => NatType.Unknown
    };

    /// <summary>只评估本机条件，不把 NAT 类型当作两台机器实际连通的证明。</summary>
    public static string GetPunchingAdvice(NatType type) => type switch
    {
        NatType.OpenInternet or NatType.NoPat => "公网或无端口转换：可尝试直接连接。",
        NatType.FullCone => "NAT1（全锥形）：通常适合打洞；双方均无独立公网 IP 也可尝试。",
        NatType.Restricted => "NAT2（地址受限锥形）：可以尝试打洞，需要双方同时连接相同的会合节点。",
        NatType.PortRestricted => "NAT3（端口受限锥形）：可以尝试打洞，成功率取决于对方 NAT 与防火墙。",
        NatType.SymmetricEasy or NatType.Symmetric => "NAT4（对称型）：打洞较困难，可尝试端口预测；失败时使用中继联机。",
        NatType.SymmetricFirewall or NatType.UdpBlocked => "UDP 受到防火墙限制：UDP 打洞受限，可尝试 TCP 或使用中继联机。",
        _ => "尚不能判断打洞条件。检测失败不等于不支持，请检查网络后重试或使用中继联机。"
    };

    public static string GetNatTypeString(NatType type)
    {
        return Lang.Text(type switch
        {
            NatType.OpenInternet or NatType.NoPat => "Link.Nat.Type.Open",
            NatType.FullCone => "Link.Nat.Type.FullCone",
            NatType.PortRestricted => "Link.Nat.Type.PortRestricted",
            NatType.Restricted => "Link.Nat.Type.Restricted",
            NatType.SymmetricEasy => "Link.Nat.Type.SymmetricEasy",
            NatType.Symmetric => "Link.Nat.Type.Symmetric",
            NatType.SymmetricFirewall => "Link.Nat.Type.SymmetricFirewall",
            NatType.UdpBlocked => "Link.Nat.Type.UdpBlocked",
            _ => "Link.Nat.Type.Unknown"
        });
    }
}
