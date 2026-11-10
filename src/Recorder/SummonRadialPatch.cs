using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Valheim.UI;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "Summon Commander: the next summon from a radial" (<see cref="SummonChoice"/>).
    /// A postfix on <c>Player.Update</c> sees the secondary attack pressed with the Dead Raiser or the
    /// Spirit Caller in hand and opens the game's radial menu the way its own radial key does
    /// (<c>Hud.m_config</c>, so the radial reads its settings and starts as usual); a prefix on
    /// <c>OpenRadialConfig.InitRadialConfig</c> then opens this patch's page in place of the game's
    /// first page. Each entry writes the pick to Summon Commander's settings and closes the radial
    /// without being kept as the radial's last used entry. Summon Commander's code is compared with
    /// 1.0.1's, and any change makes the patch step aside. The switch is this game's own.
    /// </summary>
    internal static class SummonRadialPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.summonradial";

        /// <summary>
        /// Summon Commander 1.0.1's SummonPatches.ChooseSpiritSpecies, Summons.GetSpiritPrefab and
        /// SkeletonLoadouts Apply, ChooseForTeam, GetTeamTarget, FillSlots, FindSlot, Matches, ToLoadout.
        /// </summary>
        private const ulong KnownCode = 0xCEDF38D0C6B195D1UL;

        private static readonly string[] Buttons = { "SecondaryAttack", "JoySecondaryAttack" };

        /// <summary>This game's switch, kept current by the config entry.</summary>
        public static bool On;

        public static bool Found { get; private set; }
        public static string Version { get; private set; }

        /// <summary>Why the patch stepped aside; null while it is in (or Summon Commander is absent).</summary>
        public static string Problem { get; private set; }

        /// <summary>Picks made this session.</summary>
        public static int Picked { get; private set; }

        public static string State => SummonChoice.StateLine(On, Found, Version, Problem) + (On && Found && Problem == null && Picked > 0 ? " · " + Picked + " picked this session" : "");

        private static Action<string> s_log;
        private static ConfigEntryBase s_nextSpirit, s_teamEnabled;
        private static ConfigEntryBase[] s_slots;
        private static Func<Player, bool> s_takesInput;
        private static MethodInfo s_setName, s_setSubTitle;
        private static SummonPage s_pending;
        private static int s_openFrame = -1;

        public static void Hook(Action<string> log)
        {
            s_log = log;
            var commander = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == SummonChoice.SummonCommanderGuid);
            Found = commander?.Instance != null;
            Version = commander?.Metadata.Version.ToString();
            if (!Found) return;
            try
            {
                var asm = commander.Instance.GetType().Assembly;
                var patches = asm.GetType("SummonCommander.SummonPatches");
                var summons = asm.GetType("SummonCommander.Summons");
                var loadouts = asm.GetType("SummonCommander.SkeletonLoadouts");
                var code = new MethodBase[]
                {
                    patches != null ? AccessTools.DeclaredMethod(patches, "ChooseSpiritSpecies") : null,
                    summons != null ? AccessTools.DeclaredMethod(summons, "GetSpiritPrefab") : null,
                    loadouts != null ? AccessTools.DeclaredMethod(loadouts, "Apply") : null,
                    loadouts != null ? AccessTools.DeclaredMethod(loadouts, "ChooseForTeam") : null,
                    loadouts != null ? AccessTools.DeclaredMethod(loadouts, "GetTeamTarget") : null,
                    loadouts != null ? AccessTools.DeclaredMethod(loadouts, "FillSlots") : null,
                    loadouts != null ? AccessTools.DeclaredMethod(loadouts, "FindSlot") : null,
                    loadouts != null ? AccessTools.DeclaredMethod(loadouts, "Matches") : null,
                    loadouts != null ? AccessTools.DeclaredMethod(loadouts, "ToLoadout") : null,
                };
                if (code.Any(m => m == null) || CodeShape.Hash(code) != KnownCode)
                {
                    Problem = "Summon Commander's next-summon code changed (an update)";
                    return;
                }
                var config = commander.Instance.Config;
                s_nextSpirit = Entry(config, SummonChoice.SpiritSection, SummonChoice.SpiritKey);
                s_teamEnabled = Entry(config, SummonChoice.TeamSection, SummonChoice.TeamEnabledKey);
                s_slots = Enumerable.Range(1, SummonChoice.SkeletonSlots)
                    .Select(i => Entry(config, SummonChoice.TeamSection, SummonChoice.SlotKeyPrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
                if (!Offers(s_nextSpirit, SummonStaff.SpiritCaller) || s_teamEnabled?.SettingType != typeof(bool) || s_slots.Any(s => !Offers(s, SummonStaff.DeadRaiser)))
                {
                    Problem = "Summon Commander's settings for the next summon were not found (an update changed them)";
                    return;
                }
                s_takesInput = AccessTools.MethodDelegate<Func<Player, bool>>(AccessTools.DeclaredMethod(typeof(Player), "TakeInput"));
                s_setName = AccessTools.DeclaredProperty(typeof(RadialMenuElement), nameof(RadialMenuElement.Name))?.GetSetMethod(true);
                s_setSubTitle = AccessTools.DeclaredProperty(typeof(RadialMenuElement), nameof(RadialMenuElement.SubTitle))?.GetSetMethod(true);
                var init = AccessTools.DeclaredMethod(typeof(OpenRadialConfig), nameof(OpenRadialConfig.InitRadialConfig));
                var update = AccessTools.DeclaredMethod(typeof(Player), "Update");
                if (s_takesInput == null || s_setName == null || s_setSubTitle == null || init == null || update == null)
                {
                    Problem = "the game's radial menu or player input changed (an update)";
                    return;
                }
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(init, prefix: new HarmonyMethod(typeof(SummonRadialPatch), nameof(BeforeFirstPage)));
                harmony.Patch(update, postfix: new HarmonyMethod(typeof(SummonRadialPatch), nameof(AfterPlayerUpdate)));
            }
            catch (Exception e) { Problem = "Summon Commander could not be hooked: " + e.GetType().Name + " " + e.Message; }
        }

        private static ConfigEntryBase Entry(ConfigFile config, string section, string key)
        {
            var definition = new ConfigDefinition(section, key);
            return config.ContainsKey(definition) ? config[definition] : null;
        }

        /// <summary>Whether a setting is an enum holding every value the staff's radial offers.</summary>
        private static bool Offers(ConfigEntryBase entry, SummonStaff staff) =>
            entry != null && entry.SettingType.IsEnum && SummonChoice.Options(staff).All(o => Enum.GetNames(entry.SettingType).Contains(o.Value));

        private static void StepAside(Exception e)
        {
            Problem = "its radial failed: " + e.GetType().Name + " " + e.Message;
            s_pending = null;
            s_log?.Invoke("Vigil: the Summon Commander radial patch stepped aside: " + Problem);
        }

        private static void AfterPlayerUpdate(Player __instance)
        {
            if (!On || Problem != null || !Found || __instance != Player.m_localPlayer) return;
            if (!Buttons.Any(ZInput.GetButtonDown)) return;
            try
            {
                var weapon = __instance.GetCurrentWeapon();
                var staff = SummonChoice.StaffOf(weapon?.m_dropPrefab != null ? weapon.m_dropPrefab.name : null);
                var open = new SummonOpen
                {
                    SwitchOn = On,
                    Ready = Problem == null && Hud.instance != null && Hud.instance.m_radialMenu != null && Hud.instance.m_config != null,
                    Staff = staff,
                    TakesInput = s_takesInput(__instance),
                    InRadial = Hud.InRadial(),
                    Building = __instance.InPlaceMode() || Hud.IsPieceSelectionVisible(),
                    Pressed = true,
                };
                if (!SummonChoice.ShouldOpen(open)) return;
                var radial = Hud.instance.m_radialMenu;
                s_pending = new SummonPage(staff, weapon);
                s_openFrame = Time.frameCount;
                radial.CanOpen = true;
                radial.Open(Hud.instance.m_config);
                // Open returns early in some build-mode states; the next radial key press then gets the game's page.
                s_pending = null;
            }
            catch (Exception e) { StepAside(e); }
        }

        /// <summary>Opens this patch's page in place of the game's first page when the summon press opened the radial.</summary>
        private static bool BeforeFirstPage(RadialBase radial)
        {
            var page = s_pending;
            if (page == null) return true;
            s_pending = null;
            try
            {
                radial.OnInteractionDelay = delay => PlayerController.SetTakeInputDelay(delay);
                radial.ShouldAnimateIn = true;
                radial.Open(page);
                return false;
            }
            catch (Exception e)
            {
                StepAside(e);
                return true;
            }
        }

        private static void Pick(SummonStaff staff, SummonOption option)
        {
            try
            {
                foreach (var write in SummonChoice.Picks(staff, option.Value))
                {
                    var entry = write.Section == SummonChoice.SpiritSection ? s_nextSpirit
                        : write.Key == SummonChoice.TeamEnabledKey ? s_teamEnabled
                        : s_slots[int.Parse(write.Key.Substring(SummonChoice.SlotKeyPrefix.Length), System.Globalization.CultureInfo.InvariantCulture) - 1];
                    entry.SetSerializedValue(write.Value);
                }
                Picked++;
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, SummonChoice.Message(staff, option));
            }
            catch (Exception e) { StepAside(e); }
        }

        private static string Text(ConfigEntryBase entry) => entry?.BoxedValue?.ToString();

        /// <summary>The radial's page for one staff.</summary>
        private sealed class SummonPage : IRadialConfig
        {
            private readonly SummonStaff _staff;
            private readonly ItemDrop.ItemData _item;

            public SummonPage(SummonStaff staff, ItemDrop.ItemData item)
            {
                _staff = staff;
                _item = item;
            }

            public string LocalizedName => Localization.instance != null && _item != null ? Localization.instance.Localize(_item.m_shared.m_name) : "";

            public Sprite Sprite => _item?.GetIcon();

            public void InitRadialConfig(RadialBase radial)
            {
                var options = SummonChoice.Options(_staff);
                int current = SummonChoice.Current(_staff, Text(s_nextSpirit), s_teamEnabled.BoxedValue is bool on && on, s_slots.Select(Text).ToArray());
                var elements = new List<RadialMenuElement>();
                for (int i = 0; i < options.Count; i++)
                {
                    var option = options[i];
                    var element = UnityEngine.Object.Instantiate(RadialData.SO.EmoteElement);
                    s_setName.Invoke(element, new object[] { option.Label });
                    s_setSubTitle.Invoke(element, new object[] { i == current ? "your next summon now" : "" });
                    var sprite = IconOf(option.Icon);
                    element.Icon.gameObject.SetActive(sprite != null);
                    element.Icon.sprite = sprite;
                    var staff = _staff;
                    // False keeps the entry out of the game's last used slot; the radial closes after it.
                    element.Interact = () =>
                    {
                        Pick(staff, option);
                        return false;
                    };
                    element.CloseOnInteract = () => true;
                    elements.Add(element);
                }
                FollowTheStaffButton(radial);
                radial.ConstructRadial(elements);
            }

            private static Sprite IconOf(string itemName)
            {
                var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(itemName) : null;
                var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                return drop != null ? drop.m_itemData.GetIcon() : null;
            }

            /// <summary>
            /// The radial's own key closes it and, with the game's release-to-use setting, picks on
            /// release after a hold; the staff's button does the same here. Its press that opened the
            /// radial is not a close.
            /// </summary>
            private static void FollowTheStaffButton(RadialBase radial)
            {
                var release = radial.GetReleaseToUse;
                var close = radial.GetClose;
                radial.GetReleaseToUse = () => (release?.Invoke() ?? false) || (RadialData.SO.EnableReleaseToUseMode && HeldAndReleased());
                radial.GetClose = () => (close?.Invoke() ?? false)
                    || (Time.frameCount != s_openFrame && Buttons.Any(ZInput.GetButtonDown))
                    || (!RadialData.SO.EnableReleaseToUseMode && HeldAndReleased());
            }

            private static bool HeldAndReleased() =>
                Buttons.Any(b => ZInput.GetButtonLastPressedTimer(b) > RadialData.SO.HoldCloseDelay && ZInput.GetButtonUp(b));
        }
    }
}
