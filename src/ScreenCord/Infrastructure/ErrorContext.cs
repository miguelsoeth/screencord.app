using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ScreenCord.Localization;
using Vortice.DXGI;

namespace ScreenCord.Infrastructure;

/// <summary>
/// Estado do aplicativo que acompanha cada erro enviado ao painel (<see cref="ErrorReporter"/>): o que estava
/// acontecendo quando ele ocorreu, como o compartilhamento ativo e o encoder em uso. Nada de tokens, nomes de
/// janelas ou dados de conta.
/// </summary>
public static class ErrorContext
{
    private static readonly ConcurrentDictionary<string, string> Values = new(StringComparer.Ordinal);
    private static readonly long StartedAt = Stopwatch.GetTimestamp();
    private static int _watching;

    public static void Set(string key, object? value)
    {
        if (value is null)
        {
            Values.TryRemove(key, out _);
        }
        else
        {
            Values[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
    }

    /// <summary>Remove todas as chaves que começam com <paramref name="prefix"/> (ex.: <c>share.</c>).</summary>
    public static void Clear(string prefix)
    {
        foreach (var key in Values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            Values.TryRemove(key, out _);
        }
    }

    /// <summary>Transmissões sendo assistidas agora (cada uma tem seu decoder).</summary>
    public static void ViewerOpened() => Set("watching", Interlocked.Increment(ref _watching));

    public static void ViewerClosed() => Set("watching", Math.Max(0, Interlocked.Decrement(ref _watching)));

    public static IReadOnlyDictionary<string, string> Snapshot()
    {
        var snapshot = new SortedDictionary<string, string>(Values, StringComparer.Ordinal)
        {
            ["uptime"] = Stopwatch.GetElapsedTime(StartedAt).ToString(@"d\.hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture),
            ["language"] = Loc.Language.ToString(),
        };

        try
        {
            using var process = Process.GetCurrentProcess();
            snapshot["memory.workingSetMb"] = (process.WorkingSet64 / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            snapshot["threads"] = process.Threads.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
        }

        return snapshot;
    }
}

/// <summary>Dados do computador para o painel (<see cref="ScreenCord.Contracts.ClientErrorReport.Device"/>), lidos uma vez por execução.</summary>
public static class DeviceInfo
{
    public static IReadOnlyDictionary<string, string> Collect()
    {
        var info = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Try(() =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                // O ProductName continua "Windows 10" no Windows 11; o build diz qual é.
                var build = key.GetValue("CurrentBuildNumber") as string;
                var product = key.GetValue("ProductName") as string ?? "Windows";
                if (int.TryParse(build, out var number) && number >= 22000)
                {
                    product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
                }

                info["os.edition"] = $"{product} {key.GetValue("DisplayVersion")}".Trim();
                info["os.build"] = $"{build}.{key.GetValue("UBR")}";
            }
        });
        Try(() =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            info["cpu"] = $"{(key?.GetValue("ProcessorNameString") as string)?.Trim()} ({Environment.ProcessorCount} threads)";
        });
        Try(() => info["memoryMb"] = (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Try(() => info["culture"] = System.Globalization.CultureInfo.CurrentUICulture.Name);
        Try(() => info["dotnet"] = RuntimeInformation.FrameworkDescription);
        Try(() => info["session"] = SystemInformationIsRemote() ? "remota" : "local");
        Try(() => AddAdapters(info));
        return info;
    }

    /// <summary>Placas de vídeo (nome, driver, memória) e as telas ligadas a cada uma.</summary>
    private static void AddAdapters(SortedDictionary<string, string> info)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; a < 4 && factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                var description = adapter.Description1;
                if ((description.Flags & AdapterFlags.Software) != 0)
                {
                    continue;
                }

                var driver = adapter.CheckInterfaceSupport<IDXGIDevice>(out var umd) ? FormatDriver(umd) : "?";
                info[$"gpu{a}"] = $"{description.Description} (driver {driver}, {description.DedicatedVideoMemory / (1024 * 1024)} MB)";

                var screens = new List<string>();
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var bounds = output.Description.DesktopCoordinates;
                        screens.Add($"{bounds.Right - bounds.Left}x{bounds.Bottom - bounds.Top}");
                    }
                }

                if (screens.Count > 0)
                {
                    info[$"gpu{a}.screens"] = string.Join(", ", screens);
                }
            }
        }
    }

    // A versão do driver vem em quatro partes de 16 bits (ex.: 31.0.15.5222).
    private static string FormatDriver(long version) =>
        $"{(version >> 48) & 0xFFFF}.{(version >> 32) & 0xFFFF}.{(version >> 16) & 0xFFFF}.{version & 0xFFFF}";

    private static bool SystemInformationIsRemote() => GetSystemMetrics(0x1000) != 0; // SM_REMOTESESSION

    private static void Try(Action read)
    {
        try
        {
            read();
        }
        catch (Exception)
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
