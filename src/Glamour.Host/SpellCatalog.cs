using System.Text.Json;

namespace Glamour.Host;

/// <summary>Modes is Data Dragon's list (CLASSIC, ARAM, URF, ...); the UI filters on the game mode of the queue.</summary>
public sealed record SpellInfo(int Id, string Name, string Icon, IReadOnlyList<string> Modes);
public sealed record SpellList(string Version, IReadOnlyList<SpellInfo> Spells);

public interface ISpellSource
{
    Task<SpellList> FetchAsync(CancellationToken ct);
}

/// <summary>Summoner spell names and icons from Riot's Data Dragon CDN (no client needed, same as the champion list).</summary>
public sealed class DataDragonSpellSource(IHttpClientFactory http) : ISpellSource
{
    private const string Base = "https://ddragon.leagueoflegends.com";

    public async Task<SpellList> FetchAsync(CancellationToken ct)
    {
        var client = http.CreateClient("ddragon");
        using var versions = JsonDocument.Parse(await client.GetStringAsync($"{Base}/api/versions.json", ct));
        var version = versions.RootElement[0].GetString() ?? throw new InvalidDataException("no version");
        using var data = JsonDocument.Parse(await client.GetStringAsync($"{Base}/cdn/{version}/data/en_US/summoner.json", ct));
        return Parse(version, data.RootElement.GetProperty("data"));
    }

    /// <summary>Each entry has `key` (the numeric id as text), `name`, `modes` and `image.full` (the icon file).</summary>
    public static SpellList Parse(string version, JsonElement data) => new(version,
        [.. data.EnumerateObject()
            .Select(p => new SpellInfo(int.Parse(p.Value.GetProperty("key").GetString()!), p.Value.GetProperty("name").GetString()!,
                $"{Base}/cdn/{version}/img/spell/{p.Value.GetProperty("image").GetProperty("full").GetString()}",
                [.. p.Value.GetProperty("modes").EnumerateArray().Select(m => m.GetString()).OfType<string>()]))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)]);
}

/// <summary>Spells change a few times a year: keep one copy in memory for the whole run, retry a failed fetch after a minute.</summary>
public sealed class SpellCatalog(ISpellSource source, TimeProvider time, ILogger<SpellCatalog> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SpellList? _list;
    private DateTimeOffset _retryAt;

    public async Task<SpellList?> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_list is not null || time.GetUtcNow() < _retryAt) return _list;
            try { _list = await source.FetchAsync(ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or KeyNotFoundException or FormatException or InvalidOperationException && !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Could not load the summoner spell list");
                _retryAt = time.GetUtcNow() + TimeSpan.FromMinutes(1);
            }
            return _list;
        }
        finally { _gate.Release(); }
    }
}
