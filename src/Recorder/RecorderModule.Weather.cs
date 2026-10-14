using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// "Weather and light" in the panel's World tab and "vigil weather": the weather and why, the
    /// biome's list as mods leave it, the fog drawn, the wind, the mods that change weather or light
    /// and the player's post-processing (<see cref="WeatherWatch"/>); the fog limiter for anyone;
    /// changes to the weather, fog, wind and time of day drawn for the server's admins or the world's
    /// host, on their own game only; environment reports to send and compare. Every change of weather
    /// is written to Vigil-events.tsv with its cause, and every change made here too.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private const int ReportsListed = 6;
        private static readonly CultureInfo Wc = CultureInfo.InvariantCulture;

        private static readonly Ui.Column[] ListColumns =
        {
            new Ui.Column("Weather", 0, false), new Ui.Column("Weight", 60, true), new Ui.Column("Chance", 70, true),
        };

        private static readonly Ui.Column[] ModColumns = { new Ui.Column("Mod", 150, false), new Ui.Column("Patches", 0, false) };

        private ConfigEntry<string> _fogLimit, _serverFogLimit;
        private bool _limitFromServer, _sameOpen;
        private string _serverLimitText = "off";
        private WeatherWatch.State _weather;
        private double _weatherAt = double.NegativeInfinity, _weatherCheckedAt = double.NegativeInfinity;
        private string _lastWeather, _lastCauseKind;
        private bool _wasInWorld, _listOpen = true, _modsOpen, _postOpen;
        private string _limitText = "off", _setWeatherText = "", _setFogText = "0.01", _setWindText = "0 50", _setTimeText = "12:00";
        private string _lastReport, _comparePath;
        private EnvironmentReport _compareWith;
        private List<(string Path, EnvironmentReport Report)> _envReports = new List<(string Path, EnvironmentReport Report)>();
        private double _envReportsReadAt = double.NegativeInfinity;

        private void BindWeather()
        {
            _fogLimit = Config.Bind("Weather", "FogLimit", "off",
                "Caps the fog drawn on your game: off, or a fog density from 0 to 1 (thick storms are about 0.1 to 0.2; 0.03 is a light haze). Fog an admin set is drawn as set. ServerFogLimit wins while the server sets one.");
            _serverFogLimit = Config.Bind("Weather", "ServerFogLimit", "off",
                "Set on the dedicated server: caps the fog drawn on the game of every player running Vigil, whatever their own FogLimit says; off leaves each player's own limiter in charge. " +
                "ServerSync passes it to the players. The server's admins can also change it in game (the panel or vigil weather limit server), and the server keeps the change.");
            _host.Sync.AddConfigEntry(_serverFogLimit);
            ApplyLimit();
            _fogLimit.SettingChanged += (_, __) => ApplyLimit();
            _serverFogLimit.SettingChanged += (_, __) => ApplyLimit();
            if (!_server) WeatherWatch.Hook();
        }

        /// <summary>The cap in force: the server's while it sets one (ServerSync keeps it current), else the player's own.</summary>
        private void ApplyLimit()
        {
            var (cap, fromServer) = WeatherControl.Limit(_serverFogLimit.Value, _fogLimit.Value);
            WeatherWatch.Limit = cap;
            WeatherWatch.LimitFromServer = fromServer;
            _limitFromServer = fromServer;
            _limitText = LimitWords(_fogLimit.Value);
            _serverLimitText = LimitWords(_serverFogLimit.Value);
        }

        private static string LimitWords(string text) =>
            WeatherControl.TryLimit(text, out float? cap) && cap.HasValue ? cap.Value.ToString("0.###", Wc) : "off";

        /// <summary>Whether this game may change the server's fog limit: a server admin on a server running Vigil, or the host of the world.</summary>
        private bool MayChangeServerLimit()
        {
            if (ZNet.instance == null) return false;
            if (ZNet.instance.IsServer()) return true;
            return _host.Link.Server.State == GreetingState.Answered && _host.Sync.IsAdmin;
        }

        /// <summary>Every frame on a player's game: once a second, notes a change of weather in the events; clears what was set when the world unloads.</summary>
        private void UpdateWeather()
        {
            if (_server) return;
            bool inWorld = Player.m_localPlayer != null && EnvMan.instance != null;
            if (_wasInWorld && !inWorld)
            {
                WeatherWatch.WorldLeft();
                _lastWeather = _lastCauseKind = null;
                _weather = null;
            }
            _wasInWorld = inWorld;
            if (!inWorld || Now - _weatherCheckedAt < 1) return;
            _weatherCheckedAt = Now;
            var s = WeatherWatch.Read(withMods: false);
            if (s == null) return;
            string kind = s.Cause.Kind.ToString();
            if (s.Weather != _lastWeather || (kind != _lastCauseKind && s.Cause.Kind != WeatherCauseKind.Rolled))
                EventLog.Write("weather", s.Weather, WeatherCause.ChangeLine(_lastWeather, s.Weather, s.Cause.Words));
            _lastWeather = s.Weather;
            _lastCauseKind = kind;
        }

        private PanelBadge WeatherBadge()
        {
            if (_weather == null) return null;
            bool set = WeatherWatch.SetSummary(_weather) != "nothing";
            return new PanelBadge(_weather.Weather + (set ? ", set" : _weather.Cause.Kind == WeatherCauseKind.Rolled ? ", rolled" : ", forced"),
                set ? Skin.Tone.Caution : Skin.Tone.Quiet);
        }

        private void DrawWeather()
        {
            if (Player.m_localPlayer == null)
            {
                Ui.Note("Join a world to see its weather.");
                return;
            }

            // Read on a layout pass only, so the pass that draws the card draws what was laid out.
            if (Event.current.type == EventType.Layout && Now - _weatherAt >= 1)
            {
                _weatherAt = Now;
                try { _weather = WeatherWatch.Read(withMods: true); } catch (Exception) { _weather = null; }
            }
            var s = _weather;
            if (s == null)
            {
                Ui.Note("Reading the weather ...");
                return;
            }

            Ui.Row("Weather", s.Weather + ", in " + s.Biome);
            Ui.Row("Why this one", s.Cause.Words);
            Ui.Row("Time", "day " + s.Day + ", " + s.Time + (s.SetTime != null ? " (drawn at " + s.SetTime + ", set)" : "") + " · wind " + s.Wind + (s.SetWind != null ? " (set)" : ""));
            Ui.Row("On you", s.OnYou);
            Ui.Row("Fog drawn", FogWords(s));

            bool listOpen = _listOpen;
            if (Ui.ExpandRow(FoldColumns, new[] { "The " + s.Biome + " list now, as mods leave it (" + s.List.Count.ToString(Wc) + ")" }, listOpen)) Ui.Later(() => _listOpen = !listOpen);
            if (listOpen)
            {
                if (s.List.Count == 0) Ui.Note("No list could be read for this biome.");
                else Ui.Table(ListColumns, s.List.Select(e => new[] { e.Now ? e.Name + "   (now)" : e.Name, e.Weight.ToString("0.##", Wc), e.Chance.ToString("0", Wc) + "%" }));
                Ui.Note("The game rolls from this list once every " + s.Duration.ToString(Wc) + " s of world time, the same roll on every game with the same list.");
            }

            bool modsOpen = _modsOpen;
            if (Ui.ExpandRow(FoldColumns, new[] { "Mods that change weather or light (" + s.Mods.Count.ToString(Wc) + ")" }, modsOpen)) Ui.Later(() => _modsOpen = !modsOpen);
            if (modsOpen)
            {
                if (s.Mods.Count == 0) Ui.Note("None patches the game's weather, fog, biome colours or camera effects.");
                else Ui.Table(ModColumns, s.Mods.Select(m => new[] { m.Key, m.Value }));
            }

            bool postOpen = _postOpen;
            if (Ui.ExpandRow(FoldColumns, new[] { "Your post-processing and graphics" }, postOpen)) Ui.Later(() => _postOpen = !postOpen);
            if (postOpen)
            {
                Ui.Row("On", PostWords(s, true));
                Ui.Row("Off", PostWords(s, false));
                Ui.Row("Graphics", s.Quality + " · " + s.Gpu + " · " + s.Api);
            }

            string ownAbout = _limitFromServer
                ? "Yours counts while the server's is off; the server's " + _serverLimitText + " is in charge now"
                : "Caps the fog drawn on your game; off, or a density such as 0.05";
            if (Ui.Tool("Fog limiter", ownAbout, "Set", ref _limitText, 50, ""))
            {
                string text = _limitText;
                Ui.Later(() => _host.RunShown("weather limit " + text));
            }
            if (MayChangeServerLimit())
            {
                if (Ui.Tool("Server's fog limit", "For every player running Vigil, over their own; kept in the server's config", "Set", ref _serverLimitText, 50, ""))
                {
                    string text = _serverLimitText;
                    Ui.Later(() => _host.RunShown("weather limit server " + text));
                }
            }
            else if (_limitFromServer)
            {
                Ui.Row("Server's fog limit", _serverLimitText + ", set by the server for every player running Vigil");
            }

            DrawWeatherAdmin();
            DrawEnvironmentReports(s);
        }

        private static string FogWords(WeatherWatch.State s)
        {
            string text = s.FogDrawn.ToString("0.000", Wc) + "  <color=" + s.FogColour + ">■</color>";
            if (!float.IsNaN(s.FogWeather) && Mathf.Abs(s.FogWeather - s.FogDrawn) > 0.0005f) text += "  (the weather's own " + s.FogWeather.ToString("0.000", Wc) + ")";
            if (WeatherWatch.SetFog.HasValue) text += ", set by an admin";
            else if (WeatherWatch.Limit.HasValue) text += (WeatherWatch.LimitFromServer ? ", the server's limiter at " : ", limiter at ") + WeatherWatch.Limit.Value.ToString("0.###", Wc);
            return text;
        }

        private static readonly Dictionary<string, string> PostNames = new Dictionary<string, string>
        {
            { "bloom", "bloom" }, { "dof", "depth of field" }, { "sun_shafts", "sun shafts" }, { "ssao", "ambient occlusion" },
            { "aa", "anti-aliasing" }, { "motion_blur", "motion blur" }, { "chromatic", "chromatic aberration" },
        };

        private static string PostWords(WeatherWatch.State s, bool on)
        {
            var names = s.Post.Where(p => p.Value == on).Select(p => PostNames.TryGetValue(p.Key, out string n) ? n : p.Key).ToArray();
            return names.Length == 0 ? "none" : string.Join(", ", names);
        }

        private void DrawWeatherAdmin()
        {
            Ui.Heading("Admin: changes on this game only");
            string refusal = AdminRefusal();
            if (refusal != null)
            {
                Ui.Note(char.ToUpperInvariant(refusal[0]) + refusal.Substring(1) + ". Each game picks and draws its own weather, so a change here shows on the admin's game only.");
                return;
            }

            if (Ui.Tool("Weather", "Any weather the game knows, by its name or a part only it has", "Set", ref _setWeatherText, 120, ""))
            {
                string t = _setWeatherText;
                Ui.Later(() => _host.RunShown("weather set " + t));
            }
            if (Ui.Tool("Fog drawn", "A density from 0 to 1, or off for none", "Set", ref _setFogText, 50, ""))
            {
                string t = _setFogText;
                Ui.Later(() => _host.RunShown("weather fog " + t));
            }
            if (Ui.Tool("Wind", "Direction in degrees and strength in percent", "Set", ref _setWindText, 60, ""))
            {
                string t = _setWindText;
                Ui.Later(() => _host.RunShown("weather wind " + t));
            }
            if (Ui.Tool("Time of day drawn", "Hours and minutes; the world's time goes on", "Set", ref _setTimeText, 50, ""))
            {
                string t = _setTimeText;
                Ui.Later(() => _host.RunShown("weather time " + t));
            }
            if (Ui.Tool("Back to the game's own", "Weather, fog, wind and time as the game has them", "Reset")) Ui.Later(() => _host.RunShown("weather reset"));
        }

        /// <summary>Why this player may not change the weather here; null when they may.</summary>
        private string AdminRefusal()
        {
            bool inWorld = Player.m_localPlayer != null && EnvMan.instance != null;
            bool hosts = ZNet.instance != null && ZNet.instance.IsServer();
            return WeatherControl.Refusal(inWorld, hosts, _host.Link.Server.IsAdmin);
        }

        private void DrawEnvironmentReports(WeatherWatch.State s)
        {
            if (Ui.Tool("Environment report", "Writes this card to a file to send to the server and compare with another player's", "Write"))
                Ui.Later(() => _host.RunShown("weather report"));
            if (_lastReport != null)
            {
                Ui.BeginLine();
                Ui.Note("Written as " + Path.GetFileName(_lastReport) + ".");
                GUILayout.FlexibleSpace();
                bool waiting = _shareQueue.Files.Contains(_lastReport, StringComparer.OrdinalIgnoreCase);
                string lacking = _host.Link.Server.Lacking(Greeting.Share, "take shared reports", adminOnly: false);
                if (waiting) Ui.Note("Waiting for the server");
                else if (lacking != null) Ui.Note(Sentence(lacking));
                else if (Ui.Button("Send to the server"))
                {
                    string path = _lastReport;
                    Ui.Later(() => ShareOne(path));
                }
                Ui.EndLine();
            }

            if (Event.current.type == EventType.Layout && Now - _envReportsReadAt >= 3)
            {
                _envReportsReadAt = Now;
                _envReports = EnvironmentReports();
            }
            var reports = _envReports;
            if (reports.Count > 0) Ui.Heading("Compare with a report (yours, or fetched under The server's reports)");
            foreach (var (path, report) in reports)
            {
                bool comparing = string.Equals(path, _comparePath, StringComparison.OrdinalIgnoreCase);
                string who = report.Values.TryGetValue("player", out string p) ? p : "someone";
                string when = report.Values.TryGetValue("written", out string w) ? w : File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm", Wc);
                string weather = report.Values.TryGetValue("weather", out string x) ? x : "?";
                if (Ui.Tool(who + ", " + when, weather + (path.IndexOf(Path.DirectorySeparatorChar + "server" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0 ? ", fetched from the server" : ", written here"), comparing ? "Close" : "Compare"))
                {
                    string chosen = comparing ? null : path;
                    var with = comparing ? null : report;
                    Ui.Later(() => { _comparePath = chosen; _compareWith = with; });
                }
                if (comparing && _compareWith != null) DrawComparison(s, who);
            }
        }

        /// <summary>
        /// This game now against the report picked: what differs first, each as short lines of yours
        /// and theirs that wrap (lists item by item), then what is the same under a fold.
        /// </summary>
        private void DrawComparison(WeatherWatch.State s, string who)
        {
            var rows = EnvironmentReport.Compare(WeatherWatch.Report(s, Player.m_localPlayer.GetPlayerName()), _compareWith);
            var differ = rows.Where(r => r.Differs).ToList();
            var same = rows.Where(r => !r.Differs && r.Key != "player" && r.Key != "written").ToList();
            if (differ.Count == 0) Ui.Note("Nothing differs from " + who + "'s report.");
            foreach (var r in differ) Ui.Pairs(r.Label, r.Lines(), mark: true);
            bool sameOpen = _sameOpen;
            if (Ui.ExpandRow(FoldColumns, new[] { "Same on both (" + same.Count.ToString(Wc) + ")" }, sameOpen)) Ui.Later(() => _sameOpen = !sameOpen);
            if (sameOpen)
            {
                foreach (var r in same) Ui.Row(r.Label, r.Mine);
            }
        }

        /// <summary>The newest environment reports here: written on this game, and fetched from the server's shared reports.</summary>
        private List<(string Path, EnvironmentReport Report)> EnvironmentReports()
        {
            var found = new List<(string Path, EnvironmentReport Report)>();
            try
            {
                var files = Directory.GetFiles(_dir, "Vigil-environment-*.txt", SearchOption.TopDirectoryOnly).ToList();
                string fetched = Path.Combine(_dir, "server");
                if (Directory.Exists(fetched)) files.AddRange(Directory.GetFiles(fetched, "Vigil-environment-*.txt", SearchOption.AllDirectories));
                foreach (var f in files.OrderByDescending(File.GetLastWriteTime).Take(ReportsListed))
                {
                    var r = EnvironmentReport.Parse(File.ReadAllLines(f));
                    if (r != null) found.Add((f, r));
                }
            }
            catch (Exception) { }
            return found;
        }

        // ------------------------------------------------------------------ console

        /// <summary>"vigil weather [report | limit | set | fog | wind | time | reset]".</summary>
        private string WeatherCommand(string rest)
        {
            var words = (rest ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            string verb = words.Count > 0 ? words[0].ToLowerInvariant() : "";
            string arg = string.Join(" ", words.Skip(1).ToArray());
            if (verb == "limit" && words.Count > 1 && string.Equals(words[1], "server", StringComparison.OrdinalIgnoreCase))
            {
                if (!MayChangeServerLimit()) return "only the server's admins (on a server running Vigil) or the world's host change the server's fog limit";
                string value = string.Join(" ", words.Skip(2).ToArray());
                if (!WeatherControl.TryLimit(value, out float? serverCap)) return "the server's limit takes off or a fog density from 0 to 1";
                _serverFogLimit.Value = serverCap.HasValue ? serverCap.Value.ToString("0.###", Wc) : "off";
                EventLog.Write("weather-set", PlayerName(), "server fog limit " + _serverFogLimit.Value);
                return serverCap.HasValue
                    ? "the server's fog limit is now " + _serverFogLimit.Value + " for every player running Vigil; ServerSync passes it on"
                    : "the server's fog limit is off; each player's own limiter counts again";
            }

            if (verb == "limit")
            {
                if (!WeatherControl.TryLimit(arg, out float? cap)) return "the limiter takes off or a fog density from 0 to 1";
                _fogLimit.Value = cap.HasValue ? cap.Value.ToString("0.###", Wc) : "off";
                EventLog.Write("weather-set", PlayerName(), "fog limiter " + _fogLimit.Value);
                string note = _limitFromServer ? "; the server's limit " + _serverLimitText + " is in charge while it is set" : "";
                return (cap.HasValue ? "fog limited to " + _fogLimit.Value + " on your game" : "fog limiter off") + note;
            }

            var s = WeatherWatch.Read(withMods: verb.Length == 0 || verb == "report");
            if (s == null) return "join a world first";
            if (verb.Length == 0) return WeatherLines(s);
            if (verb == "report") return WriteEnvironmentReport(s);

            string refusal = AdminRefusal();
            if (refusal != null) return refusal;
            var env = EnvMan.instance;
            switch (verb)
            {
                case "set":
                    {
                        string name = WeatherControl.Match(arg, WeatherWatch.Known(), out string why);
                        if (name == null) return why;
                        env.m_debugEnv = name;
                        WeatherWatch.SetWeather = name;
                        return Changed("weather " + name);
                    }
                case "fog":
                    if (!WeatherControl.TryFog(arg, out float fog)) return "fog takes off or a density from 0 to 1";
                    WeatherWatch.SetFog = fog;
                    return Changed("fog " + fog.ToString("0.###", Wc));
                case "wind":
                    if (words.Count < 3 || !WeatherControl.TryWind(words[1], words[2], out float angle, out float strength)) return "wind takes a direction in degrees and a strength in percent, such as: wind 45 80";
                    env.SetDebugWind(angle, strength);
                    return Changed(string.Format(Wc, "wind {0:0}° at {1:0}%", angle, strength * 100f));
                case "time":
                    if (!WeatherControl.TryTime(arg, out float day)) return "time takes hours and minutes with a colon, or the day's fraction from 0 to 1";
                    env.m_debugTimeOfDay = true;
                    env.m_debugTime = day;
                    return Changed(string.Format(Wc, "time drawn {0:00}:{1:00}", (int)(day * 24f), (int)(day * 24f * 60f) % 60));
                case "reset":
                    env.m_debugEnv = "";
                    env.ResetDebugWind();
                    env.m_debugTimeOfDay = false;
                    WeatherWatch.SetWeather = null;
                    WeatherWatch.SetFog = null;
                    return Changed("reset: the game's own weather, fog, wind and time");
                default:
                    return "vigil weather [report | limit <fog|off> | set <name> | fog <density|off> | wind <angle> <percent> | time <hours:minutes> | reset]";
            }
        }

        private string Changed(string what)
        {
            EventLog.Write("weather-set", PlayerName(), what);
            _weatherAt = double.NegativeInfinity;
            return what + " (on this game only)";
        }

        private static string PlayerName() => Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "?";

        private static string WeatherLines(WeatherWatch.State s)
        {
            var lines = new List<string>
            {
                s.Weather + " in " + s.Biome + ": " + s.Cause.Words,
                "  day " + s.Day + ", " + s.Time + " · wind " + s.Wind + " · " + s.OnYou,
                "  fog drawn " + s.FogDrawn.ToString("0.000", Wc) + (float.IsNaN(s.FogWeather) ? "" : ", the weather's own " + s.FogWeather.ToString("0.000", Wc))
                    + (WeatherWatch.Limit.HasValue ? ", limiter " + WeatherWatch.Limit.Value.ToString("0.###", Wc) : ""),
                "  set on this game: " + WeatherWatch.SetSummary(s),
                "  the list: " + string.Join(", ", s.List.Select(e => e.Name + " " + e.Chance.ToString("0", Wc) + "%").ToArray()),
                "  mods on weather and light: " + (s.Mods.Count == 0 ? "none" : string.Join("; ", s.Mods.Select(m => m.Key + ": " + m.Value).ToArray())),
            };
            return string.Join("\n", lines);
        }

        private string WriteEnvironmentReport(WeatherWatch.State s)
        {
            string file = "Vigil-environment-" + DateTime.Now.ToString("HHmmss", Wc) + ".txt";
            string path = Path.Combine(_dir, file);
            File.WriteAllLines(path, WeatherWatch.Report(s, PlayerName()).Lines().ToArray());
            _lastReport = path;
            _envReportsReadAt = double.NegativeInfinity;
            ReportWritten(path);
            bool goes = _share.Value && ShareQueue.Wanted(_shareKinds.Value, path);
            return "environment report written to " + file + (goes ? "; it goes to the server on its own (Sharing)" : "; the World tab can send it to the server");
        }
    }
}
