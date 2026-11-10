using System;
using System.Collections.Generic;

namespace Vigil.Core
{
    /// <summary>A summon staff whose next summon can be chosen.</summary>
    internal enum SummonStaff
    {
        None,
        DeadRaiser,
        SpiritCaller,
    }

    /// <summary>What the next summon can be: Summon Commander's setting value, its words and the item whose icon shows it.</summary>
    internal readonly struct SummonOption
    {
        public readonly string Value, Label, Icon;

        public SummonOption(string value, string label, string icon)
        {
            Value = value;
            Label = label;
            Icon = icon;
        }
    }

    /// <summary>One of Summon Commander's settings and the value a pick gives it.</summary>
    internal readonly struct SettingWrite
    {
        public readonly string Section, Key, Value;

        public SettingWrite(string section, string key, string value)
        {
            Section = section;
            Key = key;
            Value = value;
        }
    }

    /// <summary>What decides whether a press opens the summon radial.</summary>
    internal struct SummonOpen
    {
        public bool SwitchOn, Ready, TakesInput, InRadial, Building, Pressed;
        public SummonStaff Staff;
    }

    /// <summary>
    /// The game patch "Summon Commander: the next summon from a radial". No staff in the game has a
    /// secondary attack, so with the Dead Raiser or the Spirit Caller in hand that press opens the
    /// game's radial menu with what the next summon can be. Summon Commander (1.0.1) decides what
    /// the staffs summon from its own settings: <c>Summon.NextSpirit</c> for the Spirit Caller's
    /// species, and the team slots <c>SkeletonTeam.Slot1</c> to <c>Slot4</c>, from which a new
    /// skeleton gets the weapon the team is missing. A pick sets the species, or every slot to the
    /// weapon, so the next skeleton always has it.
    /// </summary>
    internal static class SummonChoice
    {
        public const string SummonCommanderGuid = "xmordax.valheim.summoncommander";
        public const string DeadRaiserPrefab = "StaffSkeleton";
        public const string SpiritCallerPrefab = "StaffSpiritCaller";
        public const string RandomValue = "Random";

        /// <summary>Summon Commander's skeleton team slots; a Dead Raiser summons at most 4 (its highest quality).</summary>
        public const int SkeletonSlots = 4;

        public const string SpiritSection = "Summon", SpiritKey = "NextSpirit";
        public const string TeamSection = "SkeletonTeam", TeamEnabledKey = "Enabled", SlotKeyPrefix = "Slot";

        private static readonly SummonOption[] DeadRaiserOptions =
        {
            new SummonOption("SwordAndShield", "Sword and shield", "ShieldWood"),
            new SummonOption("Bow", "Bow", "Bow"),
            new SummonOption(RandomValue, "Any weapon", "StaffSkeleton"),
        };

        private static readonly SummonOption[] SpiritCallerOptions =
        {
            new SummonOption("Wolf", "Wolf", "TrophyWolf"),
            new SummonOption("Boar", "Boar", "TrophyBoar"),
            new SummonOption("Moose", "Moose", "TrophyMoose"),
            new SummonOption("Bear", "Bear", "TrophyBjorn"),
            new SummonOption(RandomValue, "Any species", "StaffSpiritCaller"),
        };

        /// <summary>The staff an item is, by its prefab name.</summary>
        public static SummonStaff StaffOf(string prefabName)
        {
            if (string.Equals(prefabName, DeadRaiserPrefab, StringComparison.Ordinal)) return SummonStaff.DeadRaiser;
            if (string.Equals(prefabName, SpiritCallerPrefab, StringComparison.Ordinal)) return SummonStaff.SpiritCaller;
            return SummonStaff.None;
        }

        /// <summary>The radial's entries for a staff, in their order on the radial.</summary>
        public static IReadOnlyList<SummonOption> Options(SummonStaff staff)
        {
            switch (staff)
            {
                case SummonStaff.DeadRaiser: return DeadRaiserOptions;
                case SummonStaff.SpiritCaller: return SpiritCallerOptions;
                default: return Array.Empty<SummonOption>();
            }
        }

        /// <summary>
        /// The entry that is the next summon now, read from Summon Commander's settings; -1 when none
        /// is: a team of mixed weapons, or a value the radial does not offer.
        /// </summary>
        public static int Current(SummonStaff staff, string nextSpirit, bool teamEnabled, IReadOnlyList<string> slots)
        {
            var options = Options(staff);
            if (staff == SummonStaff.SpiritCaller) return IndexOf(options, nextSpirit);
            if (staff != SummonStaff.DeadRaiser) return -1;
            if (!teamEnabled) return IndexOf(options, RandomValue);
            if (slots == null || slots.Count == 0) return -1;
            for (int i = 1; i < slots.Count; i++)
                if (!string.Equals(slots[i], slots[0], StringComparison.Ordinal)) return -1;
            return IndexOf(options, slots[0]);
        }

        /// <summary>The settings a pick writes; none for a value the staff does not offer.</summary>
        public static IReadOnlyList<SettingWrite> Picks(SummonStaff staff, string value)
        {
            if (IndexOf(Options(staff), value) < 0) return Array.Empty<SettingWrite>();
            if (staff == SummonStaff.SpiritCaller) return new[] { new SettingWrite(SpiritSection, SpiritKey, value) };
            var writes = new List<SettingWrite> { new SettingWrite(TeamSection, TeamEnabledKey, "true") };
            for (int i = 1; i <= SkeletonSlots; i++) writes.Add(new SettingWrite(TeamSection, SlotKeyPrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture), value));
            return writes;
        }

        /// <summary>Whether this press opens the radial.</summary>
        public static bool ShouldOpen(SummonOpen o) =>
            o.SwitchOn && o.Ready && o.Staff != SummonStaff.None && o.TakesInput && !o.InRadial && !o.Building && o.Pressed;

        /// <summary>The line shown after a pick.</summary>
        public static string Message(SummonStaff staff, SummonOption picked) =>
            (staff == SummonStaff.DeadRaiser ? "Next skeleton: " : "Next spirit: ")
            + (string.Equals(picked.Value, RandomValue, StringComparison.Ordinal) ? picked.Label.ToLowerInvariant() : picked.Label);

        /// <summary>What the patch does on this game, in words.</summary>
        public static string StateLine(bool on, bool found, string version, string problem)
        {
            if (!on) return "off: this game's switch (Patches.SummonCommanderRadial)";
            if (!found) return "not needed: Summon Commander is not installed";
            if (problem != null) return "stepped aside: " + problem;
            return "working · Summon Commander " + version + " · the secondary attack of the Dead Raiser and the Spirit Caller";
        }

        private static int IndexOf(IReadOnlyList<SummonOption> options, string value)
        {
            for (int i = 0; i < options.Count; i++)
                if (string.Equals(options[i].Value, value, StringComparison.Ordinal)) return i;
            return -1;
        }
    }
}
