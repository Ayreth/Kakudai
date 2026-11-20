using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Kakudai.Core.Updates;

/// <summary>Versions of the release tags (<c>vX.Y.Z</c>) compared to the running one.</summary>
public static class ReleaseVersion
{
    /// <summary>
    /// The version of a release tag: <c>vX.Y.Z</c> or <c>X.Y.Z</c>, three numbers and nothing else. Null for anything
    /// else, a prerelease (<c>v2.1.0-beta</c>) included.
    /// </summary>
    public static Version? Parse(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        var text = tag[0] is 'v' or 'V' ? tag[1..] : tag;
        var parts = text.Split('.');
        if (parts.Length != 3) return null;

        var numbers = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return null;
        }
        return new Version(numbers[0], numbers[1], numbers[2]);
    }

    /// <summary>True if <paramref name="tag"/> is a release more recent than <paramref name="current"/> (whose
    /// revision, the fourth number of an assembly version, does not count).</summary>
    public static bool IsNewer(string? tag, Version current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return Parse(tag) is { } latest && latest > Normalize(current);
    }

    /// <summary>The three numbers of a version: "2.0.0" for the assembly version 2.0.0.0.</summary>
    public static string Display(Version version)
    {
        var v = Normalize(version);
        return FormattableString.Invariant($"{v.Major}.{v.Minor}.{v.Build}");
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}

/// <summary>A published release more recent than the running Kakudai.</summary>
/// <param name="Url">Its page on GitHub, normalised, always under <see cref="UpdateChecker.ReleasePagePrefix"/>.</param>
public sealed record ReleaseInfo(Version Version, string Url)
{
    public string DisplayVersion => ReleaseVersion.Display(Version);
}

/// <summary>
/// Asks GitHub, once, whether a newer Kakudai was published. The request is anonymous: no cookie, no credential,
/// only a User-Agent naming Kakudai and its version. Any failure (offline, 404, rate limit, unexpected answer, no
/// answer within the timeout) means "no update".
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/Ayreth/Kakudai/releases/latest";
    public const string ReleasePagePrefix = "https://github.com/Ayreth/Kakudai/";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly Version _current;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    /// <param name="current">The running version (the assembly version of the app).</param>
    /// <param name="handler">Sends the request; by default a handler without cookies or credentials. Tests pass a
    /// fake one: nothing here needs the network.</param>
    /// <param name="timeout">How long to wait for GitHub; <see cref="DefaultTimeout"/> by default.</param>
    public UpdateChecker(Version current, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        _current = current;
        _timeout = timeout ?? DefaultTimeout;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan, // the timeout is _timeout, through a cancellation token
        };
    }

    /// <summary>The newer release, or null if there is none or the question could not be answered. Never throws.</summary>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Kakudai", ReleaseVersion.Display(_current)));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return Read(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException
                                       or InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True for an https page of this repository on github.com (no other host, port or user, and nothing that
    /// leaves the repository once normalised).
    /// </summary>
    public static bool IsReleasePage(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps
               && uri.IsDefaultPort
               && string.IsNullOrEmpty(uri.UserInfo)
               && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
               && uri.AbsoluteUri.StartsWith(ReleasePagePrefix, StringComparison.Ordinal);
    }

    public void Dispose() => _http.Dispose();

    private ReleaseInfo? Read(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!Flag(root, "prerelease", out var prerelease) || prerelease) return null;
        if (!Flag(root, "draft", out var draft) || draft) return null;
        if (!Text(root, "tag_name", out var tag) || !Text(root, "html_url", out var url)) return null;
        if (!ReleaseVersion.IsNewer(tag, _current) || !IsReleasePage(url)) return null;
        // Kept normalised: the address opened later is exactly the one IsReleasePage checked.
        return new ReleaseInfo(ReleaseVersion.Parse(tag)!, new Uri(url).AbsoluteUri);
    }

    private static bool Text(JsonElement root, string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.String) return false;
        value = e.GetString()!;
        return true;
    }

    /// <summary>A missing flag is false; anything but a boolean makes the answer unreadable.</summary>
    private static bool Flag(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var e)) return true;
        if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = e.GetBoolean();
        return true;
    }
}
