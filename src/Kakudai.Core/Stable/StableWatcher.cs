namespace Kakudai.Core.Stable;

/// <summary>
/// Follows the map selected in osu!stable: from its memory every <c>interval</c>, or from tosu (once a second) when
/// the memory cannot be read. Raises <see cref="SelectionChanged"/> when the map changes, <see cref="StateChanged"/>
/// when what can be read changes.
/// </summary>
public sealed class StableWatcher : IDisposable
{
    private static readonly TimeSpan TosuInterval = TimeSpan.FromSeconds(1);

    private readonly StableReader _reader;
    private readonly TosuSource _tosu;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private string? _lastChecksum;
    private StableState? _lastState;

    public event Action<StableSelection>? SelectionChanged;

    /// <summary>The game's state as far as Kakudai can read it; with tosu standing in, <see cref="StableState.Reading"/>.</summary>
    public event Action<StableState>? StateChanged;

    public StableWatcher(TimeSpan interval) : this(new StableReader(), new TosuSource(), interval)
    {
    }

    internal StableWatcher(StableReader reader, TosuSource tosu, TimeSpan interval)
    {
        _reader = reader;
        _tosu = tosu;
        _interval = interval;
    }

    public void Start() => _loop ??= Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var token = _stop.Token;
        var nextTosu = DateTime.MinValue;
        while (!token.IsCancellationRequested)
        {
            var selection = _reader.Read();
            var state = _reader.State;

            // tosu stands in when osu! runs but cannot be read; asked once a second, never when osu! is not there.
            if (selection is null && state == StableState.Denied && DateTime.UtcNow >= nextTosu)
            {
                nextTosu = DateTime.UtcNow + TosuInterval;
                selection = await _tosu.ReadAsync(_reader.Game, token).ConfigureAwait(false);
                if (selection is not null) state = StableState.Reading;
            }

            if (state != _lastState)
            {
                _lastState = state;
                Raise(() => StateChanged?.Invoke(state));
            }
            if (selection is not null && selection.Checksum != _lastChecksum)
            {
                _lastChecksum = selection.Checksum;
                Raise(() => SelectionChanged?.Invoke(selection));
            }
            if (state == StableState.NotRunning) _lastChecksum = null; // the same map, once the game is back, is news

            try
            {
                await Task.Delay(_interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static void Raise(Action raise)
    {
        try
        {
            raise();
        }
        catch
        {
            // A throwing subscriber must not stop the watcher.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _reader.Dispose();
        _tosu.Dispose();
        _stop.Dispose();
    }
}
