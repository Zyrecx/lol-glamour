namespace Glamour.Tests;

/// <summary>Real client responses kept in <c>Fixtures/</c> (copied from tools/logs, other players' names and ids replaced), copied next to the test dll.</summary>
internal static class FixtureFiles
{
    public static string PathOf(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", file);
}
