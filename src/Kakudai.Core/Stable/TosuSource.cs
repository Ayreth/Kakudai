using System.Net.Http;
using System.Text.Json;

namespace Kakudai.Core.Stable;

/// <summary>
/// The map osu!stable shows, asked to tosu (github.com/tosuapp/tosu) on this machine, for when Kakudai cannot read the
/// game's memory itself (Linux with ptrace restrictions). Only 127.0.0.1 is ever contacted.
/// </summary>
public sealed class TosuSource : IDisposable
{
    public const string DefaultAddress = "http://127.0.0.1:24050/json/v2";

    private readonly HttpClient _http;
    private readonly string _address;

    public TosuSource(string address = DefaultAddress, HttpMessageHandler? handler = null)
    {
        _address = address;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(1);
    }

    /// <summary>The stable map tosu reports, or null (tosu not running, lazer, no map, file not found).</summary>
    public async Task<StableSelection?> ReadAsync(StableGame? game, CancellationToken cancellationToken)
    {
        try
        {
            var json = await _http.GetStringAsync(_address, cancellationToken).ConfigureAwait(false);
            return Parse(json, game);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Reads a tosu v2 message: <c>client</c>, <c>beatmap.checksum</c>, <c>directPath.beatmapFile</c>, else
    /// <c>folders.songs</c> + <c>folders.beatmap</c> + <c>files.beatmap</c>.</summary>
    internal static StableSelection? Parse(string json, StableGame? game)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var client = Str(root, "client");
        if (client.Length > 0 && !client.Equals("stable", StringComparison.OrdinalIgnoreCase)) return null;

        var checksum = Str(Obj(root, "beatmap"), "checksum");
        var path = Str(Obj(root, "directPath"), "beatmapFile");
        if (path.Length == 0 || !File.Exists(path))
        {
            var folders = Obj(root, "folders");
            var songs = Str(folders, "songs");
            var folder = Str(folders, "beatmap");
            var file = Str(Obj(root, "files"), "beatmap");
            if (songs.Length == 0 || folder.Length == 0 || file.Length == 0) return null;
            path = Path.Combine(songs, folder, file);
        }
        if (checksum.Length == 0 || !path.EndsWith(".osu", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return null;
        return new StableSelection(checksum, path, game);
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public void Dispose() => _http.Dispose();
}
