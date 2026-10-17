using System.Diagnostics;
using System.Management;

namespace Glamour.Lcu;

/// <summary>Names of the running processes. On some PCs .NET's own process list throws inside its parser (ArgumentOutOfRangeException from
/// Process.GetProcesses, every call); WMI's list stands in there.</summary>
public static class ProcessNames
{
    /// <summary>The names without ".exe", or null when neither list could be read: callers must not take that as "nothing runs".</summary>
    /// <param name="fellBack">Called with the .NET error when WMI's list had to stand in.</param>
    public static HashSet<string>? TryGet(Action<Exception>? fellBack = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
                using (p) seen.Add(p.ProcessName);
            return seen;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { fellBack?.Invoke(ex); }
        try
        {
            seen.Clear();
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Process");
            using var processes = searcher.Get();
            foreach (var process in processes)
                using (process)
                    if (process["Name"] is string name) seen.Add(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);
            return seen;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Process.GetProcessesByName, with WMI's list standing in where .NET's throws. WMI errors propagate: the caller cannot go on blind.</summary>
    public static Process[] ByName(string name)
    {
        try { return Process.GetProcessesByName(name); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        var found = new List<Process>();
        using var searcher = new ManagementObjectSearcher($"SELECT ProcessId FROM Win32_Process WHERE Name = '{name}.exe'");
        using var processes = searcher.Get();
        foreach (var process in processes)
            using (process)
                try { found.Add(Process.GetProcessById(Convert.ToInt32(process["ProcessId"]))); }
                catch (ArgumentException) { } // already gone
        return [.. found];
    }
}
