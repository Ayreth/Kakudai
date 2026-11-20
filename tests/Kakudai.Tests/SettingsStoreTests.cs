using Kakudai.Core.Osu;
using Kakudai.Core.Settings;
using Kakudai.Core.Training;
using NUnit.Framework;

namespace Kakudai.Tests;

public class SettingsStoreTests
{
    private string dir = "";
    private string path = "";

    [SetUp]
    public void Up()
    {
        dir = Path.Combine(Path.GetTempPath(), "kakudai-settings-" + Guid.NewGuid().ToString("N"));
        path = Path.Combine(dir, "Kakudai", "settings.json");
    }

    [TearDown]
    public void Down()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    private static void AssertDefaults(AppSettings s)
    {
        Assert.That(s.Current, Is.EqualTo(new TrainerSettings()));
        Assert.That(s.Profiles, Has.Length.EqualTo(4));
        Assert.That(s.Profiles, Has.All.EqualTo(new Profile(null, null)));
    }

    [Test]
    public void SettingsStore_RoundTrip()
    {
        var current = new TrainerSettings(Factor: 2.25, CrushStreams: true, Rate: 1.1, LockedBpm: 200, Ar: 9.5,
            ArLocked: true, FlipMap: true, FlipMode: FlipMode.Both, EmulateHr: true, ScaleAr: false, ChangePitch: true);
        var profiles = new[]
        {
            new Profile("Vitesse", current),
            new Profile(null, null),
            new Profile(null, new TrainerSettings(Factor: 3, KiaiOnly: true, AccentAngles: true)),
            new Profile("vide", null),
        };

        SettingsStore.Save(path, new AppSettings(current, profiles));
        var loaded = SettingsStore.Load(path);

        Assert.That(loaded.Current, Is.EqualTo(current));
        Assert.That(loaded.Profiles, Is.EqualTo(profiles));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!), Is.EqualTo(new[] { path }), "no temp file left");

        // Saving again replaces the file.
        SettingsStore.Save(path, new AppSettings(new TrainerSettings(Factor: 1), []));
        Assert.That(SettingsStore.Load(path).Current.Factor, Is.EqualTo(1));
    }

    [Test]
    public void SettingsStore_OldHrField_IsIgnored()
    {
        // Settings saved before "HR" and "CS HR" became "Flip map" and "Emulate HR": the old fields are dropped.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"current\": { \"factor\": 2, \"hr\": true, \"csHr\": true } }");

        var loaded = SettingsStore.Load(path);

        Assert.That(loaded.Current, Is.EqualTo(new TrainerSettings(Factor: 2)));
    }

    [Test]
    public void SettingsStore_FlipMode_IsAString()
    {
        SettingsStore.Save(path, new AppSettings(new TrainerSettings(FlipMode: FlipMode.Horizontal), []));
        Assert.That(File.ReadAllText(path), Does.Contain("\"flipMode\": \"horizontal\""));
        Assert.That(SettingsStore.Load(path).Current.FlipMode, Is.EqualTo(FlipMode.Horizontal));
    }

    [TestCase("\"diagonal\"")]
    [TestCase("7")]
    public void SettingsStore_UnknownFlipMode_GivesDefaults(string mode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"current\": { \"factor\": 2, \"flipMode\": " + mode + " } }");
        AssertDefaults(SettingsStore.Load(path));
    }

    [Test]
    public void SettingsStore_CorruptFile_GivesDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"current\": { \"factor\": ");

        AssertDefaults(SettingsStore.Load(path));
    }

    [Test]
    public void SettingsStore_MissingFile_GivesDefaults()
        => AssertDefaults(SettingsStore.Load(path));

    [TestCase("null")]
    [TestCase("[]")]
    [TestCase("{}")]
    [TestCase("{\"current\": {\"factor\": \"NaN\"}}")]
    public void SettingsStore_UnusableContent_GivesDefaults(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);

        AssertDefaults(SettingsStore.Load(path));
    }

    [Test]
    public void SettingsStore_PartialFile_KeepsDefaultsAndAlwaysFourProfiles()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"current\": {\"factor\": 2}, \"profiles\": [{\"name\": \"a\"}, null, {}, {}, {\"name\": \"e\"}]}");

        var loaded = SettingsStore.Load(path);

        Assert.That(loaded.Current, Is.EqualTo(new TrainerSettings(Factor: 2)));
        Assert.That(loaded.Current.ScaleAr, Is.True);
        Assert.That(loaded.Profiles, Is.EqualTo(new[]
        {
            new Profile("a", null), new Profile(null, null), new Profile(null, null), new Profile(null, null),
        }));
    }

    [Test]
    public void AppSettings_AlwaysHasFourProfiles()
    {
        Assert.That(new AppSettings(new TrainerSettings(), []).Profiles, Has.Length.EqualTo(4));
        var five = Enumerable.Range(0, 5).Select(i => new Profile($"p{i}", null)).ToArray();
        var s = new AppSettings(new TrainerSettings(), five);
        Assert.That(s.Profiles.Select(p => p.Name), Is.EqualTo(new[] { "p0", "p1", "p2", "p3" }));
        Assert.That((s with { Profiles = [] }).Profiles, Has.Length.EqualTo(4));
    }
}
