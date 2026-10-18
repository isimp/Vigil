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
        private ConfigEntry<bool> _odinVolume, _skipWinter, _joinPackages;
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
        }

        private PanelBadge PatchesBadge() => new PanelBadge(((_odinVolume.Value ? 1 : 0) + (_skipWinter.Value ? 1 : 0) + (_joinPackages.Value ? 1 : 0)) + " of 3 on", Skin.Tone.Quiet);

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

            Ui.Note(may ? "You can switch patches: the server keeps the change and passes it on." : "Only the server's admins (or the world's host) switch patches; everyone sees them. A patch whose target changed in an update steps aside and says so here and in the self-test.");
        }
    }
}
