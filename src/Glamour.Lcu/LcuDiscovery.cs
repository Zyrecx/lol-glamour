using System.Management;
using System.Text.RegularExpressions;

namespace Glamour.Lcu;

/// <summary>Finds the running League client by reading the port and token from its command line.</summary>
public static partial class LcuDiscovery
{
    public static LcuCredentials? Find()
    {
        // The process list first: a WMI query costs ~50 ms of the WMI service's CPU (measured: ~2% of a core at one per 2 s, all game long), the list a few ms.
        // No list at all: the WMI query below is the only look left.
        if (ProcessNames.TryGet() is { } running && !running.Contains("LeagueClientUx")) return null;
        // Called every couple of seconds while no client runs, so the WMI (COM) objects are released at once, not left to the finalizer.
        using var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name = 'LeagueClientUx.exe'");
        using var processes = searcher.Get();
        foreach (var process in processes)
        {
            using (process)
                if (Parse(process["CommandLine"] as string) is { } credentials) return credentials;
        }
        return null;
    }

    public static LcuCredentials? Parse(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return null;
        var port = PortPattern().Match(commandLine);
        var token = TokenPattern().Match(commandLine);
        return port.Success && token.Success ? new LcuCredentials(int.Parse(port.Groups[1].Value), token.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"--app-port=(\d+)")]
    private static partial Regex PortPattern();

    [GeneratedRegex(@"--remoting-auth-token=([\w-]+)")]
    private static partial Regex TokenPattern();
}
