using Kakudai.Core.Game;
using NUnit.Framework;

namespace Kakudai.Tests;

public class GameLogTests
{
    private const string Header1 = "----------------------------------------------------------";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(500);

    private string _dir = "";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kakudai-logtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Header(string running) =>
        string.Join("\n", Header1, "runtime Log (LogLevel: Verbose)", running,
            "Environment: Windows (Microsoft Windows NT 10.0.26200.0), 24 cores ", Header1) + "\n";

    private static string Sel(string display) =>
        $"2026-11-18 20:02:01 [verbose]: Game-wide working beatmap updated to {display}\n";

    private string Write(string name, string content, DateTime lastWriteUtc)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        File.SetLastWriteTimeUtc(p, lastWriteUtc);
        return p;
    }

    private static void Append(string path, string text)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var b = System.Text.Encoding.UTF8.GetBytes(text);
        fs.Write(b, 0, b.Length);
    }

    private static (LogWatcher w, List<GameSelection> got, Func<int, TimeSpan?, bool> waitCount) Make(string dir)
    {
        var w = new LogWatcher(dir, TimeSpan.FromMilliseconds(50));
        var got = new List<GameSelection>();
        var sig = new SemaphoreSlim(0);
        w.SelectionChanged += s => { lock (got) got.Add(s); sig.Release(); };
        bool Wait(int n, TimeSpan? t = null)
        {
            var end = DateTime.UtcNow + (t ?? Timeout);
            while (true)
            {
                lock (got) if (got.Count >= n) return true;
                var left = end - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !sig.Wait(left)) { lock (got) return got.Count >= n; }
            }
        }
        return (w, got, Wait);
    }

    [Test]
    public void ParseSelection_RealLine()
    {
        var line = "2026-11-18 20:02:01 [verbose]: Game-wide working beatmap updated to goreshit - satori de pon! (Rita Summers) [8 years of high sr slop]";
        Assert.That(GameLog.ParseSelection(line), Is.EqualTo("goreshit - satori de pon! (Rita Summers) [8 years of high sr slop]"));
        Assert.That(GameLog.ParseSelection("2026-11-18 20:02:01 [verbose]: Invalidating working beatmap cache for x"), Is.Null);
        Assert.That(GameLog.ParseSelection("random"), Is.Null);
    }

    [Test]
    public void ParseClient()
    {
        Assert.That(GameLog.ParseClient("Running osu-torii 2026.1117.1.0 on .NET 11.0.0"), Is.EqualTo("torii"));
        Assert.That(GameLog.ParseClient("Running osu! 2026.1013.0-lazer"), Is.EqualTo("lazer"));
    }

    [Test]
    public void Watcher_EmitsOnAppend()
    {
        var p = Write("100.runtime.log", Header("Running osu-torii 2026.1117.1.0 on .NET 11.0.0"), DateTime.UtcNow);
        var (w, got, wait) = Make(_dir);
        using (w)
        {
            w.Start();
            Assert.That(wait(1, Short), Is.False, "no selection yet");
            Append(p, Sel("A - B (C) [D]"));
            Assert.That(wait(1, null), Is.True);
            Assert.That(got[0], Is.EqualTo(new GameSelection("torii", "A - B (C) [D]")));
        }
    }

    [Test]
    public void Watcher_EmitsLastSelectionOnStart()
    {
        Write("100.runtime.log", Header("Running osu! 2026.1013.0-lazer") + Sel("One - x (m) [a]") + Sel("Two - y (m) [b]") + "2026-11-18 20:02:02 [verbose]: Invalidating working beatmap cache for z\n", DateTime.UtcNow);
        var (w, got, wait) = Make(_dir);
        using (w)
        {
            w.Start();
            Assert.That(wait(1, null), Is.True);
            Assert.That(got[0], Is.EqualTo(new GameSelection("lazer", "Two - y (m) [b]")));
            Assert.That(wait(2, Short), Is.False, "no duplicates");
        }
    }

    [Test]
    public void Watcher_IgnoresPartialLineUntilComplete()
    {
        var p = Write("100.runtime.log", Header("Running osu-torii 1 on .NET 11"), DateTime.UtcNow);
        var (w, got, wait) = Make(_dir);
        using (w)
        {
            w.Start();
            var full = Sel("Part - x (m) [a]").TrimEnd('\n');
            Append(p, full[..40]);
            Assert.That(wait(1, Short), Is.False);
            Append(p, full[40..] + "\n");
            Assert.That(wait(1, null), Is.True);
            Assert.That(got[0].Display, Is.EqualTo("Part - x (m) [a]"));
        }
    }

    [Test]
    public void Watcher_SwitchesToNewerLog()
    {
        var t = DateTime.UtcNow;
        Write("100.runtime.log", Header("Running osu! 1-lazer") + Sel("Old - o (m) [a]"), t.AddMinutes(-10));
        var (w, got, wait) = Make(_dir);
        using (w)
        {
            w.Start();
            Assert.That(wait(1, null), Is.True);
            Assert.That(got[0], Is.EqualTo(new GameSelection("lazer", "Old - o (m) [a]")));
            Write("200.runtime.log", Header("Running osu-torii 2026.1117.1.0 on .NET 11.0.0") + Sel("New - n (m) [b]"), t.AddMinutes(1));
            Assert.That(wait(2, null), Is.True);
            Assert.That(got[1], Is.EqualTo(new GameSelection("torii", "New - n (m) [b]")));
        }
    }

    [Test]
    public void Watcher_NoEventsAfterDispose()
    {
        var p = Write("100.runtime.log", Header("Running osu-torii 1 on .NET 11"), DateTime.UtcNow);
        var (w, got, wait) = Make(_dir);
        w.Start();
        w.Dispose();
        Append(p, Sel("Late - x (m) [a]"));
        Assert.That(wait(1, Short), Is.False);
    }

    [Test]
    public void Watcher_ThrowingSubscriberDoesNotStopLaterEvents()
    {
        var p = Write("100.runtime.log", Header("Running osu-torii 1 on .NET 11"), DateTime.UtcNow);
        var (w, got, wait) = Make(_dir);
        using (w)
        {
            var first = true;
            w.SelectionChanged += _ => { if (first) { first = false; throw new InvalidOperationException("boom"); } };
            w.Start();
            Append(p, Sel("A - a (m) [x]"));
            Assert.That(wait(1, null), Is.True);
            Append(p, Sel("B - b (m) [y]"));
            Assert.That(wait(2, null), Is.True);
            Assert.That(got[1].Display, Is.EqualTo("B - b (m) [y]"));
        }
    }
}
