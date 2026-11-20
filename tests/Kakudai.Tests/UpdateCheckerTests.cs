using System.Net;
using System.Text;
using Kakudai.Core.Updates;
using NUnit.Framework;

namespace Kakudai.Tests;

public class UpdateCheckerTests
{
    private static readonly Version Current = new(2, 0, 0);
    private const string ReleasePage = "https://github.com/Ayreth/Kakudai/releases/tag/v2.1.0";

    // ------------------------------------------------------------------ versions

    [TestCase("v2.1.0", 2, 1, 0)]
    [TestCase("V2.1.0", 2, 1, 0)]
    [TestCase("2.0.1", 2, 0, 1)]
    [TestCase("v10.20.30", 10, 20, 30)]
    public void Parse_ReleaseTag(string tag, int major, int minor, int patch)
        => Assert.That(ReleaseVersion.Parse(tag), Is.EqualTo(new Version(major, minor, patch)));

    [TestCase(null)]
    [TestCase("")]
    [TestCase("latest")]
    [TestCase("v")]
    [TestCase("v2.1")]
    [TestCase("v2.1.0.4")]
    [TestCase("v2.x.0")]
    [TestCase("v-1.0.0")]
    [TestCase("v2..0")]
    [TestCase(" v2.1.0")]
    [TestCase("v2.1.0+build5")]
    [TestCase("v99999999999.0.0")]
    public void Parse_Garbage_IsNull(string? tag) => Assert.That(ReleaseVersion.Parse(tag), Is.Null);

    [TestCase("v2.1.0-beta")]
    [TestCase("v2.1.0-rc.1")]
    [TestCase("v3.0.0-alpha")]
    public void Parse_Prerelease_IsNull(string tag) => Assert.That(ReleaseVersion.Parse(tag), Is.Null);

    [TestCase("v2.0.1", true)]
    [TestCase("v2.1.0", true)]
    [TestCase("v3.0.0", true)]
    [TestCase("v2.0.0", false)]
    [TestCase("v1.9.9", false)]
    [TestCase("v1.99.0", false)]
    [TestCase("v2.1.0-beta", false)]
    [TestCase("garbage", false)]
    [TestCase(null, false)]
    public void IsNewer_ComparedToRunningVersion(string? tag, bool newer)
        => Assert.That(ReleaseVersion.IsNewer(tag, Current), Is.EqualTo(newer));

    [Test]
    public void IsNewer_IgnoresTheRevisionOfTheAssemblyVersion()
    {
        // The assembly of <Version>2.0.0</Version> is 2.0.0.0.
        Assert.That(ReleaseVersion.IsNewer("v2.0.0", new Version(2, 0, 0, 0)), Is.False);
        Assert.That(ReleaseVersion.IsNewer("v2.0.1", new Version(2, 0, 0, 7)), Is.True);
        Assert.That(ReleaseVersion.IsNewer("v2.0.0", new Version(2, 0)), Is.False);
    }

    [Test]
    public void Display_IsThreeNumbers() => Assert.That(ReleaseVersion.Display(new Version(2, 0, 0, 0)), Is.EqualTo("2.0.0"));

    // ------------------------------------------------------------------ release page

    [TestCase("https://github.com/Ayreth/Kakudai/releases/tag/v2.1.0", true)]
    [TestCase("https://github.com/Ayreth/Kakudai/releases/latest", true)]
    [TestCase("http://github.com/Ayreth/Kakudai/releases/tag/v2.1.0", false)]
    [TestCase("https://github.com/Ayreth/Other/releases/tag/v2.1.0", false)]
    [TestCase("https://github.com/Ayreth/Kakudai-evil/releases", false)]
    [TestCase("https://github.com/Ayreth/Kakudai/../../evil/x", false)]
    [TestCase("https://github.com.evil.com/Ayreth/Kakudai/releases", false)]
    [TestCase("https://user@github.com/Ayreth/Kakudai/releases", false)]
    [TestCase("https://github.com:8443/Ayreth/Kakudai/releases", false)]
    [TestCase("file:///C:/Windows/notepad.exe", false)]
    [TestCase("not a url", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void IsReleasePage_OnlyThisRepositoryOnGitHub(string? url, bool allowed)
        => Assert.That(UpdateChecker.IsReleasePage(url), Is.EqualTo(allowed));

    // ------------------------------------------------------------------ checking (no network: a fake handler)

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, cancellationToken);
        }
    }

    private static FakeHandler Responds(HttpStatusCode status, string body) => new((_, _) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

    private static string Release(string tag, string url = ReleasePage, bool prerelease = false, bool draft = false)
        => $$"""{"tag_name":"{{tag}}","html_url":"{{url}}","prerelease":{{(prerelease ? "true" : "false")}},"draft":{{(draft ? "true" : "false")}},"name":"Kakudai {{tag}}"}""";

    // Awaited inside the using: the checker (and its HttpClient) lives until the answer is in.
    private static async Task<ReleaseInfo?> Check(HttpMessageHandler handler, TimeSpan? timeout = null, CancellationToken token = default)
    {
        using var checker = new UpdateChecker(Current, handler, timeout);
        return await checker.CheckAsync(token);
    }

    [Test]
    public async Task Check_NewerRelease_ReturnsItsVersionAndPage()
    {
        var handler = Responds(HttpStatusCode.OK, Release("v2.1.0"));

        var release = await Check(handler);

        Assert.That(release, Is.Not.Null);
        Assert.That(release!.Version, Is.EqualTo(new Version(2, 1, 0)));
        Assert.That(release.DisplayVersion, Is.EqualTo("2.1.0"));
        Assert.That(release.Url, Is.EqualTo(ReleasePage));
    }

    [Test]
    public async Task Check_SendsAnAnonymousRequestToTheLatestRelease()
    {
        var handler = Responds(HttpStatusCode.OK, Release("v2.1.0"));

        await Check(handler);

        var request = handler.Requests.Single();
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(request.RequestUri, Is.EqualTo(new Uri("https://api.github.com/repos/Ayreth/Kakudai/releases/latest")));
        Assert.That(request.Headers.UserAgent.ToString(), Is.EqualTo("Kakudai/2.0.0"));
        Assert.That(request.Headers.Accept.Select(a => a.MediaType), Is.EqualTo(new[] { "application/vnd.github+json" }));
        Assert.That(request.Headers.Authorization, Is.Null);
        Assert.That(request.Headers.Contains("Cookie"), Is.False);
        Assert.That(request.Content, Is.Null);
        // Nothing else about the player: only these headers (and API version) are sent.
        Assert.That(request.Headers.Select(h => h.Key),
            Is.EquivalentTo(new[] { "User-Agent", "Accept", "X-GitHub-Api-Version" }));
    }

    [TestCase("v2.0.0")]
    [TestCase("v1.0.0")]
    [TestCase("v2.1.0-beta")]
    [TestCase("nightly")]
    public async Task Check_NotNewer_IsNull(string tag)
        => Assert.That(await Check(Responds(HttpStatusCode.OK, Release(tag))), Is.Null);

    [Test]
    public async Task Check_PrereleaseOrDraft_IsNull()
    {
        Assert.That(await Check(Responds(HttpStatusCode.OK, Release("v2.1.0", prerelease: true))), Is.Null);
        Assert.That(await Check(Responds(HttpStatusCode.OK, Release("v2.1.0", draft: true))), Is.Null);
    }

    [Test]
    public async Task Check_ReleasePage_IsKeptNormalised()
    {
        var release = await Check(Responds(HttpStatusCode.OK,
            Release("v2.1.0", "https://GitHub.com/Ayreth/Kakudai/releases/./tag/x/../v2.1.0")));

        Assert.That(release!.Url, Is.EqualTo(ReleasePage));
    }

    [Test]
    public async Task Check_PageOutsideTheRepository_IsNull()
        => Assert.That(await Check(Responds(HttpStatusCode.OK, Release("v2.1.0", "https://example.com/kakudai.exe"))), Is.Null);

    [TestCase(HttpStatusCode.NotFound, """{"message":"Not Found"}""")]
    [TestCase(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""")]
    [TestCase(HttpStatusCode.TooManyRequests, "")]
    [TestCase(HttpStatusCode.InternalServerError, "oops")]
    public async Task Check_HttpError_IsNull(HttpStatusCode status, string body)
        => Assert.That(await Check(Responds(status, body)), Is.Null);

    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[]")]
    [TestCase("null")]
    [TestCase("""{"html_url":"https://github.com/Ayreth/Kakudai/releases/tag/v2.1.0"}""")]
    [TestCase("""{"tag_name":"v2.1.0"}""")]
    [TestCase("""{"tag_name":5,"html_url":"https://github.com/Ayreth/Kakudai/releases/tag/v2.1.0"}""")]
    [TestCase("""{"tag_name":"v2.1.0","html_url":"https://github.com/Ayreth/Kakudai/releases/tag/v2.1.0","prerelease":"yes"}""")]
    public async Task Check_BadJson_IsNull(string body)
        => Assert.That(await Check(Responds(HttpStatusCode.OK, body)), Is.Null);

    [Test]
    public async Task Check_Offline_IsNull()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("No such host is known."));
        Assert.That(await Check(handler), Is.Null);
    }

    [Test]
    public async Task Check_NoAnswer_IsNullAfterTheTimeout()
    {
        var timeout = TimeSpan.FromMilliseconds(300);
        var cancelledAfter = TimeSpan.Zero;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var handler = new FakeHandler(async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelledAfter = watch.Elapsed;
                throw;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var release = await Check(handler, timeout);

        Assert.That(release, Is.Null);
        // The request was given up by the timeout itself: not before it, and long before the 5 s default.
        Assert.That(cancelledAfter, Is.GreaterThanOrEqualTo(timeout));
        Assert.That(watch.Elapsed, Is.GreaterThanOrEqualTo(timeout).And.LessThan(TimeSpan.FromSeconds(4)));
    }

    [Test]
    public async Task Check_CancelledByCaller_IsNull()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.That(await Check(Responds(HttpStatusCode.OK, Release("v2.1.0")), token: cts.Token), Is.Null);
    }

    [Test]
    public void DefaultTimeout_IsShort() => Assert.That(UpdateChecker.DefaultTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
}
