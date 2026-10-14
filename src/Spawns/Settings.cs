using BepInEx.Configuration;
using Vigil.Core;
using UnityEngine;

namespace Vigil.Spawns;

/// <summary>The spawn marks' settings, in their own section of Vigil's config file.</summary>
internal static class Settings
{
    public static ConfigEntry<KeyCode> Key { get; private set; }

    public static ConfigEntry<float> Radius { get; private set; }

    public static ConfigEntry<float> Spacing { get; private set; }

    public static ConfigEntry<MarkStyle> Style { get; private set; }

    public static ConfigEntry<float> PillarHeight { get; private set; }

    public static ConfigEntry<Color> MarkColor { get; private set; }

    public static void Bind(ConfigFile config)
    {
        Key = config.Bind("Spawn marks", "Key", KeyCode.F6,
            "Turns the view of spawn gaps on and off. While it is on, every spot around you where a creature can spawn is marked on the ground.");
        Radius = config.Bind("Spawn marks", "Radius", 40f, new ConfigDescription(
            "How far around you spots are checked, in metres.",
            new AcceptableValueRange<float>(8f, 128f)));
        Spacing = config.Bind("Spawn marks", "Spacing", 2f, new ConfigDescription(
            "Distance between the spots checked, in metres. A smaller spacing shows the edges of base protection more finely and takes longer to check.",
            new AcceptableValueRange<float>(1f, 8f)));
        Style = config.Bind("Spawn marks", "MarkStyle", MarkStyle.Pillars,
            "How open spots are shown. Pillars stand straight up from each spot with a dot at the foot, so they stay easy to see on hills and in hollows. Dots lie flat on the ground.");
        PillarHeight = config.Bind("Spawn marks", "PillarHeight", 1.5f, new ConfigDescription(
            "Height of the pillars, in metres.",
            new AcceptableValueRange<float>(0.25f, 5f)));
        MarkColor = config.Bind("Spawn marks", "MarkColor", new Color(1f, 0.2f, 0.12f, 0.75f),
            "Colour of the marks on open spots.");
    }
}
