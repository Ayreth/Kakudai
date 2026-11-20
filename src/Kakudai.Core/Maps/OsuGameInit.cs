using osu.Game.Beatmaps.Formats;
using osu.Game.Rulesets;

namespace Kakudai.Core.Maps;

/// <summary>Registers the osu.Game decoder dependencies once per process.</summary>
internal static class OsuGameInit
{
    private static readonly Lock gate = new();
    private static bool registered;

    public static void EnsureRegistered()
    {
        lock (gate)
        {
            if (registered)
                return;

            // In ppy.osu.Game 2026.1026.0 a repeated call does not throw (it only replaces the ruleset store),
            // so there is no "already registered" exception to swallow: the flag alone keeps this idempotent.
            Decoder.RegisterDependencies(new AssemblyRulesetStore());
            registered = true;
        }
    }
}
