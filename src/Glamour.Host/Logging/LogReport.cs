using System.IO.Compression;
using System.Reflection;
using Glamour.Host.Auth;

namespace Glamour.Host.Logging;

/// <summary>Opens a file or link on this PC for the user (Explorer, the default browser). The real one is registered only with a client (not in tests or the demo host).</summary>
public interface IShellOpener
{
    void ShowFile(string path);
    void OpenLink(string url);
}

public sealed class WindowsShellOpener : IShellOpener
{
    public void ShowFile(string path) => Start("explorer.exe", $"/select,\"{path}\"");
    public void OpenLink(string url) => Start(url, null);

    private static void Start(string file, string? args)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args ?? "") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* nothing to open with */ }
    }
}

/// <summary>
/// Settings "Send a log" (SPEC "Publishing"): the log files, scrubbed, in a zip next to them, then a prefilled GitHub issue to attach it to.
/// Logs leave the PC only this way, by hand.
/// </summary>
public static class LogReport
{
    /// <summary>owner/repo on GitHub: the Problem form and the release updates come from it.</summary>
    public static readonly string? GitHubRepo = "Zyrecx/lol-glamour";

    private const int Keep = 5;

    public static string Version => typeof(LogReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    /// <summary>Writes `reports\glamour-log-<time>.zip` under the log folder (the older log first, so numbering follows time) and keeps the last few.</summary>
    public static string Build(string logDir, Scrubber scrubber, DateTime now)
    {
        var dir = Path.Combine(logDir, "reports");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"glamour-log-{now:yyyyMMdd-HHmmss}.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var about = new StreamWriter(zip.CreateEntry("about.txt").Open()))
            {
                about.WriteLine($"Glamour {Version}");
                about.WriteLine($"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
                about.WriteLine($"Made {now:yyyy-MM-dd HH:mm}");
            }
            var logs = new[] { "glamour.1.log", "glamour.log" }.Select(n => Path.Combine(logDir, n)).Where(File.Exists).ToList();
            foreach (var log in logs) // first who is who, so a name goes even where it shows before its player's event
                foreach (var line in Lines(log)) scrubber.Learn(line);
            foreach (var log in logs)
            {
                using var writer = new StreamWriter(zip.CreateEntry(Path.GetFileName(log)).Open());
                foreach (var line in Lines(log)) writer.WriteLine(scrubber.Line(line));
            }
        }
        foreach (var old in new DirectoryInfo(dir).GetFiles("glamour-log-*.zip").OrderByDescending(f => f.Name).Skip(Keep))
            try { old.Delete(); } catch (IOException) { /* in use; next time */ }
        return path;
    }

    private static IEnumerable<string> Lines(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)); // the app is still writing it
        while (reader.ReadLine() is { } line) yield return line;
    }

    /// <summary>The repo's Problem form (`.github/ISSUE_TEMPLATE/bug.yml`, fields by id) with the version and the zip to attach filled in; null without a repo.</summary>
    public static string? IssueLink(string zipName) => GitHubRepo is null ? null : IssueLink(GitHubRepo, Version, zipName);

    public static string IssueLink(string repo, string version, string zipName) =>
        $"https://github.com/{repo}/issues/new?template=bug.yml&version={Uri.EscapeDataString(version)}" +
        $"&log={Uri.EscapeDataString($"Drag {zipName} here (in the folder that opened).")}";

    public static void MapLogReport(this RouteGroupBuilder api, string logDir)
    {
        // The logs are this PC's: only the PC itself may make the zip, never a phone.
        api.MapPost("/logs/report", (HttpContext ctx, IServiceProvider sp, TimeProvider time) =>
        {
            if (!AuthEndpoints.IsLoopback(ctx)) return Results.StatusCode(403);
            string path;
            try { path = Build(logDir, Scrubber.ForThisPc(), time.GetLocalNow().DateTime); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Json(new { error = "Could not write the log zip." }, statusCode: 500); }
            var link = IssueLink(Path.GetFileName(path));
            if (sp.GetService<IShellOpener>() is { } shell)
            {
                shell.ShowFile(path);
                if (link is not null) shell.OpenLink(link);
            }
            return Results.Ok(new { file = Path.GetFileName(path), path, issue = link });
        });
    }
}
