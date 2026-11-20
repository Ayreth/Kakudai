using System.Text;

namespace Kakudai.Core.Game;

/// <summary>Selected map as announced by the game log. Client is "torii" or "lazer".</summary>
public sealed record GameSelection(string Client, string Display);

public static class GameLog
{
    private const string Marker = "Game-wide working beatmap updated to ";

    /// <summary>Returns the "{Artist} - {Title} ({Mapper}) [{Diff}]" text of a selection line, or null.</summary>
    public static string? ParseSelection(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var i = line.IndexOf(Marker, StringComparison.Ordinal);
        if (i < 0) return null;
        var display = line[(i + Marker.Length)..].Trim();
        return display.Length == 0 ? null : display;
    }

    public static string ParseClient(string headerLine) =>
        headerLine.Contains("torii", StringComparison.OrdinalIgnoreCase) ? "torii" : "lazer";
}

/// <summary>Follows the most recent *.runtime.log of the game and raises an event when the selected map changes.</summary>
public sealed class LogWatcher : IDisposable
{
    private readonly string _dir;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private Timer? _timer;
    private volatile bool _disposed;

    private string? _path;
    private long _offset;
    private int _lineNo;
    private string _client = "lazer";
    private readonly List<byte> _pending = new();
    private GameSelection? _last;

    public event Action<GameSelection>? SelectionChanged;

    public LogWatcher(string logsDir, TimeSpan pollInterval)
    {
        _dir = logsDir;
        _interval = pollInterval;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer != null) return;
            _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, _interval);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Poll()
    {
        List<GameSelection> events;
        lock (_gate)
        {
            if (_disposed) return;
            try { events = PollCore(); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
        }

        // Raised outside the lock: a slow or UI-marshalling subscriber cannot deadlock Dispose,
        // and a throwing subscriber cannot take down the timer thread (or the process).
        foreach (var gs in events)
        {
            if (_disposed) return;
            try { SelectionChanged?.Invoke(gs); } catch { }
        }
    }

    private List<GameSelection> PollCore()
    {
        if (!Directory.Exists(_dir)) return new List<GameSelection>();
        var newest = new DirectoryInfo(_dir).EnumerateFiles("*.runtime.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (newest == null) return new List<GameSelection>();

        var firstScan = false;
        if (!string.Equals(newest.FullName, _path, StringComparison.OrdinalIgnoreCase))
        {
            _path = newest.FullName;
            ResetFileState();
            firstScan = true;
        }

        using var fs = new FileStream(_path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _offset) { ResetFileState(); firstScan = true; }
        if (fs.Length == _offset) return new List<GameSelection>();

        fs.Seek(_offset, SeekOrigin.Begin);
        var buf = new byte[fs.Length - _offset];
        var read = 0;
        while (read < buf.Length)
        {
            var n = fs.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        _offset += read;

        var selections = new List<string>();
        for (var i = 0; i < read; i++)
        {
            var b = buf[i];
            if (b != (byte)'\n') { _pending.Add(b); continue; }
            var line = Encoding.UTF8.GetString(_pending.ToArray()).TrimEnd('\r');
            _pending.Clear();
            _lineNo++;
            if (_lineNo == 3) _client = GameLog.ParseClient(line);
            var sel = GameLog.ParseSelection(line);
            if (sel != null) selections.Add(sel);
        }

        var result = new List<GameSelection>();
        if (selections.Count == 0) return result;
        if (firstScan) selections = new List<string> { selections[^1] };
        foreach (var display in selections)
        {
            var gs = new GameSelection(_client, display);
            if (gs == _last) continue;
            _last = gs;
            result.Add(gs);
        }
        return result;
    }

    private void ResetFileState()
    {
        _offset = 0;
        _lineNo = 0;
        _client = "lazer";
        _pending.Clear();
    }
}
