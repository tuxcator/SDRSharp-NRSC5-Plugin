using System.Globalization;
using System.Text.Json;

namespace SDRSharp.NRSC5;

/// <summary>
/// The caching half of a network lookup, shared by the FCC licence query and the
/// transmitter site geocoder. Both answer questions whose answers barely change and
/// both are asked the same handful of questions over and over as the listener sweeps
/// the band, so both want the same thing: an in-memory hit, a disk hit across sessions,
/// and exactly one request in flight per key.
///
/// A null result is cached: it means the service answered and has nothing, which is as
/// durable an answer as any. A thrown exception is not cached, so being offline costs
/// nothing permanent and the next tune retries.
/// </summary>
internal sealed class LookupCache<TValue> where TValue : class
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _cache = new();
    private readonly Dictionary<string, Task<TValue?>> _inFlight = new();
    private readonly string _path;
    private readonly TimeSpan _lifetime;
    private readonly int _maxEntries;
    private bool _loaded;

    public LookupCache(string fileName, TimeSpan lifetime, int maxEntries)
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SDRSharp.NRSC5",
            fileName);
        _lifetime = lifetime;
        _maxEntries = maxEntries;
    }

    public Task<TValue?> GetOrAddAsync(string key, Func<CancellationToken, Task<TValue?>> fetch, CancellationToken token)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.Fetched < _lifetime)
                return Task.FromResult(cached.Value);

            if (_inFlight.TryGetValue(key, out var running)) return running;

            var task = RunAsync(key, fetch, token);
            _inFlight[key] = task;
            // Registered while the lock is held, so the continuation cannot clear the
            // entry before it has been added even if the fetch already finished.
            _ = task.ContinueWith(
                _ => { lock (_gate) _inFlight.Remove(key); },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            return task;
        }
    }

    private async Task<TValue?> RunAsync(string key, Func<CancellationToken, Task<TValue?>> fetch, CancellationToken token)
    {
        var value = await fetch(token).ConfigureAwait(false);
        lock (_gate)
        {
            _cache[key] = new Entry(value, DateTimeOffset.UtcNow);
            Trim();
            Save();
        }
        return value;
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void Trim()
    {
        if (_cache.Count <= _maxEntries) return;
        foreach (var stale in _cache.OrderBy(entry => entry.Value.Fetched)
                     .Take(_cache.Count - _maxEntries)
                     .Select(entry => entry.Key)
                     .ToList())
            _cache.Remove(stale);
    }

    /// <summary>
    /// Stamped into the file so a cache written by an older build is discarded rather
    /// than misread. Version 1 replaced a bare dictionary whose entries named the value
    /// differently; loading one of those as the current shape yielded an entry per
    /// station whose value was null, which reads as "the service has no record" and
    /// would have blanked the licence fields for the thirty days of its lifetime.
    /// Raise this whenever <see cref="Entry"/> or TValue changes shape.
    /// </summary>
    private const int FormatVersion = 1;

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path)) return;
            var stored = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_path));
            if (stored is null || stored.Version != FormatVersion || stored.Entries is null) return;
            foreach (var (key, entry) in stored.Entries) _cache[key] = entry;
        }
        catch
        {
            // A corrupt or unreadable cache is not worth reporting: it is rebuilt on use.
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (directory is not null) Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(new CacheFile(FormatVersion, _cache)));
        }
        catch
        {
            // Read-only or roaming profile. The in-memory cache still does its job.
        }
    }

    internal sealed record CacheFile(int Version, Dictionary<string, Entry>? Entries);

    /// <summary>Rounds a coordinate pair into a cache key: 4 decimals is about 11 metres.</summary>
    internal static string CoordinateKey(float latitude, float longitude) => string.Format(
        CultureInfo.InvariantCulture, "{0:0.0000},{1:0.0000}", latitude, longitude);

    internal sealed record Entry(TValue? Value, DateTimeOffset Fetched);
}
