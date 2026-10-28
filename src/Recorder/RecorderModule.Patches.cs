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
        private ConfigEntry<bool> _odinVolume, _skipWinter, _joinPackages, _jotunnSync, _chestScroll, _containerFit, _relocateFixes, _relocateSpirit, _buildMenu, _comfortBadges, _awaySleep, _wardNetwork, _wardAuto, _wardPasswordSet;
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
            _comfortBadges = Config.Bind("Patches", "ComfortMeBadges", false,
                "This game's own switch: ComfortMe redraws the badge on every piece button of the build menu on every frame the menu is open. " +
                "On, it redraws them only when what they show can have changed: a new comfort snapshot (ComfortMe makes one at least every 0.2 s, and when you move), another player, the menu opening or closing, its buttons rebuilt, or one of ComfortMe's settings changed.");
            ComfortBadgesPatch.On = _comfortBadges.Value;
            _comfortBadges.SettingChanged += (_, __) => ComfortBadgesPatch.On = _comfortBadges.Value;
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
            if (!_server) ComfortBadgesPatch.Hook();
            if (ComfortBadgesPatch.Problem != null) _host.Log.LogWarning("Vigil: the ComfortMe badge patch stepped aside: " + ComfortBadgesPatch.Problem);
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
            + (_buildMenu.Value ? 1 : 0) + (_comfortBadges.Value ? 1 : 0) + (_awaySleep.Value ? 1 : 0) + (_wardNetwork.Value ? 1 : 0) + (_wardAuto.Value ? 1 : 0)) + " of 13 on", Skin.Tone.Quiet);

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
                "EasyRelocate's first grab of a session searched every mod for two mods that are not installed (about a second), every grab and every end of a move rebuilt six game methods (about 50 ms each way), and while a piece moved its ghost was updated twice a frame. On, the search is skipped, the hooks go in once at the main menu and stay, and the ghost is updated once a frame, as with the hammer. A move always ends: the grab key works while other keys are held, Esc ends it, a text box taking the keys ends it, taking a hammer, hoe or cultivator in hand ends it, and an error in EasyRelocate ends it as a relog would. With one of those tools in hand no piece is grabbed and none is offered. Each of these writes a \"[Vigil] relocate\" line to the game's log.");
            if (!_server) Ui.Row("On this game", Toned(RelocatePatch.FixesState, RelocatePatch.Problem != null ? ResultTone.Caution : _relocateFixes.Value && RelocatePatch.Found ? ResultTone.Good : ResultTone.Quiet));

            OwnSwitch("EasyRelocate's ghost is a spirit", _relocateSpirit, "EasyRelocateSpiritGhost",
                "The piece EasyRelocate moves is drawn see-through: pale blue where it can be placed, pale red where not, breathing slowly. Off, it is the solid copy EasyRelocate draws. It changes the next ghost.");
            if (!_server) Ui.Row("On this game", Toned(RelocatePatch.SpiritState, RelocatePatch.Problem != null || RelocatePatch.SpiritProblem != null ? ResultTone.Caution : _relocateSpirit.Value && RelocatePatch.Found ? ResultTone.Good : ResultTone.Quiet));

            OwnSwitch("The build menu opens faster", _buildMenu, "BuildMenuFaster",
                "Opening the build menu rebuilds one button per piece, and sets up the menu's arrow-key and controller navigation while it is still hidden, where that only forces every canvas to update. On, when the menu would show the same pieces in the same order, its buttons stay and only their material colours and favourite stars are refreshed, and the navigation is set up once the menu is shown. Nothing in the menu changes. Each open's line in Vigil-events.tsv says whether its buttons were kept.");
            if (!_server) Ui.Row("On this game", Toned(BuildMenuPatch.State, BuildMenuPatch.Problem != null ? ResultTone.Caution : _buildMenu.Value ? ResultTone.Good : ResultTone.Quiet));

            OwnSwitch("ComfortMe redraws its badges only on a change", _comfortBadges, "ComfortMeBadges",
                "ComfortMe redraws the badge on every piece button of the build menu on every frame the menu is open, over a thousand on the All tab. On, it redraws them only when what they show can have changed: a new comfort snapshot (ComfortMe makes one at least every 0.2 s, and when you move), another player, the menu opening or closing, its buttons rebuilt, or one of ComfortMe's settings changed. Badges look the same; one that follows your materials follows them within 0.2 s, as ComfortMe's comfort does.");
            if (!_server) Ui.Row("On this game", Toned(ComfortBadgesPatch.State, ComfortBadgesPatch.Problem != null ? ResultTone.Caution : _comfortBadges.Value && ComfortBadgesPatch.Found ? ResultTone.Good : ResultTone.Quiet));

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
