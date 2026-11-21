using Kakudai.Core.Game;
using NUnit.Framework;
using osu.Framework.Platform;

namespace Kakudai.Tests;

public class GameImportTests
{
    [Test]
    public void OpenPipes_FindsAnOsuFrameworkPipe()
    {
        var name = "kakudai-test-" + Guid.NewGuid().ToString("N")[..8];
        using var server = new NamedPipeIpcProvider(name);
        Assume.That(server.Bind(), Is.True);
        Assert.That(GameImport.OpenPipes(), Does.Contain(name));
    }
}
