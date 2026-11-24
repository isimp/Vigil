namespace Vigil.Core
{
    /// <summary>
    /// The game's equipment modifier totals of one player measured three ways, in the order of
    /// <see cref="Fields"/>: with the pack's values set to nothing, as counted with the patch's switch
    /// off, and with it on; and the pack's own values.
    /// </summary>
    internal sealed class PackTotals
    {
        public readonly string[] Fields;
        public readonly float[] WithoutPack, Counted, Fixed, Pack;
        public readonly bool CapeWorn, TrinketWorn;

        public PackTotals(string[] fields, float[] withoutPack, float[] counted, float[] fixedTotals, float[] pack, bool capeWorn, bool trinketWorn)
        {
            Fields = fields;
            WithoutPack = withoutPack;
            Counted = counted;
            Fixed = fixedTotals;
            Pack = pack;
            CapeWorn = capeWorn;
            TrinketWorn = trinketWorn;
        }
    }

    /// <summary>
    /// The rule of the patch "Adventure Backpacks' buffs never stack". With Extra Slots Custom Slots an
    /// Adventure Backpacks pack sits in a slot of its own and the shoulder slot stays free for a cape,
    /// and that mod adds every equipment modifier of the pack to the game's totals, on top of the cape's.
    /// Some packs carry a cape's buffs (Cinderward: the Ashen cape's -20 % block and -10 % attack
    /// stamina; Frostbound: the Deep North cape's -20 % run and attack stamina), and the Frostbound's
    /// level 4 an adrenaline bar with its own full-bar effect, as a trinket has. So a buff from the pack
    /// is taken back while a cape is worn, and the pack's adrenaline while a trinket is worn; its
    /// penalties always count.
    /// </summary>
    internal static class PackOverlap
    {
        /// <summary>The game's equipment modifier for the size of the adrenaline bar.</summary>
        public const string AdrenalineField = "m_maxAdrenaline";

        /// <summary>
        /// What to take back from the game's total of one equipment modifier (named as the game's item
        /// field) for the pack's value of it. Stamina modifiers help below zero; movement and heat
        /// resistance help above zero. A modifier not known here is taken back whole under a cape, so a
        /// game update cannot bring a buff twice.
        /// </summary>
        public static float TakeBack(string field, float packValue, bool capeWorn, bool trinketWorn)
        {
            if (packValue == 0f) return 0f;
            if (field == AdrenalineField) return trinketWorn ? packValue : 0f;
            if (!capeWorn) return 0f;
            switch (field)
            {
                case "m_movementModifier":
                case "m_heatResistanceModifier":
                    return packValue > 0f ? packValue : 0f;
                case "m_homeItemsStaminaModifier":
                case "m_jumpStaminaModifier":
                case "m_attackStaminaModifier":
                case "m_blockStaminaModifier":
                case "m_dodgeStaminaModifier":
                case "m_swimStaminaModifier":
                case "m_sneakStaminaModifier":
                case "m_runStaminaModifier":
                    return packValue < 0f ? packValue : 0f;
                default:
                    return packValue;
            }
        }

        /// <summary>Whether the pack's own full-bar effect may fire when the adrenaline bar fills.</summary>
        public static bool PackEffectFires(bool trinketWorn) => !trinketWorn;

        private const float Near = 1e-4f;
        private const string TotalsName = "game patch: Adventure Backpacks' buffs never stack, measured";
        private const string EffectName = "game patch: Adventure Backpacks' buffs never stack, full-bar effect";

        /// <summary>
        /// The self-test's verdict on the game's equipment totals measured three ways: without the
        /// pack's values, as counted with the patch's switch off, and with it on. It passes only when
        /// the pack was counted on top and exactly <see cref="TakeBack"/> came back out.
        /// </summary>
        public static SelfTestReport.Check TotalsCheck(string area, PackTotals t)
        {
            if (t == null) return new SelfTestReport.Check(area, TotalsName, SelfTestReport.Result.Info, "not tested: no pack in Extra Slots' Backpack slot");
            if (!t.CapeWorn && !t.TrinketWorn)
                return new SelfTestReport.Check(area, TotalsName, SelfTestReport.Result.Info, "not tested: wear a cape or a trinket beside the pack");
            var notCounted = new System.Collections.Generic.List<string>();
            var wrong = new System.Collections.Generic.List<string>();
            var taken = new System.Collections.Generic.List<string>();
            for (int i = 0; i < t.Fields.Length; i++)
            {
                string field = t.Fields[i];
                if (System.Math.Abs(t.Counted[i] - (t.WithoutPack[i] + t.Pack[i])) > Near) notCounted.Add(Label(field));
                float back = TakeBack(field, t.Pack[i], t.CapeWorn, t.TrinketWorn);
                float expected = t.Counted[i] - back;
                if (System.Math.Abs(t.Fixed[i] - expected) > Near)
                    wrong.Add(Label(field) + " " + Amount(field, t.Fixed[i]) + " with the patch, " + Amount(field, expected) + " expected");
                if (back != 0f) taken.Add(Label(field) + " " + Amount(field, t.Counted[i]) + " -> " + Amount(field, t.Fixed[i]));
            }
            if (notCounted.Count > 0)
                return new SelfTestReport.Check(area, TotalsName, SelfTestReport.Result.Fail,
                    "the pack's " + string.Join(", ", notCounted.ToArray()) + " not counted on top by the slot, so the patch would take back what was never there");
            if (wrong.Count > 0) return new SelfTestReport.Check(area, TotalsName, SelfTestReport.Result.Fail, string.Join("; ", wrong.ToArray()));
            if (taken.Count == 0)
                return new SelfTestReport.Check(area, TotalsName, SelfTestReport.Result.Info, "not tested: this pack has no buff to hold back beside a cape or trinket");
            return new SelfTestReport.Check(area, TotalsName, SelfTestReport.Result.Pass,
                (t.CapeWorn ? (t.TrinketWorn ? "cape and trinket" : "cape") : "trinket") + " beside the pack: " + string.Join(", ", taken.ToArray()));
        }

        /// <summary>The self-test's verdict on hiding the pack's full-bar effect while a trinket is worn.</summary>
        public static SelfTestReport.Check EffectCheck(string area, bool packHasEffect, bool trinketWorn, bool hooked, bool hiddenDuring, bool backAfter)
        {
            if (!packHasEffect) return new SelfTestReport.Check(area, EffectName, SelfTestReport.Result.Info, "not tested: the pack has no full-bar effect (a level 4 Frostbound Haversack has one)");
            if (!trinketWorn) return new SelfTestReport.Check(area, EffectName, SelfTestReport.Result.Info, "not tested: wear a trinket beside the pack");
            if (!hooked) return new SelfTestReport.Check(area, EffectName, SelfTestReport.Result.Fail, "the game's adrenaline step does not carry the patch");
            if (!hiddenDuring) return new SelfTestReport.Check(area, EffectName, SelfTestReport.Result.Fail, "the pack's effect was not hidden while a trinket is worn");
            if (!backAfter) return new SelfTestReport.Check(area, EffectName, SelfTestReport.Result.Fail, "the pack's effect was not put back afterwards");
            return new SelfTestReport.Check(area, EffectName, SelfTestReport.Result.Pass, "only the trinket's effect fires when the bar fills; the pack's is put back after");
        }

        /// <summary>"m_blockStaminaModifier" as "block stamina".</summary>
        private static string Label(string field)
        {
            if (field == AdrenalineField) return "adrenaline bar";
            string name = field.StartsWith("m_", System.StringComparison.Ordinal) ? field.Substring(2) : field;
            if (name.EndsWith("Modifier", System.StringComparison.Ordinal)) name = name.Substring(0, name.Length - "Modifier".Length);
            var text = new System.Text.StringBuilder();
            foreach (char c in name)
            {
                if (char.IsUpper(c)) text.Append(' ').Append(char.ToLowerInvariant(c));
                else text.Append(c);
            }
            return text.ToString();
        }

        private static string Amount(string field, float value) => field == AdrenalineField
            ? value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
            : (value * 100f).ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture) + " %";

        public static string StateLine(bool on, bool slotFound, string slotVersion, string problem)
        {
            if (!on) return "off: the server's switch (Patches.AdventureBackpacksBuffsOnce)";
            if (!slotFound) return "not needed: no backpack slot of Extra Slots Custom Slots";
            if (problem != null) return "stepped aside: " + problem;
            return "working · Extra Slots Custom Slots " + slotVersion + " · a backpack's buffs do not add to a worn cape's or trinket's";
        }
    }
}
