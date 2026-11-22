using System;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "Adventure Backpacks' buffs never stack" (<see cref="PackOverlap"/>). Extra Slots
    /// Custom Slots 1.0.23 gives an Adventure Backpacks pack a slot of its own and, in a postfix on
    /// <c>Player.UpdateModifiers</c> (run on every stats update), adds every equipment modifier of the
    /// pack to the game's totals, which already hold the cape in the shoulder slot and the trinket.
    /// A later postfix takes back what
    /// <see cref="PackOverlap.TakeBack"/> names, and a prefix with a finalizer on
    /// <c>Player.AddAdrenaline</c> hides the pack's full-bar effect while a trinket is worn, so only the
    /// trinket's fires. Extra Slots Custom Slots' code is compared with 1.0.23's, and any change makes
    /// the patch step aside. The switch is the server's, passed on by ServerSync.
    /// </summary>
    internal static class PackOverlapPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.packoverlap";
        private const string SlotsGuid = "shudnal.ExtraSlotsCustomSlots";
        private const string PacksGuid = "vapok.mods.adventurebackpacks";

        /// <summary>Extra Slots Custom Slots 1.0.23: its UpdateModifiers postfix for the backpack slot and GetAdventureBackpack.</summary>
        private const ulong KnownCode = 0x4CB9127A0DAC2698UL;

        /// <summary>The server's switch, kept current by the config entry.</summary>
        public static bool On;

        public static bool Found { get; private set; }
        public static string SlotsVersion { get; private set; }

        /// <summary>Why the patch stepped aside; null while it is in (or the slot is absent).</summary>
        public static string Problem { get; private set; }

        public static string State => PackOverlap.StateLine(On, Found, SlotsVersion, Problem);

        private static readonly AccessTools.FieldRef<Player, float[]> s_values = AccessTools.FieldRefAccess<Player, float[]>("m_equipmentModifierValues");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> s_shoulder = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_shoulderItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> s_trinket = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_trinketItem");
        private static FieldInfo s_sources;
        private static Func<Humanoid, ItemDrop.ItemData> s_pack;
        private static Func<bool> s_slotActive;

        /// <summary>The pack whose full-bar effect is hidden for the length of one AddAdrenaline.</summary>
        private static ItemDrop.ItemData.SharedData s_hiddenFrom;
        private static StatusEffect s_hidden;

        public static void Hook(Action<string> log)
        {
            var slots = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == SlotsGuid);
            Found = slots?.Instance != null && Chainloader.PluginInfos.ContainsKey(PacksGuid);
            SlotsVersion = slots?.Metadata.Version.ToString();
            if (!Found) return;
            try
            {
                var asm = slots.Instance.GetType().Assembly;
                var ext = asm.GetType("ExtraSlotsCustomSlots.AdventureBackpacksCustomSlot.HumanoidExtension");
                var items = asm.GetType("ExtraSlotsCustomSlots.AdventureBackpacksCustomSlot.CustomItemType");
                var slot = asm.GetType("ExtraSlotsCustomSlots.AdventureBackpacksSlot");
                var get = ext != null ? AccessTools.DeclaredMethod(ext, "GetAdventureBackpack", new[] { typeof(Humanoid) }) : null;
                var adds = items?.GetNestedType("Player_UpdateModifiers_AdventureBackpack", BindingFlags.NonPublic | BindingFlags.Public);
                var postfix = adds != null ? AccessTools.DeclaredMethod(adds, "Postfix") : null;
                var active = slot != null ? AccessTools.PropertyGetter(slot, "IsActive") : null;
                if (get == null || postfix == null || active == null || CodeShape.Hash(new MethodBase[] { postfix, get }) != KnownCode)
                {
                    Problem = "its backpack slot code changed (an update)";
                    return;
                }
                s_sources = AccessTools.Field(typeof(Player), "s_equipmentModifierSourceFields");
                var update = AccessTools.DeclaredMethod(typeof(Player), "UpdateModifiers");
                var addAdrenaline = AccessTools.DeclaredMethod(typeof(Player), nameof(Player.AddAdrenaline));
                if (s_sources == null || update == null || addAdrenaline == null)
                {
                    Problem = "the game's equipment modifiers were not found (a game update changed them)";
                    return;
                }
                s_pack = (Func<Humanoid, ItemDrop.ItemData>)Delegate.CreateDelegate(typeof(Func<Humanoid, ItemDrop.ItemData>), get);
                s_slotActive = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), active);
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(update, postfix: new HarmonyMethod(typeof(PackOverlapPatch), nameof(AfterUpdateModifiers)) { priority = Priority.Last, after = new[] { SlotsGuid } });
                harmony.Patch(addAdrenaline, prefix: new HarmonyMethod(typeof(PackOverlapPatch), nameof(BeforeAddAdrenaline)),
                    finalizer: new HarmonyMethod(typeof(PackOverlapPatch), nameof(AfterAddAdrenaline)));
                log?.Invoke("Vigil: the patch keeping Adventure Backpacks' buffs from stacking is hooked into Extra Slots Custom Slots " + SlotsVersion + ".");
            }
            catch (Exception e) { Problem = "Extra Slots Custom Slots could not be hooked: " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>The pack in Extra Slots Custom Slots' own slot, or null (none, the slot off, or the pack in the shoulder slot).</summary>
        private static ItemDrop.ItemData SlotPack(Player player)
        {
            if (!s_slotActive()) return null;
            var pack = s_pack(player);
            return pack != null && pack != s_shoulder(player) ? pack : null;
        }

        private static void AfterUpdateModifiers(Player __instance)
        {
            if (!On || Problem != null) return;
            try
            {
                var pack = SlotPack(__instance);
                var fields = s_sources.GetValue(null) as FieldInfo[];
                var values = s_values(__instance);
                if (pack == null || fields == null || values == null) return;
                bool cape = s_shoulder(__instance) != null;
                bool trinket = s_trinket(__instance) != null;
                for (int i = 0; i < values.Length && i < fields.Length; i++)
                {
                    if (fields[i].GetValue(pack.m_shared) is float value)
                        values[i] -= PackOverlap.TakeBack(fields[i].Name, value, cape, trinket);
                }
            }
            catch (Exception e) { Problem = "it failed on this game: " + e.GetType().Name + " " + e.Message; }
        }

        private static void BeforeAddAdrenaline(Player __instance)
        {
            s_hiddenFrom = null;
            s_hidden = null;
            if (!On || Problem != null) return;
            var pack = SlotPack(__instance);
            var effect = pack?.m_shared.m_fullAdrenalineSE;
            if (effect == null || PackOverlap.PackEffectFires(s_trinket(__instance) != null)) return;
            s_hiddenFrom = pack.m_shared;
            s_hidden = effect;
            pack.m_shared.m_fullAdrenalineSE = null;
        }

        /// <summary>A finalizer that returns nothing lets any exception go on as before; the effect is always put back.</summary>
        private static void AfterAddAdrenaline()
        {
            if (s_hiddenFrom != null && s_hiddenFrom.m_fullAdrenalineSE == null) s_hiddenFrom.m_fullAdrenalineSE = s_hidden;
            s_hiddenFrom = null;
            s_hidden = null;
        }
    }
}
