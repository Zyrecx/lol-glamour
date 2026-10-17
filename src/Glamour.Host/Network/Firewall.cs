using System.ComponentModel;
using System.Diagnostics;

namespace Glamour.Host.Network;

/// <param name="Supported">False off Windows or when the firewall cannot be read.</param>
/// <param name="Allowed">An enabled inbound allow rule covers this port on the network profile in use right now.</param>
/// <param name="RulePresent">Glamour's own rule exists (it may still not apply, see <paramref name="NetworkIsPublic"/>).</param>
/// <param name="NetworkIsPublic">Windows treats the current network as Public, where Glamour's rule is not active.</param>
public sealed record FirewallStatus(bool Supported, bool Allowed, bool RulePresent, bool NetworkIsPublic);

public interface IFirewall
{
    FirewallStatus Check(int port);

    /// <summary>Adds (or replaces) Glamour's inbound rule for the ports. Windows shows its admin prompt; false when the user declines or it fails.</summary>
    Task<bool> AddRuleAsync(IReadOnlyCollection<int> ports, CancellationToken ct);
}

/// <summary>Reads the Windows Firewall through COM (works in every display language) and adds the rule with an elevated netsh.</summary>
public sealed class WindowsFirewall : IFirewall
{
    public const string RuleName = "Glamour LAN access";
    private const int Inbound = 1, Allow = 1, Domain = 1, Private = 2, Public = 4;
    private const int ErrorCancelled = 1223; // the user said no at the admin prompt

    public FirewallStatus Check(int port)
    {
        if (!OperatingSystem.IsWindows()) return new FirewallStatus(false, false, false, false);
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            int current = policy.CurrentProfileTypes; // can be several at once
            var program = Environment.ProcessPath;
            bool allowed = false, present = false;
            foreach (dynamic rule in policy.Rules)
            {
                if ((int)rule.Direction != Inbound || (int)rule.Action != Allow || !(bool)rule.Enabled) continue;
                bool ours = string.Equals((string)rule.Name, RuleName, StringComparison.Ordinal);
                bool coversPort = PortsCover((string?)rule.LocalPorts, port);
                // Either our rule, or the one Windows adds when the user clicks "Allow" on its own prompt for this exe.
                bool programRule = program is not null && string.Equals((string?)rule.ApplicationName, program, StringComparison.OrdinalIgnoreCase)
                    && ((int)rule.Protocol is 6 or 256) && coversPort;
                if (ours && coversPort) present = true;
                if ((ours && coversPort || programRule) && ((int)rule.Profiles & current) != 0) allowed = true;
            }
            return new FirewallStatus(true, allowed, present, (current & Public) != 0 && (current & (Private | Domain)) == 0);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or NullReferenceException)
        {
            return new FirewallStatus(false, false, false, false);
        }
    }

    public async Task<bool> AddRuleAsync(IReadOnlyCollection<int> ports, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return false;
        // LocalSubnet plus Tailscale's range, Private and Domain networks only: a public hotspot stays closed.
        var add = $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={string.Join(',', ports)} remoteip=LocalSubnet,100.64.0.0/10 profile=private,domain";
        var del = $"netsh advfirewall firewall delete rule name=\"{RuleName}\"";
        var info = new ProcessStartInfo("cmd.exe", $"/c \"{del} & {add}\"") { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        try
        {
            using var p = Process.Start(info);
            if (p is null) return false;
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled) { return false; }
    }

    /// <summary>Does a netsh/COM LocalPorts value ("5170", "80,5170", "5000-6000", "*") include the port?</summary>
    public static bool PortsCover(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts) || localPorts == "*") return true;
        foreach (var part in localPorts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var bounds = part.Split('-', 2);
            if (int.TryParse(bounds[0], out var lo) && int.TryParse(bounds[^1], out var hi) && port >= lo && port <= hi) return true;
        }
        return false;
    }
}
