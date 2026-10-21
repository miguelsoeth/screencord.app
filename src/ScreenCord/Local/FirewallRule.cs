using System.ComponentModel;
using System.Diagnostics;

namespace ScreenCord.Local;

/// <summary>Regra do Firewall do Windows que deixa os outros computadores alcançarem as salas locais.</summary>
public interface IFirewallRule
{
    /// <summary>A regra existe (consultar não exige administrador).</summary>
    Task<bool> IsConfiguredAsync();

    /// <summary>Cria a regra pedindo a permissão do Windows (UAC). Retorna se ela passou a existir.</summary>
    Task<bool> ConfigureAsync();
}

/// <summary>
/// Cria duas regras de entrada para o exe do ScreenCord, em todos os perfis de rede (Hamachi e Radmin costumam
/// aparecer como rede "Pública"), mas só para endereços de rede local e das VPNs de jogo (<see cref="LocalNetwork.Ranges"/>):
/// TCP <see cref="LocalNetwork.RoomPort"/> (salas e mídia) e UDP <see cref="LocalNetwork.DiscoveryPort"/> (anúncios).
/// Quem cria é o próprio exe com <see cref="CommandLineFlag"/>, elevado; o instalador faz o mesmo na primeira instalação.
/// </summary>
public sealed class FirewallRule : IFirewallRule
{
    public const string CommandLineFlag = "--configure-firewall";
    public const string RemoveFlag = "--remove-firewall";
    /// <summary>Vai junto com <see cref="RemoveFlag"/> no processo já elevado, para ele não pedir a permissão de novo.</summary>
    public const string ElevatedFlag = "--elevated";
    public const string RoomRuleName = "ScreenCord - salas locais";
    public const string DiscoveryRuleName = "ScreenCord - descoberta de salas";

    public async Task<bool> IsConfiguredAsync() =>
        await RunNetshAsync(["advfirewall", "firewall", "show", "rule", $"name={RoomRuleName}"]) == 0 &&
        await RunNetshAsync(["advfirewall", "firewall", "show", "rule", $"name={DiscoveryRuleName}"]) == 0;

    public async Task<bool> ConfigureAsync()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, CommandLineFlag) { UseShellExecute = true, Verb = "runas" });
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (Win32Exception)
        {
            // A pessoa recusou o pedido de permissão do Windows.
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }

        return await IsConfiguredAsync();
    }

    /// <summary>
    /// Cria (ou recria) as regras para <paramref name="exePath"/>. Roda no processo elevado iniciado por
    /// <see cref="ConfigureAsync"/> ou pelo instalador; retorna o código de saída.
    /// </summary>
    public static int Apply(string exePath)
    {
        var remote = string.Join(',', LocalNetwork.Ranges);
        var failed = false;
        foreach (var (name, protocol, port) in new[] { (RoomRuleName, "TCP", LocalNetwork.RoomPort), (DiscoveryRuleName, "UDP", LocalNetwork.DiscoveryPort) })
        {
            RunNetsh(["advfirewall", "firewall", "delete", "rule", $"name={name}"]);
            failed |= RunNetsh(
            [
                "advfirewall", "firewall", "add", "rule", $"name={name}", "dir=in", "action=allow", $"program={exePath}",
                $"protocol={protocol}", $"localport={port}", $"remoteip={remote}", "profile=any", "enable=yes",
            ]) != 0;
        }

        return failed ? 1 : 0;
    }

    /// <summary>
    /// Apaga as regras; o instalador chama o exe com <see cref="RemoveFlag"/> ao desinstalar, sem elevação. Sem
    /// regra, sai sem perguntar nada; com regra, o exe se reinicia elevado (pedindo a permissão do Windows) para
    /// apagá-la. Se a pessoa recusar, a regra fica (o README explica como apagá-la à mão). Retorna o código de saída.
    /// </summary>
    public static int Remove(string exePath, bool elevated)
    {
        string[] names = [RoomRuleName, DiscoveryRuleName];
        var existing = names.Where(name => RunNetsh(["advfirewall", "firewall", "show", "rule", $"name={name}"]) == 0).ToList();
        if (existing.Count == 0)
        {
            return 0;
        }

        if (!elevated)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(exePath, $"{RemoveFlag} {ElevatedFlag}") { UseShellExecute = true, Verb = "runas" });
                if (process is null || !process.WaitForExit(60_000))
                {
                    return 1;
                }

                return process.ExitCode;
            }
            catch (Win32Exception)
            {
                // A pessoa recusou o pedido de permissão do Windows.
                return 1;
            }
        }

        var failed = false;
        foreach (var name in existing)
        {
            failed |= RunNetsh(["advfirewall", "firewall", "delete", "rule", $"name={name}"]) != 0;
        }

        return failed ? 1 : 0;
    }

    private static int RunNetsh(IEnumerable<string> arguments)
    {
        try
        {
            using var process = Process.Start(Netsh(arguments));
            if (process is null)
            {
                return -1;
            }

            process.WaitForExit(15_000);
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (Win32Exception)
        {
            return -1;
        }
    }

    private static async Task<int> RunNetshAsync(IEnumerable<string> arguments)
    {
        try
        {
            using var process = Process.Start(Netsh(arguments));
            if (process is null)
            {
                return -1;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is Win32Exception or OperationCanceledException)
        {
            return -1;
        }
    }

    private static ProcessStartInfo Netsh(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo("netsh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }
}
