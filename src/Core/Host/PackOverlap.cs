namespace Vigil.Core
{
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

        public static string StateLine(bool on, bool slotFound, string slotVersion, string problem)
        {
            if (!on) return "off: the server's switch (Patches.AdventureBackpacksBuffsOnce)";
            if (!slotFound) return "not needed: no backpack slot of Extra Slots Custom Slots";
            if (problem != null) return "stepped aside: " + problem;
            return "working · Extra Slots Custom Slots " + slotVersion + " · a backpack's buffs do not add to a worn cape's or trinket's";
        }
    }
}
