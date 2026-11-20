using Kakudai.Core.Export;
using Kakudai.Core.Library;
using NUnit.Framework;

namespace Kakudai.Tests;

public class SetFilesTests
{
    internal static BeatmapEntry Entry(string artist, string title, params string[] names) => new(
        Guid.NewGuid(), artist, artist, title, title, "Mapper", "Insane", "h", 3, 120, 1000,
        4, 9, 8, "audio.mp3", "bg.jpg", "test",
        names.Select(n => new NamedFile(n, "hash-" + n)).ToList(), null, DateTimeOffset.UtcNow);

    private const string Header = "osu file format v14\n\n[HitObjects]\n";

    [Test]
    public void ToInclude_KeepsAudioBgAndSamples()
    {
        var e = Entry("A", "T", "audio.mp3", "bg.jpg", "soft-hitclap2.wav", "normal-hitnormal.ogg",
            "other.mp3", "clip.avi", "x.osb", "y.osu");
        var r = SetFiles.ToInclude(e, Header + "256,192,1000,1,0,0:0:0:0:\n");
        Assert.That(r.Select(f => f.Filename),
            Is.EquivalentTo(new[] { "audio.mp3", "bg.jpg", "soft-hitclap2.wav", "normal-hitnormal.ogg" }));
    }

    [Test]
    public void ToInclude_KeepsReferencedCustomSample()
    {
        var e = Entry("A", "T", "audio.mp3", "boom.wav", "other.wav");
        var r = SetFiles.ToInclude(e, Header + "256,192,1000,1,0,0:0:0:0:boom.wav\n100,100,2000,2,0,L|200:100,1,100\n");
        Assert.That(r.Select(f => f.Filename), Is.EquivalentTo(new[] { "audio.mp3", "boom.wav" }));
    }

    [Test]
    public void ToInclude_MissingBackground_Skips()
    {
        var e = Entry("A", "T", "audio.mp3");
        var r = SetFiles.ToInclude(e, Header);
        Assert.That(r.Select(f => f.Filename), Is.EquivalentTo(new[] { "audio.mp3" }));
    }
}
