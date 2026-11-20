using Kakudai.Core.Maps;
using NUnit.Framework;

namespace Kakudai.Tests;

public class StarsTests
{
    [Test]
    public void Stars_Simple_Positive()
    {
        var text = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "simple.osu"));
        var s = Stars.Calculate(text);
        Assert.That(s.Total, Is.GreaterThan(0));
    }
}
