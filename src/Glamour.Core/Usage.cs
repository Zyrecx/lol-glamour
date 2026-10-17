namespace Glamour.Core;

/// <summary>
/// Anonymous usage counts (SPEC "Publishing": what Glamour did, never who for). The host's tally sends them once per game; tests and the demo host have none.
/// Keys are fixed words like "auto_lock" or "phone_hover", never ids or names.
/// </summary>
public interface IUsage
{
    void Count(string what);
}

/// <summary>Who asked for the client write running now: "auto" (a feature on its own), or the device whose request it serves ("pc", "phone").</summary>
public static class UsageOrigin
{
    private static readonly AsyncLocal<string?> By = new();

    public static string Current => By.Value ?? "auto";

    /// <summary>For the rest of this request's flow (the host's request middleware sets it).</summary>
    public static void Set(string by) => By.Value = by;
}
