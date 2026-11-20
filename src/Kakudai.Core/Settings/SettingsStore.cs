using System.Text.Json;
using System.Text.Json.Serialization;
using Kakudai.Core.Training;

namespace Kakudai.Core.Settings;

/// <summary>A profile slot: an optional label and the settings it holds (null = empty slot).</summary>
public sealed record Profile(string? Name, TrainerSettings? Settings);

/// <summary>The last settings and the profiles. There are always <see cref="ProfileCount"/> profiles: missing slots
/// are empty, extra ones are dropped.</summary>
public sealed record AppSettings
{
    public const int ProfileCount = 4;

    public AppSettings(TrainerSettings? current, Profile?[]? profiles)
    {
        Current = current!;
        Profiles = profiles!;
    }

    public TrainerSettings Current { get; init => field = value ?? new TrainerSettings(); }

    public Profile[] Profiles { get; init => field = Fit(value); }

    private static Profile[] Fit(Profile?[]? profiles)
        => Enumerable.Range(0, ProfileCount)
            .Select(i => profiles is not null && i < profiles.Length && profiles[i] is { } p ? p : new Profile(null, null))
            .ToArray();
}

/// <summary>Reads and writes <c>settings.json</c> (normally <c>%LOCALAPPDATA%\Kakudai\settings.json</c>).</summary>
public static class SettingsStore
{
    /// <summary>camelCase, strict about numbers (no "NaN", no numbers in strings). The page's messages are read the
    /// same way.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict,
        WriteIndented = true,
    };

    /// <summary>The settings saved at <paramref name="path"/>, or the defaults if the file is missing or unreadable.</summary>
    public static AppSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var loaded = JsonSerializer.Deserialize<AppSettings>(stream, Json);
            bool usable = loaded is not null && IsValid(loaded.Current)
                          && loaded.Profiles.All(p => p.Settings is null || IsValid(p.Settings));
            return usable ? loaded! : Defaults();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return Defaults();
        }
    }

    /// <summary>Writes a temp file next to <paramref name="path"/>, then replaces the real one with it.</summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable.</exception>
    public static void Save(string path, AppSettings settings)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(settings);

        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(settings, Json));
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>True if every number of <paramref name="s"/> is finite (null optional values included) and the flip
    /// mode is a known one.</summary>
    public static bool IsValid(TrainerSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        double?[] numbers = [s.Factor, s.Rate, s.LockedBpm, s.Hp, s.Cs, s.Ar, s.Od];
        return numbers.All(n => n is not { } v || double.IsFinite(v)) && Enum.IsDefined(s.FlipMode);
    }

    private static AppSettings Defaults() => new(new TrainerSettings(), []);
}
