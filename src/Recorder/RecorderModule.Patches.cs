using BepInEx.Configuration;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The panel's Patches tab: Vigil's own game patches, each switched by the server for every player
    /// running Vigil (a config entry ServerSync passes on, off unless set) and by its admins in the
    /// panel; everyone sees what each does and whether it works on their game.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private static readonly string[] OnOffWords = { "On", "Off" };
        private ConfigEntry<bool> _odinVolume, _skipWinter, _joinPackages, _jotunnSync, _chestScroll, _containerFit, _relocateFixes, _relocateSpirit, _buildMenu, _buildNavLater, _comfortBadges, _comfortPlates, _compassPins, _vehiclePins, _compassFollow, _compassMap, _awaySleep, _wardNetwork, _wardAuto, _wardPasswordSet;
        private const string CompassPinsNote = "Every frame HUDCompass puts the map's pins on its compass and, to find each pin's icon, writes the pin's position out as text, looks the icon up by that name and crosses the name off a list, for every pin of a shown type on the whole map, near or far, so each pin on the map costs more the more pins there are. A pin that moves gets a new icon each time. On, Vigil does that frame's work by HUDCompass's own rules: each icon stays with its position without any text, and an icon whose pin is gone is kept aside for the next new one. The compass looks the same. A HUDCompass whose compass code differs from 1.2.1 is left alone.";
        private const string VehiclePinsNote = "A player's game hears only of objects near it and keeps a far one at the spot it last saw, and HUDCompass pins the carts, ships and portals it knows from the server's list at joining and from the game after that, so a far one moved or built later was missing or in its old place. On, the server's Vigil keeps every cart, ship and portal of the world and tells each player the whole list on joining and every 5 s what was added, moved, renamed or removed; the player's Vigil adds and removes HUDCompass's pins by it and draws a far one at the server's place, a loaded one where the game has it. A changed portal is also sent whole, so every mod reading the game's copy (TargetPortal's list and where it teleports to) has its new place and name. Destroyed ones go everywhere at once. Needs Vigil on the server and HUDCompass 1.2.1 on the player's game; a HUDCompass whose map pin code differs is left alone.";
        private const string CompassFollowNote = "HUDCompass's compass draws every map pin in range in one colour, also those Cartur's Map Pins hides on the map. On, and with HudCompassPins on, the compass leaves out the pins Cartur hides (its list of hidden names and its hide key) and gives pins the colour Cartur gives them on the map: the colour set in its editor, else its ore or berry tint, a looted chest at half strength. Hiding by zoom, by distance and Cartur's map search stay on the map only. A change to Cartur's colouring code makes it step aside.";
        private const string CompassMapNote = "Cartur's Map Pins fades every map pin whose name does not hold the text typed into its search on the large map and hides its label, and its hide key steps through all pins shown, all hidden and auto pins hidden. HUDCompass draws its cart, ship and portal pins itself, so both passed them by. On, those pins follow the same rules: while the hide key hides all pins or auto pins they are hidden (they are made by a mod, not by hand), and while a search runs one whose name (a portal's tag, a cart's or ship's name or type) does not match is faded and loses its label. Needs both mods; a change to either mod's code makes it step aside.";
        private const string CompassPinsFollowNote = "The game redraws its map pins only when something asks for it (walking, dragging or zooming the map, a pin added or removed), and HUDCompass places its cart, ship and portal pins only then; it writes a pin's name and colour only when it makes the pin and makes it again only after the pin was hidden. So a portal moved, renamed or linked while you stand near kept its old place, name and colour until the pins were hidden once, as entering a portal with TargetPortal does. On, a quarter-second look compares each pin with its portal, cart or ship (the server's place for a far one while VehiclePinsFromServer is in use) and asks for a redraw when one differs, and a pin whose name or colour is out of date is made again. A HUDCompass whose map pin code differs from 1.2.1 is left alone.";
        private const string TargetPortalFollowNote = "TargetPortal names its portal pins once, when it makes them, so a renamed portal kept its old name on the map and in the favourites list, and it keeps favourites by the place a portal had when it was marked, so a moved portal lost its favourite. On, each time TargetPortal updates its pins a pin takes its portal's current tag, a favourite moves with its portal, and the favourites list is built again. On a server, a portal's new place and name reach a far player's game only with VehiclePinsFromServer on there. A TargetPortal whose pin code differs from 1.2.7 is left alone.";
        private ConfigEntry<bool> _compassFollowPins, _targetPortalFollow;
        private ConfigEntry<int> _awayMinutes, _wardSeconds;
        private ConfigEntry<string> _wardPassword;
        private string _awayMinutesText = "5", _wardSecondsText = "10";
        private bool _patchesHooked;

        private void BindPatches()
        {
            _odinVolume = Config.Bind("Patches", "OdinOnDemandVolume", false,
                "Set on the dedicated server: OdinOnDemand's radios, screens and girdle follow the game's master and music volume from the first join, OdinOnDemand's " +
                "master volume slider is the game's music slider, and AzuMiscPatches' mute in background gives the sound back at 100 % instead of at the master volume, " +
                "so the game's own sounds keep their level. OdinOnDemand's own master volumes stay in its config for when the patch is off. ServerSync passes it to every " +
                "player running Vigil; the server's admins can switch it in the panel's Patches tab, and the server keeps the change.");
            _host.Sync.AddConfigEntry(_odinVolume);
            OdinVolumePatch.On = _odinVolume.Value;
            _odinVolume.SettingChanged += (_, __) => OdinVolumePatch.On = _odinVolume.Value;
            _skipWinter = Config.Bind("Patches", "SkipWinter", false,
                "Set on the dedicated server: Seasonality's seasons go spring, summer, fall, spring; when the next season would be winter, it is spring. A season set by hand is left alone. " +
                "It takes effect where the season changes (the server, or a game hosting its world); ServerSync passes the switch to every player running Vigil, and the server's admins can switch it in the panel's Patches tab.");
            _host.Sync.AddConfigEntry(_skipWinter);
            SkipWinterPatch.On = _skipWinter.Value;
            _skipWinter.SettingChanged += (_, __) => SkipWinterPatch.On = _skipWinter.Value;
            _joinPackages = Config.Bind("Patches", "JoinPackages", false,
                "Set on the dedicated server: Drop That and Spawn That build the config packages they send each joining player from scratch, on the server's main loop (0.7 to 0.9 s each, everyone else standing still meanwhile). " +
                "On, the server builds them once and every later join gets the same bytes; a dedicated server builds them a minute after its world is up, while nobody is connected. " +
                "They are built again after dropthat:reload or when the world's location count changes. It takes effect where players join (the server, or a game hosting its world); " +
                "ServerSync passes the switch to every player running Vigil, and the server's admins can switch it in the panel's Patches tab.");
            _host.Sync.AddConfigEntry(_joinPackages);
            JoinPackagesPatch.Enabled = _joinPackages.Value;
            _joinPackages.SettingChanged += (_, __) => JoinPackagesPatch.Enabled = _joinPackages.Value;
            // This game's own switch, not the server's: Jotunn's answers stay the same, only quicker.
            _jotunnSync = Config.Bind("Patches", "JotunnConfigSync", false,
                "This game's own switch: when the server's synced settings arrive on joining, Jotunn checks each one with the file's list of keys, which BepInEx copies whole for every " +
                "setting (seconds for thousands of settings). On, Jotunn gets one copy of each file's keys per sync, which answers every check the same way; the first answers are asked " +
                "both ways, and Jotunn's own way comes back at a disagreement.");
            JotunnSyncPatch.On = _jotunnSync.Value;
            _jotunnSync.SettingChanged += (_, __) => JotunnSyncPatch.On = _jotunnSync.Value;
            _chestScroll = Config.Bind("Patches", "ChestGridScroll", false,
                "This game's own switch: with CycleChests, the mouse wheel switches chests whenever a chest is open, so a chest whose slots are taller than its view cannot be scrolled. " +
                "On, the wheel over that chest's grid or its scroll bar scrolls it the game's way; anywhere else it cycles chests as before.");
            ChestScrollPatch.On = _chestScroll.Value;
            _chestScroll.SettingChanged += (_, __) => ChestScrollPatch.On = _chestScroll.Value;
            _containerFit = Config.Bind("Patches", "WideContainerFit", false,
                "This game's own switch: the game centres a container's slots in the chest frame and scrolls only up and down, so a container wider than the frame " +
                "(such as a tombstone ExtraSlots widened for its equipped items) loses both sides. On, such a container is shrunk evenly until every column fits; " +
                "one that fits keeps its size. Only the drawing changes, never the items or their slots.");
            ContainerFitPatch.On = _containerFit.Value;
            _containerFit.SettingChanged += (_, __) => ContainerFitPatch.On = _containerFit.Value;
            _relocateFixes = Config.Bind("Patches", "EasyRelocateFixes", false,
                "This game's own switch: EasyRelocate's first grab of a session searched every mod for two mods that are not installed (about a second), every grab and every end " +
                "of a move rebuilt six game methods (about 50 ms each way), and while a piece moved its ghost was updated twice a frame. On, the search is skipped, the hooks go in once " +
                "at the main menu and stay, and the ghost is updated once a frame. A move also always ends: the grab key works while other keys are held, Esc ends it, a text box taking " +
                "the keys ends it, taking a hammer, hoe or cultivator in hand ends it, and an error in EasyRelocate ends it as a relog would. With one of those tools in hand " +
                "no piece is grabbed.");
            RelocatePatch.FixesOn = _relocateFixes.Value;
            _relocateFixes.SettingChanged += (_, __) => RelocatePatch.FixesOn = _relocateFixes.Value;
            _relocateSpirit = Config.Bind("Patches", "EasyRelocateSpiritGhost", false,
                "This game's own switch: the piece EasyRelocate moves is drawn as a see-through spirit, " +
                "pale blue where it can be placed and pale red where not, breathing slowly. Off, it is the solid copy EasyRelocate draws. It changes the next ghost.");
            RelocatePatch.SpiritOn = _relocateSpirit.Value;
            _relocateSpirit.SettingChanged += (_, __) => RelocatePatch.SpiritOn = _relocateSpirit.Value;
            _buildMenu = Config.Bind("Patches", "BuildMenuFaster", false,
                "This game's own switch: opening the build menu rebuilds one button per piece, and sets up the menu's arrow-key and controller navigation while it is still hidden, where that only forces every canvas to update. " +
                "On, when the menu would show the same pieces in the same order, its buttons stay and only their material colours and favourite stars are refreshed, and the navigation is set up once the menu is shown. Nothing in the menu changes.");
            BuildMenuPatch.On = _buildMenu.Value;
            _buildMenu.SettingChanged += (_, __) => BuildMenuPatch.On = _buildMenu.Value;
            _buildNavLater = Config.Bind("Patches", "BuildMenuNavigationLater", false, "This game's own switch: " + "The build menu links its buttons for the arrow keys and a controller after every open and every change of the search text, and before linking it forces every canvas to update twice and lays the whole button grid out again. On, the linking waits for the next frame, when the screen layout has placed the buttons anyway, and follows the game's rules without forcing anything. For that one frame the arrow keys and a controller still follow the links of before; the mouse is not affected.");
            BuildMenuPatch.NavigationLaterOn = _buildNavLater.Value;
            _buildNavLater.SettingChanged += (_, __) => BuildMenuPatch.NavigationLaterOn = _buildNavLater.Value;
            _comfortBadges = Config.Bind("Patches", "ComfortMeBadges", false,
                "This game's own switch: ComfortMe redraws the badge on every piece button of the build menu on every frame the menu is open. " +
                "On, it redraws them only when what they show can have changed: a new comfort snapshot (ComfortMe makes one at least every 0.2 s, and when you move), another player, the menu opening or closing, its buttons rebuilt, or one of ComfortMe's settings changed.");
            ComfortBadgesPatch.On = _comfortBadges.Value;
            _comfortBadges.SettingChanged += (_, __) => ComfortBadgesPatch.On = _comfortBadges.Value;
            _comfortPlates = Config.Bind("Patches", "ComfortMeHighlight", false, "This game's own switch: " + "Outside its comfort tab ComfortMe switches off the hover highlight on every piece button of the build menu on every frame, hammer out or not, looking each button's highlight up again each time. On, it does that only after a highlight was lit or the menu's buttons were rebuilt; with nothing lit there is nothing to switch off. The highlight looks the same.");
            ComfortPlatesPatch.On = _comfortPlates.Value;
            _comfortPlates.SettingChanged += (_, __) => ComfortPlatesPatch.On = _comfortPlates.Value;
            _compassPins = Config.Bind("Patches", "HudCompassPins", false, "This game's own switch: " + CompassPinsNote);
            HudCompassPatch.On = _compassPins.Value;
            _compassPins.SettingChanged += (_, __) => HudCompassPatch.On = _compassPins.Value;
            _vehiclePins = Config.Bind("Patches", "VehiclePinsFromServer", false,
                "Set on the dedicated server: " + VehiclePinsNote + " ServerSync passes it to every player running Vigil; the server's admins can switch it in the panel's Patches tab.");
            _host.Sync.AddConfigEntry(_vehiclePins);
            VehiclePinsPatch.On = _vehiclePins.Value;
            _vehiclePins.SettingChanged += (_, __) => VehiclePinsPatch.On = _vehiclePins.Value;
            _compassFollow = Config.Bind("Patches", "HudCompassFollowsCartur", false, "This game's own switch: " + CompassFollowNote);
            HudCompassPatch.FollowCartur = _compassFollow.Value;
            _compassFollow.SettingChanged += (_, __) => HudCompassPatch.FollowCartur = _compassFollow.Value;
            _compassMap = Config.Bind("Patches", "HudCompassCarturMap", false, "This game's own switch: " + CompassMapNote);
            CarturMapPatch.On = _compassMap.Value;
            _compassMap.SettingChanged += (_, __) => CarturMapPatch.On = _compassMap.Value;
            _compassFollowPins = Config.Bind("Patches", "HudCompassPinsFollow", false, "This game's own switch: " + CompassPinsFollowNote);
            CompassFollowPatch.On = _compassFollowPins.Value;
            _compassFollowPins.SettingChanged += (_, __) => CompassFollowPatch.On = _compassFollowPins.Value;
            _targetPortalFollow = Config.Bind("Patches", "TargetPortalPinsFollow", false, "This game's own switch: " + TargetPortalFollowNote);
            TargetPortalPatch.On = _targetPortalFollow.Value;
            _targetPortalFollow.SettingChanged += (_, __) => TargetPortalPatch.On = _targetPortalFollow.Value;
            _awaySleep = Config.Bind("Patches", "AwaySleep", false,
                "Set on the dedicated server: a player whose game menu has stayed open for AwayMinutes is away, and the night is skipped once everyone else is in bed; somebody has to be in bed. " +
                "An away player out of bed gets no morning (no Rested, no good morning), and the players in bed are told who was away and not counted. Moving the mouse in the menu does not count; closing it does. " +
                "It takes effect on the server and needs Vigil on the players' games to hear who is away; ServerSync passes the switch to every player running Vigil, and the server's admins can switch it in the panel's Patches tab.");
            _host.Sync.AddConfigEntry(_awaySleep);
            _awayMinutes = Config.Bind("Patches", "AwayMinutes", 5,
                new ConfigDescription("Set on the dedicated server: the minutes a player's game menu stays open before they are away for sleeping (AwaySleep). ServerSync passes it to every player running Vigil.",
                    new AcceptableValueRange<int>(AwaySleep.MinMinutes, AwaySleep.MaxMinutes)));
            _host.Sync.AddConfigEntry(_awayMinutes);
            ApplyAway();
            _awaySleep.SettingChanged += (_, __) => ApplyAway();
            _awayMinutes.SettingChanged += (_, __) => ApplyAway();
            BindWardPatches();
        }

        private void BindWardPatches()
        {
            _wardNetwork = Config.Bind("Patches", "WardNetworkRepair", false,
                "Set on the dedicated server: starting repair at a Protective Wards ward (its alt-use) starts it on every active ward chained to it by overlapping areas, and each ward repairs " +
                "its own area, one damaged piece every WardRepairSeconds, until none is left; every player sees how many pieces are left. A ward's repair runs on the game that owns it, so it " +
                "goes on while any player is near. ServerSync passes it to every player running Vigil; the server's admins can switch it in the panel's Patches tab.");
            _host.Sync.AddConfigEntry(_wardNetwork);
            _wardAuto = Config.Bind("Patches", "WardAutoRepair", false,
                "Set on the dedicated server: every active Protective Wards ward repairs its own area by itself, one damaged piece every WardRepairSeconds, following Protective Wards' own " +
                "repair rules (structures not placed by players, crafting station near). It runs on the game that owns the ward, so only while a player is near the base. ServerSync passes it " +
                "to every player running Vigil; the server's admins can switch it in the panel's Patches tab.");
            _host.Sync.AddConfigEntry(_wardAuto);
            _wardSeconds = Config.Bind("Patches", "WardRepairSeconds", WardRepair.DefaultSeconds,
                new ConfigDescription("Set on the dedicated server: seconds between two pieces a ward repairs (WardNetworkRepair, WardAutoRepair). ServerSync passes it to every player running Vigil.",
                    new AcceptableValueRange<int>(WardRepair.MinSeconds, WardRepair.MaxSeconds)));
            _host.Sync.AddConfigEntry(_wardSeconds);
            // Never synced: the players' games are told only that there is a password (WardPasswordSet).
            _wardPassword = Config.Bind("Patches", "WardPassword", "",
                "Set only in the dedicated server's own config (never on a player's game: Gale copies a profile's configs to everyone who uses it): a player without access to an active " +
                "Protective Wards ward is asked for a password at it, and this password adds them to that ward's permitted list, as a ward's own password would. Empty: nobody is asked. " +
                "At most " + WardPassword.MaxLength + " characters; after a wrong guess a player waits " + WardPassword.WaitSeconds + " s before it is checked for them again.");
            _wardPasswordSet = Config.Bind("Patches", "WardPasswordSet", false,
                "Written by the server itself from WardPassword: whether players without access are asked for the server's ward password. ServerSync passes it to every player running Vigil.");
            _host.Sync.AddConfigEntry(_wardPasswordSet);
            ApplyWards();
            _wardNetwork.SettingChanged += (_, __) => ApplyWards();
            _wardAuto.SettingChanged += (_, __) => ApplyWards();
            _wardSeconds.SettingChanged += (_, __) => ApplyWards();
            _wardPassword.SettingChanged += (_, __) => ApplyWards();
            _wardPasswordSet.SettingChanged += (_, __) => ApplyWards();
        }

        private void ApplyWards()
        {
            WardRepairPatch.Network = _wardNetwork.Value;
            WardRepairPatch.Auto = _wardAuto.Value;
            WardRepairPatch.Seconds = WardRepair.ClampSeconds(_wardSeconds.Value);
            _wardSecondsText = _wardSeconds.Value.ToString(Ic);
            WardPasswordPatch.Password = _wardPassword.Value ?? "";
            bool has = WardPasswordPatch.Password.Length > 0;
            if (_wardPasswordSet.Value != has && (ZNet.instance == null || ZNet.instance.IsServer())) _wardPasswordSet.Value = has;
            WardPasswordPatch.ServerHasOne = _wardPasswordSet.Value;
        }

        private void ApplyAway()
        {
            AwaySleepPatch.On = _awaySleep.Value;
            AwaySleepPatch.LimitSeconds = AwaySleep.LimitSeconds(_awayMinutes.Value);
            _awayMinutesText = _awayMinutes.Value.ToString(Ic);
        }

        /// <summary>On the first frame, when every plugin has loaded: the patches find the mods they work on (skip winter on the server too, where the season changes).</summary>
        private void UpdatePatches()
        {
            if (_patchesHooked) return;
            _patchesHooked = true;
            // Every plugin has loaded: the names of the mods' config sync messages, and the names in their files.
            NetAttribution.LearnSyncNames();
            _ownHooks?.Calibrate();
            if (_ownHooks != null && !double.IsNaN(_ownHooks.PerCallMs)) PhaseTimer.PairMs = _ownHooks.PerCallMs;
            if (ObjectInspector.Loaded == null && !ObjectInspector.NamesReading) ObjectInspector.ReadNames();
            SkipWinterPatch.Hook(line => _host.Log.LogInfo(line));
            if (!string.IsNullOrEmpty(SkipWinterPatch.Problem)) _host.Log.LogWarning("Vigil: the Skip winter patch stepped aside: " + SkipWinterPatch.Problem);
            if (!_server) OdinVolumePatch.Hook();
            HookJoinPackages();
            JotunnSyncPatch.Hook();
            if (!string.IsNullOrEmpty(JotunnSyncPatch.Problem)) _host.Log.LogWarning("Vigil: the patch on Jotunn's config sync stepped aside: " + JotunnSyncPatch.Problem);
            if (!_server) ChestScrollPatch.Hook();
            if (ChestScrollPatch.Problem != null) _host.Log.LogWarning("Vigil: the chest grid scroll patch stepped aside: " + ChestScrollPatch.Problem);
            if (!_server) ContainerFitPatch.Hook();
            if (ContainerFitPatch.Problem != null) _host.Log.LogWarning("Vigil: the wide container patch stepped aside: " + ContainerFitPatch.Problem);
            if (!_server) RelocatePatch.Hook(line => _host.Log.LogInfo(line));
            if (RelocatePatch.Problem != null) _host.Log.LogWarning("Vigil: the EasyRelocate patches stepped aside: " + RelocatePatch.Problem);
            if (!_server) BuildMenuPatch.Hook(line => _host.Log.LogInfo(line));
            if (BuildMenuPatch.Problem != null) _host.Log.LogWarning("Vigil: the build menu patch stepped aside: " + BuildMenuPatch.Problem);
            if (BuildMenuPatch.NavigationProblem != null) _host.Log.LogWarning("Vigil: the build menu navigation patch stepped aside: " + BuildMenuPatch.NavigationProblem);
            if (!_server) ComfortBadgesPatch.Hook();
            if (ComfortBadgesPatch.Problem != null) _host.Log.LogWarning("Vigil: the ComfortMe badge patch stepped aside: " + ComfortBadgesPatch.Problem);
            if (!_server) ComfortPlatesPatch.Hook();
            if (ComfortPlatesPatch.Problem != null) _host.Log.LogWarning("Vigil: the ComfortMe highlight patch stepped aside: " + ComfortPlatesPatch.Problem);
            if (!_server) HudCompassPatch.Hook(line => _host.Log.LogWarning(line));
            if (HudCompassPatch.Problem != null) _host.Log.LogWarning("Vigil: the HUDCompass pin patch stepped aside: " + HudCompassPatch.Problem);
            if (HudCompassPatch.FollowProblem != null) _host.Log.LogWarning("Vigil: the compass does not follow Cartur's map: " + HudCompassPatch.FollowProblem);
            VehiclePinsPatch.Hook(_server, line => _host.Log.LogInfo(line));
            if (!_server) CarturMapPatch.Hook(line => _host.Log.LogWarning(line));
            if (CarturMapPatch.Problem != null) _host.Log.LogWarning("Vigil: the patch for Cartur's map search and hide key on HUDCompass's pins stepped aside: " + CarturMapPatch.Problem);
            if (VehiclePinsPatch.Problem != null) _host.Log.LogWarning("Vigil: the patch for carts, ships and portals where they really are stepped aside: " + VehiclePinsPatch.Problem);
            if (!_server) CompassFollowPatch.Hook(line => _host.Log.LogWarning(line));
            if (CompassFollowPatch.Problem != null) _host.Log.LogWarning("Vigil: the patch for HUDCompass's pins following their portal, cart or ship stepped aside: " + CompassFollowPatch.Problem);
            if (!_server) TargetPortalPatch.Hook(line => _host.Log.LogWarning(line));
            if (TargetPortalPatch.Problem != null) _host.Log.LogWarning("Vigil: the patch for TargetPortal's pins following their portal stepped aside: " + TargetPortalPatch.Problem);
            AwaySleepPatch.Hook();
            if (AwaySleepPatch.Problem != null) _host.Log.LogWarning("Vigil: the patch on away players and sleep stepped aside: " + AwaySleepPatch.Problem);
            WardRepairPatch.Hook(line => _host.Log.LogInfo(line));
            if (WardRepairPatch.Problem != null) _host.Log.LogWarning("Vigil: the ward repair patches stepped aside: " + WardRepairPatch.Problem);
            WardPasswordPatch.Hook(line => _host.Log.LogInfo(line));
            if (WardPasswordPatch.Problem != null) _host.Log.LogWarning("Vigil: the ward password patch stepped aside: " + WardPasswordPatch.Problem);
        }

        /// <summary>A patch with this game's own switch: its heading, On/Off, what it does and who sets it.</summary>
        private void OwnSwitch(string heading, ConfigEntry<bool> entry, string key, string note)
        {
            Ui.Heading(heading);
            Ui.BeginFormLine("Switch");
            int picked = Ui.Choice(entry.Value ? 0 : 1, OnOffWords);
            if (picked >= 0 && (picked == 0) != entry.Value)
            {
                bool on = picked == 0;
                Ui.Later(() =>
                {
                    entry.Value = on;
                    EventLog.Write("patch-set", PlayerName(), key + " " + (on ? "on" : "off"));
                });
            }
            Ui.EndLine();
            Ui.Note(note);
            Ui.Row("Set by", "this game's own config (Patches." + key + "), for each player themself");
        }

        private PanelBadge PatchesBadge() => new PanelBadge(((_odinVolume.Value ? 1 : 0) + (_skipWinter.Value ? 1 : 0) + (_joinPackages.Value ? 1 : 0) + (_jotunnSync.Value ? 1 : 0) + (_chestScroll.Value ? 1 : 0) + (_containerFit.Value ? 1 : 0) + (_relocateFixes.Value ? 1 : 0) + (_relocateSpirit.Value ? 1 : 0)
            + (_buildMenu.Value ? 1 : 0) + (_buildNavLater.Value ? 1 : 0) + (_comfortBadges.Value ? 1 : 0) + (_comfortPlates.Value ? 1 : 0) + (_compassPins.Value ? 1 : 0) + (_vehiclePins.Value ? 1 : 0) + (_compassFollow.Value ? 1 : 0) + (_compassMap.Value ? 1 : 0) + (_awaySleep.Value ? 1 : 0) + (_wardNetwork.Value ? 1 : 0) + (_wardAuto.Value ? 1 : 0)) + " of 19 on", Skin.Tone.Quiet);

        private void DrawPatches()
        {
            bool may = MayChangeServerLimit();
            Ui.Heading("OdinOnDemand follows the game's volume");
            Ui.BeginFormLine("Switch");
            if (may)
            {
                int picked = Ui.Choice(_odinVolume.Value ? 0 : 1, OnOffWords);
                if (picked >= 0 && (picked == 0) != _odinVolume.Value)
                {
                    bool on = picked == 0;
                    Ui.Later(() =>
                    {
                        _odinVolume.Value = on;
                        EventLog.Write("patch-set", PlayerName(), "OdinOnDemandVolume " + (on ? "on" : "off"));
                    });
                }
            }
            else Ui.Text(_odinVolume.Value ? "On" : "Off");
            Ui.EndLine();
            Ui.Note("Radios, screens and the girdle play at their own slider × the game's master × music volume, from the first join. OdinOnDemand's master volume is the game's music volume: moving either slider moves the other. After tabbing out, AzuMiscPatches' mute gives the sound back at 100 % instead of at master volume, so the game's own sounds keep their level too.");
            Ui.Row("Set by", "the server, for every player running Vigil (Patches.OdinOnDemandVolume)");
            if (!_server)
            {
                Ui.Row("On this game", Toned(OdinVolumePatch.State, OdinVolumePatch.Problem != null ? ResultTone.Caution : _odinVolume.Value && OdinVolumePatch.OodFound ? ResultTone.Good : ResultTone.Quiet));
                Ui.Row("Now", OdinVolume.NowLine(OdinVolumePatch.Master, OdinVolumePatch.Music));
                string own = OdinVolumePatch.OwnMasters;
                if (own != null) Ui.Row("Its own masters", own);
            }
            Ui.Heading("Skip winter");
            Ui.BeginFormLine("Switch");
            if (may)
            {
                int picked = Ui.Choice(_skipWinter.Value ? 0 : 1, OnOffWords);
                if (picked >= 0 && (picked == 0) != _skipWinter.Value)
                {
                    bool on = picked == 0;
                    Ui.Later(() =>
                    {
                        _skipWinter.Value = on;
                        EventLog.Write("patch-set", PlayerName(), "SkipWinter " + (on ? "on" : "off"));
                    });
                }
            }
            else Ui.Text(_skipWinter.Value ? "On" : "Off");
            Ui.EndLine();
            Ui.Note("Seasonality's seasons go spring, summer, fall, spring: when the next season would be winter, it is spring. A season set by hand is left alone. It takes effect where the season changes, which is the server.");
            Ui.Row("Set by", "the server, for every player running Vigil (Patches.SkipWinter)");
            Ui.Row("On this game", Toned(SkipWinterPatch.State, SkipWinterPatch.Problem != null ? ResultTone.Caution : _skipWinter.Value && SkipWinterPatch.Found ? ResultTone.Good : ResultTone.Quiet));

            Ui.Heading("Join packages built once");
            Ui.BeginFormLine("Switch");
            if (may)
            {
                int picked = Ui.Choice(_joinPackages.Value ? 0 : 1, OnOffWords);
                if (picked >= 0 && (picked == 0) != _joinPackages.Value)
                {
                    bool on = picked == 0;
                    Ui.Later(() =>
                    {
                        _joinPackages.Value = on;
                        EventLog.Write("patch-set", PlayerName(), "JoinPackages " + (on ? "on" : "off"));
                    });
                }
            }
            else Ui.Text(_joinPackages.Value ? "On" : "Off");
            Ui.EndLine();
            Ui.Note("Drop That and Spawn That build the config packages for each joining player from scratch, and the server stands still meanwhile (about 1.5 s for both). On, the server builds them once and every later join gets the same bytes; a dedicated server builds them a minute after its world is up, while nobody is connected. Built again after dropthat:reload or when the world's location count changes.");
            Ui.Row("Set by", "the server, for every player running Vigil (Patches.JoinPackages)");
            bool found = JoinPackagesPatch.DropThatProblem == null || JoinPackagesPatch.SpawnThatProblem == null;
            Ui.Row("On this game", Toned(JoinGameLine(), JoinLeftAlone() ? ResultTone.Caution : _joinPackages.Value && found ? ResultTone.Good : ResultTone.Quiet));
            if (_server) Ui.Row("On the server", Toned(JoinPackages.ServerLine(JoinPackagesPatch.Lines()), ResultTone.Quiet));
            else if (may)
            {
                PollServer(true, 10);
                Ui.Row("On the server", Toned(JoinPackages.ServerRow(_serverStatus?.JoinPackages, asked: _serverStatus != null), ResultTone.Quiet));
            }

            Ui.Heading("Jotunn's config sync with one copy of the keys");
            Ui.BeginFormLine("Switch");
            int jotunn = Ui.Choice(_jotunnSync.Value ? 0 : 1, OnOffWords);
            if (jotunn >= 0 && (jotunn == 0) != _jotunnSync.Value)
            {
                bool on = jotunn == 0;
                Ui.Later(() =>
                {
                    _jotunnSync.Value = on;
                    EventLog.Write("patch-set", PlayerName(), "JotunnConfigSync " + (on ? "on" : "off"));
                });
            }
            Ui.EndLine();
            Ui.Note("When the server's synced settings arrive on joining, Jotunn checks each one against the file's list of keys, which BepInEx copies whole for every setting. On, Jotunn gets one copy of each file's keys per sync, which answers every check the same way, without the copies. The first answers are asked both ways, and Jotunn's own way comes back at a disagreement.");
            Ui.Row("Set by", "this game's own config (Patches.JotunnConfigSync), for each player themself");
            Ui.Row("On this game", Toned(JotunnSyncPatch.State, JotunnSyncPatch.Problem != null ? ResultTone.Caution : _jotunnSync.Value && JotunnSyncPatch.Answered > 0 ? ResultTone.Good : ResultTone.Quiet));

            Ui.Heading("The wheel scrolls a chest grid with a bar");
            Ui.BeginFormLine("Switch");
            int chest = Ui.Choice(_chestScroll.Value ? 0 : 1, OnOffWords);
            if (chest >= 0 && (chest == 0) != _chestScroll.Value)
            {
                bool on = chest == 0;
                Ui.Later(() =>
                {
                    _chestScroll.Value = on;
                    EventLog.Write("patch-set", PlayerName(), "ChestGridScroll " + (on ? "on" : "off"));
                });
            }
            Ui.EndLine();
            Ui.Note("CycleChests switches chests whenever the wheel turns while a chest is open, so a chest whose slots are taller than its view cannot be scrolled. On, the wheel over that chest's grid or its scroll bar scrolls it the game's way; anywhere else it cycles chests as before. Only frames with a wheel turn are kept from CycleChests, so its arrow keys work everywhere.");
            Ui.Row("Set by", "this game's own config (Patches.ChestGridScroll), for each player themself");
            if (!_server) Ui.Row("On this game", Toned(ChestScrollPatch.State, ChestScrollPatch.Problem != null ? ResultTone.Caution : _chestScroll.Value && ChestScrollPatch.Found ? ResultTone.Good : ResultTone.Quiet));

            Ui.Heading("Wide containers fit the frame");
            Ui.BeginFormLine("Switch");
            int fit = Ui.Choice(_containerFit.Value ? 0 : 1, OnOffWords);
            if (fit >= 0 && (fit == 0) != _containerFit.Value)
            {
                bool on = fit == 0;
                Ui.Later(() =>
                {
                    _containerFit.Value = on;
                    EventLog.Write("patch-set", PlayerName(), "WideContainerFit " + (on ? "on" : "off"));
                });
            }
            Ui.EndLine();
            Ui.Note("The game centres a container's slots in the chest frame and scrolls only up and down, so a container wider than the frame, such as a tombstone ExtraSlots widened for its equipped items, loses both sides out of sight and out of reach. On, such a container is shrunk evenly until every column fits; one that fits keeps its size. Only the drawing changes, never the items or their slots.");
            Ui.Row("Set by", "this game's own config (Patches.WideContainerFit), for each player themself");
            if (!_server) Ui.Row("On this game", Toned(ContainerFitPatch.State, ContainerFitPatch.Problem != null ? ResultTone.Caution : _containerFit.Value ? ResultTone.Good : ResultTone.Quiet));

            OwnSwitch("EasyRelocate: quicker, and a move always ends", _relocateFixes, "EasyRelocateFixes",
                "EasyRelocate's first grab of a session searched every mod for two mods that are not installed (about a second), every grab and every end of a move rebuilt six game methods (about 50 ms each way), and while a piece moved its ghost was updated twice a frame. On, the search is skipped, the hooks go in once at the main menu and stay, and the ghost is updated once a frame, as with the hammer. A move always ends: the grab key works while other keys are held, Esc ends it, a text box taking the keys ends it, taking a hammer, hoe or cultivator in hand ends it, and an error in EasyRelocate ends it as a relog would. With one of those tools in hand no piece is grabbed and none is offered; the grab key then says to put the tool away. Each of these writes a \"[Vigil] relocate\" line to the game's log.");
            if (!_server) Ui.Row("On this game", Toned(RelocatePatch.FixesState, RelocatePatch.Problem != null ? ResultTone.Caution : _relocateFixes.Value && RelocatePatch.Found ? ResultTone.Good : ResultTone.Quiet));

            OwnSwitch("EasyRelocate's ghost is a spirit", _relocateSpirit, "EasyRelocateSpiritGhost",
                "The piece EasyRelocate moves is drawn see-through: pale blue where it can be placed, pale red where not, breathing slowly. Off, it is the solid copy EasyRelocate draws. It changes the next ghost.");
            if (!_server) Ui.Row("On this game", Toned(RelocatePatch.SpiritState, RelocatePatch.Problem != null || RelocatePatch.SpiritProblem != null ? ResultTone.Caution : _relocateSpirit.Value && RelocatePatch.Found ? ResultTone.Good : ResultTone.Quiet));

            OwnSwitch("The build menu opens faster", _buildMenu, "BuildMenuFaster",
                "Opening the build menu rebuilds one button per piece, and sets up the menu's arrow-key and controller navigation while it is still hidden, where that only forces every canvas to update. On, when the menu would show the same pieces in the same order, its buttons stay and only their material colours and favourite stars are refreshed, and the navigation is set up once the menu is shown. Nothing in the menu changes. Each open's line in Vigil-events.tsv says whether its buttons were kept.");
            if (!_server) Ui.Row("On this game", Toned(BuildMenuPatch.State, BuildMenuPatch.Problem != null ? ResultTone.Caution : _buildMenu.Value ? ResultTone.Good : ResultTone.Quiet));

            OwnSwitch("The build menu sets up its navigation a frame later", _buildNavLater, "BuildMenuNavigationLater",
                "The build menu links its buttons for the arrow keys and a controller after every open and every change of the search text, and before linking it forces every canvas to update twice and lays the whole button grid out again. On, the linking waits for the next frame, when the screen layout has placed the buttons anyway, and follows the game's rules without forcing anything. For that one frame the arrow keys and a controller still follow the links of before; the mouse is not affected. Each open's line in Vigil-events.tsv says what the linking took, and each search has a line of its own.");
            if (!_server) Ui.Row("On this game", Toned(BuildMenuPatch.NavigationState, BuildMenuPatch.NavigationProblem != null ? ResultTone.Caution : _buildNavLater.Value ? ResultTone.Good : ResultTone.Quiet));

            OwnSwitch("ComfortMe redraws its badges only on a change", _comfortBadges, "ComfortMeBadges",
                "ComfortMe redraws the badge on every piece button of the build menu on every frame the menu is open, over a thousand on the All tab. On, it redraws them only when what they show can have changed: a new comfort snapshot (ComfortMe makes one at least every 0.2 s, and when you move), another player, the menu opening or closing, its buttons rebuilt, or one of ComfortMe's settings changed. Badges look the same; one that follows your materials follows them within 0.2 s, as ComfortMe's comfort does.");
            if (!_server) Ui.Row("On this game", Toned(ComfortBadgesPatch.State, ComfortBadgesPatch.Problem != null ? ResultTone.Caution : _comfortBadges.Value && ComfortBadgesPatch.Found ? ResultTone.Good : ResultTone.Quiet));

            OwnSwitch("ComfortMe clears its hover highlight only when one is lit", _comfortPlates, "ComfortMeHighlight", "Outside its comfort tab ComfortMe switches off the hover highlight on every piece button of the build menu on every frame, hammer out or not, looking each button's highlight up again each time. On, it does that only after a highlight was lit or the menu's buttons were rebuilt; with nothing lit there is nothing to switch off. The highlight looks the same.");
            if (!_server) Ui.Row("On this game", Toned(ComfortPlatesPatch.State, ComfortPlatesPatch.Problem != null ? ResultTone.Caution : _comfortPlates.Value && ComfortPlatesPatch.Found ? ResultTone.Good : ResultTone.Quiet));

            OwnSwitch("HUDCompass keeps its pin icons", _compassPins, "HudCompassPins", CompassPinsNote);
            if (!_server) Ui.Row("On this game", Toned(HudCompassPatch.State, HudCompassPatch.Problem != null ? ResultTone.Caution : _compassPins.Value && HudCompassPatch.Found ? ResultTone.Good : ResultTone.Quiet));

            OwnSwitch("The compass follows Cartur's map", _compassFollow, "HudCompassFollowsCartur", CompassFollowNote);
            if (!_server) Ui.Row("On this game", Toned(HudCompassPatch.FollowState, HudCompassPatch.FollowProblem != null ? ResultTone.Caution : _compassFollow.Value && _compassPins.Value && HudCompassPatch.Found ? ResultTone.Good : ResultTone.Quiet));

            OwnSwitch("Cartur's map search and hide key cover HUDCompass's pins", _compassMap, "HudCompassCarturMap", CompassMapNote);
            if (!_server) Ui.Row("On this game", Toned(CarturMapPatch.State, CarturMapPatch.Problem != null ? ResultTone.Caution : _compassMap.Value && CarturMapPatch.Found ? ResultTone.Good : ResultTone.Quiet));

            OwnSwitch("HUDCompass's pins follow their portal, cart or ship", _compassFollowPins, "HudCompassPinsFollow", CompassPinsFollowNote);
            if (!_server) Ui.Row("On this game", Toned(CompassFollowPatch.State, CompassFollowPatch.Problem != null ? ResultTone.Caution : _compassFollowPins.Value && CompassFollowPatch.Found ? ResultTone.Good : ResultTone.Quiet));

            OwnSwitch("TargetPortal's pins follow their portal", _targetPortalFollow, "TargetPortalPinsFollow", TargetPortalFollowNote);
            if (!_server) Ui.Row("On this game", Toned(TargetPortalPatch.State, TargetPortalPatch.Problem != null ? ResultTone.Caution : _targetPortalFollow.Value && TargetPortalPatch.Found ? ResultTone.Good : ResultTone.Quiet));

            Ui.Heading("Carts, ships and portals where they really are");
            Ui.BeginFormLine("Switch");
            if (may)
            {
                int picked = Ui.Choice(_vehiclePins.Value ? 0 : 1, OnOffWords);
                if (picked >= 0 && (picked == 0) != _vehiclePins.Value)
                {
                    bool on = picked == 0;
                    Ui.Later(() =>
                    {
                        _vehiclePins.Value = on;
                        EventLog.Write("patch-set", PlayerName(), "VehiclePinsFromServer " + (on ? "on" : "off"));
                    });
                }
            }
            else Ui.Text(_vehiclePins.Value ? "On" : "Off");
            Ui.EndLine();
            Ui.Note(VehiclePinsNote);
            Ui.Row("Set by", "the server, for every player running Vigil (Patches.VehiclePinsFromServer)");
            Ui.Row(_server ? "On the server" : "On this game", Toned(VehiclePinsPatch.State, VehiclePinsPatch.Problem != null ? ResultTone.Caution : _vehiclePins.Value && VehiclePinsPatch.Found ? ResultTone.Good : ResultTone.Quiet));

            Ui.Heading("Away players don't hold up sleep");
            Ui.BeginFormLine("Switch");
            if (may)
            {
                int picked = Ui.Choice(_awaySleep.Value ? 0 : 1, OnOffWords);
                if (picked >= 0 && (picked == 0) != _awaySleep.Value)
                {
                    bool on = picked == 0;
                    Ui.Later(() =>
                    {
                        _awaySleep.Value = on;
                        EventLog.Write("patch-set", PlayerName(), "AwaySleep " + (on ? "on" : "off"));
                    });
                }
            }
            else Ui.Text(_awaySleep.Value ? "On" : "Off");
            Ui.EndLine();
            if (may)
            {
                if (Ui.Tool("Away after", "Minutes in the game menu, " + AwaySleep.MinMinutes + " to " + AwaySleep.MaxMinutes, "Set", ref _awayMinutesText, 40, "min"))
                {
                    string text = _awayMinutesText;
                    Ui.Later(() =>
                    {
                        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, Ic, out int minutes)) return;
                        _awayMinutes.Value = System.Math.Max(AwaySleep.MinMinutes, System.Math.Min(AwaySleep.MaxMinutes, minutes));
                        EventLog.Write("patch-set", PlayerName(), "AwayMinutes " + _awayMinutes.Value.ToString(Ic));
                    });
                }
            }
            else Ui.Row("Away after", _awayMinutes.Value.ToString(Ic) + " min in the game menu");
            Ui.Note("A player whose game menu has stayed open for these minutes is away: the night is skipped once everyone else is in bed, and somebody has to be. Moving the mouse in the menu does not count; closing it does. An away player out of bed gets no morning (no Rested, no good morning), and the players in bed are told who was away and not counted. A player without this Vigil is never away.");
            Ui.Row("Set by", "the server, for every player running Vigil (Patches.AwaySleep, Patches.AwayMinutes)");
            Ui.Row("On this game", Toned(AwaySleepPatch.State(_host.Link), AwaySleepPatch.Problem != null ? ResultTone.Caution : _awaySleep.Value ? ResultTone.Good : ResultTone.Quiet));

            DrawWardPatches(may);

            Ui.Note(may ? "You can switch patches: the server keeps the change and passes it on." : "Only the server's admins (or the world's host) switch the server's patches; everyone sees them. A patch whose target changed in an update steps aside and says so here and in the self-test.");
        }
    }
}
