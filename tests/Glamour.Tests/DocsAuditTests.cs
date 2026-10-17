using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Glamour.Tests;

/// <summary>Cross-checks between the host, the web UI and the docs that no single unit test sees: reads the source files, runs no host.</summary>
public partial class DocsAuditTests
{
    // Located from this file, not the test dll: the build output may live outside the repo (--artifacts-path).
    private static string Root([CallerFilePath] string self = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", ".."));

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative));

    /// <summary>Every event type the host publishes (EventHub.Publish("x"), plus the snapshot every stream starts with).</summary>
    private static SortedSet<string> Published()
    {
        var types = new SortedSet<string>(StringComparer.Ordinal) { "snapshot" };
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in PublishCall().Matches(File.ReadAllText(file))) types.Add(m.Groups[1].Value);
        }
        return types;
    }

    // Known gaps found by the docs audit: delete each entry together with its fix.
    private static readonly string[] SpecNotYetDocumented = []; // SPEC "Event stream" has no row for these

    [Fact]
    public void The_host_publishes_the_events_this_test_knows_about()
    {
        // Guards the regexes below: if the publish style changes, this fails instead of the other tests passing on an empty set.
        Assert.Superset(new SortedSet<string> { "snapshot", "phase", "board", "honor", "parties", "automation", "game" }, Published());
    }

    [Fact]
    public void The_web_ui_listens_for_every_event_the_host_publishes()
    {
        // EventSource only delivers named events that have a listener, so a type missing from EVENTS is silently dropped.
        var now = Read(Path.Combine("web", "src", "lib", "now.ts"));
        var list = WebEventList().Match(now);
        Assert.True(list.Success, "EVENTS array not found in now.ts");
        var listened = Quoted().Matches(list.Groups[1].Value).Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Equal([], Published().Where(t => !listened.Contains(t)).ToArray());
    }

    [Fact]
    public void The_spec_event_table_matches_what_the_host_publishes()
    {
        if (!File.Exists(Path.Combine(Root(), "docs", "SPEC.md"))) return;  // the docs stay private: absent in the public repo
        var spec = Read(Path.Combine("docs", "SPEC.md"));
        var start = spec.IndexOf("## Event stream", StringComparison.Ordinal);
        Assert.True(start >= 0, "SPEC has no \"Event stream\" section");
        var end = spec.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var section = end < 0 ? spec[start..] : spec[start..end];
        var rows = SpecRow().Matches(section).Select(m => m.Groups[1].Value).ToHashSet();
        var published = Published();
        Assert.Equal([], published.Where(t => !rows.Contains(t) && !SpecNotYetDocumented.Contains(t)).ToArray());  // published but undocumented
        Assert.Equal([], rows.Where(t => !published.Contains(t)).ToArray());  // documented but never published
    }

    [Fact]
    public void Claude_md_stays_short()
    {
        var path = Path.Combine(Root(), "CLAUDE.md");
        if (!File.Exists(path)) return;  // git-ignored: absent on a fresh clone
        Assert.True(File.ReadAllLines(path).Length <= 100, "CLAUDE.md should stay under ~100 lines (its own rule)");
    }

    [Fact]
    public void The_private_codename_is_not_in_anything_shipped()
    {
        // Champion data may say Neeko (she is a champion); names, titles, manifests and project files must not.
        var files = new List<string> { Path.Combine("web", "index.html"), Path.Combine("web", "login.html"), Path.Combine("web", "pair.html"), Path.Combine("web", "public", "manifest.webmanifest"), Path.Combine("web", "public", "sw.js"), "Glamour.slnx" };
        files.AddRange(Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.csproj", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Root(), f)));
        Assert.Equal([], files.Where(f => Read(f).Contains("neeko", StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    [GeneratedRegex(@"Publish\(""([a-z-]+)""")]
    private static partial Regex PublishCall();

    [GeneratedRegex(@"export const EVENTS = \[(.*?)\]", RegexOptions.Singleline)]
    private static partial Regex WebEventList();

    [GeneratedRegex("'([a-z-]+)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^\| `([a-z-]+)` \|", RegexOptions.Multiline)]
    private static partial Regex SpecRow();
}
