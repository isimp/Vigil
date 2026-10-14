using System;
using System.Collections.Generic;
using System.Globalization;

namespace Vigil.Core;

/// <summary>One kind of creature a spawn list can place, and whether it may appear inside bases.</summary>
internal sealed class SpawnEntry
{
    public SpawnEntry(string source, string creature, bool enabled, bool insideBases)
    {
        Source = source;
        Creature = creature;
        Enabled = enabled;
        InsideBases = insideBases;
    }

    /// <summary>Where the entry comes from: "world" for the biome spawns, "raid name" for a raid.</summary>
    public string Source { get; }

    public string Creature { get; }

    public bool Enabled { get; }

    public bool InsideBases { get; }
}

/// <summary>What the player is told when a check finishes.</summary>
internal static class Readout
{
    public static string Summary(int open, float radius, int ignoringBases)
    {
        var ci = CultureInfo.InvariantCulture;
        string reach = Math.Round(radius).ToString("0", ci) + " m";
        string text = open == 0
            ? "No spawn gaps within " + reach
            : string.Format(ci, "Spawn gaps: {0} open {1} within {2}", open, open == 1 ? "spot" : "spots", reach);
        if (ignoringBases > 0)
        {
            text += string.Format(ci, ". {0} {1} bases, see the log", ignoringBases, ignoringBases == 1 ? "spawn ignores" : "spawns ignore");
        }

        return text;
    }

    /// <summary>
    /// The enabled spawns that may appear inside bases, as "source: creature", each once, in the
    /// order they were found. The marks cannot show safety from these.
    /// </summary>
    public static List<string> IgnoringBases(IEnumerable<SpawnEntry> entries)
    {
        var named = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries ?? Array.Empty<SpawnEntry>())
        {
            if (entry == null || !entry.Enabled || !entry.InsideBases)
            {
                continue;
            }

            string line = entry.Source + ": " + entry.Creature;
            if (seen.Add(line))
            {
                named.Add(line);
            }
        }

        return named;
    }
}
