using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// The text of "vigil help": one line per command grouped by area, or one command in detail with
    /// its options and defaults, and which area answers each command. The defaults live here and the
    /// console and the load test use them, so the help cannot drift from what the commands do. Lines
    /// stay within 100 characters for the console.
    /// </summary>
    internal static class ConsoleHelp
    {
        public const int DeepSeconds = 20, CensusRadius = 60, PlayersDefault = 16, ObjectsDefault = 10, LinkDefault = 256, TraceSeconds = 300;
        public const int MissingRadius = 64, BusiestRadius = 64, BusiestSeconds = 10;

        /// <summary>The areas, named as their switches in the config's Modules section.</summary>
        public const string Recorder = "Recorder", SpawnMarks = "Spawn marks", Cleanup = "Cleanup";

        public static readonly string[] Areas = { Recorder, SpawnMarks, Cleanup };

        /// <summary>The first words "vigil" understands, in the order the overview lists them (tab completion offers them).</summary>
        public static readonly string[] Commands =
        {
            "status", "selftest", "on", "off", "auto", "mark", "deep", "server", "load", "census", "memory", "patches", "trace", "inspect", "busiest", "weather",
            "spawned", "spawnable", "leftovers", "spawns", "missing", "help",
        };

        /// <summary>How far back "vigil spawned" looks by default.</summary>
        public const int SpawnedMinutes = 30;

        /// <summary>The area that answers a command; null for "status" and "help", which Vigil answers itself, and for unknown words.</summary>
        public static string Owner(string word)
        {
            switch (word)
            {
                case "spawns": return SpawnMarks;
                case "missing": return Cleanup;
                case "status":
                case "selftest":
                case "help":
                case "?":
                    return null;
                default:
                    return Array.IndexOf(Commands, word) >= 0 ? Recorder : null;
            }
        }

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        private static readonly char[] Space = { ' ' };

        private static string Minutes(double seconds) => string.Format(Ci, "about {0:0} minutes", seconds / 60);

        /// <param name="topic">Empty for the overview, else a command ("load players" for the player simulation).</param>
        /// <param name="markKey">The configured lag-report key.</param>
        /// <param name="spawnKey">The configured key for the spawn marks.</param>
        /// <param name="dir">Where the recordings go.</param>
        /// <param name="panelKey">The configured key for the panel; null where there is no panel.</param>
        public static List<string> Lines(string topic, string markKey, string spawnKey, string dir, string panelKey = null)
        {
            var words = (topic ?? "").ToLowerInvariant().Split(Space, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count > 0 && words[0] == "vigil") words.RemoveAt(0);
            string key = words.Count == 0 ? "" : words[0] == "load" && words.Contains("players") ? "players" : words[0];
            var lines = Topic(key, markKey, spawnKey);
            if (lines != null) return lines;
            var all = new List<string>();
            if (key.Length > 0) all.Add("No help on \"" + key + "\".");
            all.AddRange(Overview(markKey, spawnKey, dir, panelKey));
            return all;
        }

        private static List<string> Overview(string markKey, string spawnKey, string dir, string panelKey)
        {
            var lines = new List<string>
            {
                "Vigil records what the game does, so a lag can be read afterwards from the files, and shows",
                "what the game sees around you. vigil help <command> explains one command.",
                "  vigil [status]               this session so far, the recording switch, the spawn marks",
                "  vigil selftest [server]      check Vigil against the running game, or the server's",
                "  vigil help [command]         this list, or one command in detail",
                "Recording",
                "  vigil on | off | auto        recording switch (auto, the default: while in a world)",
                "  vigil mark [note]            write a lag report now (key: " + markKey + ")",
                string.Format(Ci, "  vigil deep [seconds]         time every method mods patch, {0} s by default", DeepSeconds),
                "  vigil server on | off | auto | status    the server's recording switch (server admins)",
                "Load tests",
                "  vigil load [up|down]         load test against the dedicated server, " + Minutes(LoadPlan.For("both").TotalSeconds),
                "  vigil load players [max]     simulated players on the server, " + Minutes(LoadPlan.ForPlayers(PlayersDefault).TotalSeconds),
                "  vigil load stop | status     end or check a running test",
                "Inspect",
                string.Format(Ci, "  vigil census [radius]        objects around you, what they cost to draw, by mod ({0} m)", CensusRadius),
                "  vigil memory                 what fills memory: textures, meshes, sounds, per mod",
                "  vigil patches                every Harmony patch on a game method, by mod",
                "  vigil trace materials        which code gives objects material copies (start in main menu)",
                "  vigil inspect [names]        the object under your crosshair: owner, values, record, scripts",
                string.Format(Ci, "  vigil busiest [radius]       objects around you whose changes take the most to send ({0} m, {1} s)", BusiestRadius, BusiestSeconds),
                "  vigil weather [report]       the weather and why, fog, wind; a report to compare with others",
                "  vigil weather limit [server] <fog|off>   cap the fog drawn on your game (or everyone's)",
                string.Format(Ci, "  vigil spawned [server] [min] what your game (or everyone's, for admins) spawned ({0} min)", SpawnedMinutes),
                "  vigil spawnable              what could spawn at the spot under your crosshair, and why not",
                "  vigil leftovers [scan|...]   the whole world on the server: removed mods' objects (admins)",
                "Spawn marks",
                "  vigil spawns                 show or hide where creatures can still spawn (key: " + spawnKey + ")",
                "Cleanup",
                string.Format(Ci, "  vigil missing [remove]       objects near you whose prefab no mod provides ({0} m)", MissingRadius),
                "Recordings: " + dir,
            };
            if (panelKey != null) lines.Insert(2, "The panel, with buttons for most of this, opens with " + panelKey + ".");
            return lines;
        }

        private static List<string> Topic(string key, string markKey, string spawnKey)
        {
            switch (key)
            {
                case "status":
                    return new List<string>
                    {
                        "vigil  or  vigil status",
                        "  Incidents written so far, frame times and median ping of this session, whether a deep",
                        "  capture is running, the recording switch and why it is on or off, the folder the",
                        "  recordings go to, and whether the spawn marks are shown.",
                    };
                case "on":
                case "off":
                case "auto":
                    return new List<string>
                    {
                        "vigil on | off | auto",
                        "  The recording switch (Recording > Mode in the config; it is kept across restarts).",
                        "  on: records all the time.  off: records nothing and does no work per frame.",
                        "  auto, the default: records while you are in a world. A dedicated server records while",
                        "  players are connected, for 2 minutes after the last one leaves and for its first 5 minutes.",
                        "  Deep captures and load tests always record. The server's switch: vigil server.",
                    };
                case "mark":
                    return new List<string>
                    {
                        "vigil mark [note]   or the key " + markKey + " (Lag report > Key in the config)",
                        "  Writes a lag report now, Vigil-incident-<time>-manual.txt: the worst recent frame,",
                        "  frames over 50 ms, the last 300 seconds row by row, network messages and uploads, time",
                        "  per mod, the log's repeated lines and its last 150 lines. The note goes into the report.",
                    };
                case "deep":
                    return new List<string>
                    {
                        string.Format(Ci, "vigil deep [seconds]   (default {0})", DeepSeconds),
                        "  Times every game method that mods patch, labelled with the mods that patch it, and",
                        "  writes Vigil-deep-<time>.txt with each method's average and its longest single frame.",
                        "  The costliest ones, and any that stalls a frame by " + DeepSplit.SpikeMs.ToString("0", Ci) + " ms or more, are split into",
                        "  the game's own code and the mods' part, so an action repeated after its first stall shows",
                        "  how much of it the mods add. Automatic stutter reports pause meanwhile; F9 still works.",
                        "  Putting the timers in and taking them out costs some frame rate for a few seconds at each",
                        "  end. A slow stretch starts one on its own (Deep capture in the config).",
                    };
                case "load":
                    return new List<string>
                    {
                        "vigil load [up|down] [force] [deep]   (" + Minutes(LoadPlan.For("both").TotalSeconds) + "; server admins only)",
                        "  Measures how much world-update traffic the connection and the server carry. Needs",
                        "  Vigil on the dedicated server too. Steps of " + string.Join("/", LoadPlan.Rates.Select(r => r.ToString("0", Ci)).ToArray())
                            + string.Format(Ci, " kB/s for {0:0} s each,", LoadPlan.StepSeconds),
                        string.Format(Ci, "  from your game to the server, {0:0} s without load, then the same from the server to you.", LoadPlan.RecoverySeconds),
                        "  up / down: one direction only.   force: run although other players are online.",
                        "  deep: a deep capture on both machines for the whole test.",
                        "  vigil load stop ends a running test; vigil load status shows how far it is.",
                        string.Format(Ci, "  Stand still, at least {0:0} m from the world's center.", LoadAdmission.CenterGuardMeters),
                        "  Both machines write Vigil-load-<time>.txt (verdict and tables) and .tsv (per second).",
                        "  The player simulation: vigil help players.",
                    };
                case "players":
                    return new List<string>
                    {
                        "vigil load players [max] [pattern] [objects <n>] [link <kB/s>] [force] [deep]",
                        string.Format(Ci, "  Adds simulated players on the server, doubling every {0:0} s from 1 to max (default {1},",
                            LoadPlan.PlayerStepSeconds, PlayersDefault),
                        string.Format(Ci, "  up to {0}), then removes them: {1}. Server admins only.", LoadPlan.MaxPlayers, Minutes(LoadPlan.ForPlayers(PlayersDefault).TotalSeconds)),
                        "  Patterns: stay = around you (default); spread = one at each of the busiest places, your",
                        "  bases first; travel = running between those places and around you; teleport = jumping",
                        "  between them, so the server keeps sending whole areas.",
                        string.Format(Ci, "  objects <n>: changing objects each simulated player sends (default {0}).", ObjectsDefault),
                        string.Format(Ci, "  link <kB/s>: each simulated player's connection (default {0}).", LinkDefault),
                        "  force: run although other players are online.   deep: deep captures on both machines.",
                        "  The report says how many players the server kept up with, what gave first, and how much",
                        "  server work each player added.",
                    };
                case "server":
                    return new List<string>
                    {
                        "vigil server on | off | auto | status   (server admins only)",
                        "  Switches the dedicated server's recording from your game, or shows it. Same meaning as",
                        "  vigil on | off | auto; the server keeps it in its own config.",
                    };
                case "inspect":
                    return new List<string>
                    {
                        "vigil inspect [names]",
                        "  Reads the world object under your crosshair, as far as 50 m: what it is and its mod,",
                        "  whose game runs it, how the game keeps it, its size, its stored values by name with the",
                        "  mod whose code holds each name, the game's own record of it (id, owner, revisions, zone,",
                        "  link), the scripts on it by mod, and what its kind adds (a creature's health and target,",
                        "  a piece's support, an item's stack). Reads only. The panel's World tab shows the same",
                        "  for what was under the crosshair when the panel opened, with how often it changes.",
                        "  The names come from every text the game's and the mods' code carries, read once in the",
                        "  background; vigil inspect names reads them again.",
                    };
                case "weather":
                    return new List<string>
                    {
                        "vigil weather [report | limit [server] <fog|off> | set <name> | fog <density|off>",
                        "              | wind <angle> <percent> | time <hours:minutes|fraction> | reset]",
                        "  Without a word: the weather and why (a raid, a zone, a dev command, or the roll from the",
                        "  biome's list), the fog drawn, the wind, and the mods that change weather or light.",
                        "  report writes Vigil-environment-<time>.txt, which the World tab can send to the server and",
                        "  compare with another player's. limit caps the fog drawn on your game (anyone; kept).",
                        "  limit server <fog|off> sets the server's fog limit, which wins over every player's own",
                        "  while it is set (the server's admins; ServerSync passes it on and the server keeps it).",
                        "  The server's admins, or whoever hosts the world, can also change these on their own game",
                        "  only, until reset or leaving the world: set picks a weather by name or a part of it, fog",
                        "  sets the fog drawn, wind its direction and strength, time the time of day drawn.",
                    };
                case "spawned":
                    return new List<string>
                    {
                        string.Format(Ci, "vigil spawned [minutes]   (default {0})", SpawnedMinutes),
                        "  What your game spawned in the last minutes, newest first, and where each came from: a",
                        "  biome's list and its entry (night only or not), a special area's list, a raid, a",
                        "  spawner structure such as a nest, or the fixed spawner of a location; how far from you",
                        "  and whether inside a base (and whether its entry allows that). Spawns another mod's check",
                        "  cancelled are listed apart, with the mods that can cancel them. Spawns run on the game",
                        "  of whoever's area it is, so another player's areas spawn on their game.",
                        "vigil spawned server [minutes]   (default 60, the server's admins)",
                        "  Everyone's spawns, merged by the server's Vigil from the players who send it",
                        "  their spawn log (Sharing on the Overview tab): spawns inside bases by place, a line per",
                        "  game, and what mods cancelled. The World tab's Spawns across the server shows the same.",
                    };
                case "spawnable":
                    return new List<string>
                    {
                        "vigil spawnable",
                        "  Checks every spawn entry for the spot under your crosshair (up to 250 m) against the",
                        "  game's rules: \"no\" when the spot itself fails one (roof or rock above, height,",
                        "  slope, water depth, forest, lava, a base the entry may not spawn in), \"not now\" when",
                        "  only the world's state does (time of day, weather, a world key, enough of them about,",
                        "  a world event, another close by), \"could\" otherwise, once a player stands 40 to 80 m",
                        "  away (or the entry's own range) and none nearer. Lists only the spot's biome, its",
                        "  special areas and a raid spawning near you. Other mods that patch the spawn checks may add",
                        "  checks of their own that Vigil cannot read; the answer names them. The World tab shows",
                        "  the spot from then on, as it does for the spot under the crosshair when the panel opens.",
                    };
                case "leftovers":
                    return new List<string>
                    {
                        "vigil leftovers [scan | remove <hash> | bots]   (the server's admins)",
                        "  The server reads every object the world keeps, a share each frame so it never stalls,",
                        "  and lists those whose prefab no installed mod provides, by prefab with where they lie,",
                        "  and the world's player history with the entries load tests left (bots). Alone it shows",
                        "  the last reading; scan reads the world again. remove <hash> removes every object of that",
                        "  kind: the server saves the world first, then destroys them the way the game destroys any",
                        "  object. bots takes the load-test entries out of the player history; a load test now",
                        "  takes its own out when it stops. The World tab's Leftovers in the world does the same.",
                    };
                case "busiest":
                    return new List<string>
                    {
                        string.Format(Ci, "vigil busiest [radius]   (default {0} m; 0 watches everything loaded)", BusiestRadius),
                        string.Format(Ci, "  Watches every loaded object around you for {0} s and lists the ones whose changes", BusiestSeconds),
                        "  take the most to send, by object, by type and by the game that runs them, into",
                        "  Vigil-busiest-<time>.tsv. A changed object is sent with all its values, at most 20 times",
                        "  a second; moving and turning count as changes. The panel shows it in the World tab.",
                    };
                case "census":
                    return new List<string>
                    {
                        string.Format(Ci, "vigil census [radius]   (default {0} m; 0 counts everything loaded)", CensusRadius),
                        "  Counts the world objects loaded around you by type, with what they ask of the renderer",
                        "  (renderers, shadow casters, lights, particle systems, material copies of their own) and",
                        "  the mod each type comes from, into Vigil-census-<time>.tsv; the same per mod in",
                        "  -mods.tsv, and in -materials.txt what sets the objects with material copies apart.",
                    };
                case "memory":
                    return new List<string>
                    {
                        "vigil memory",
                        "  Lists what fills memory: loaded textures, meshes and sounds by size, materials, shaders",
                        "  and asset bundles, and the same added up per mod (matched through the mods' asset",
                        "  bundles), into Vigil-memory-<time>.txt. It walks every loaded asset at once, so the",
                        "  game can stall for a moment.",
                    };
                case "patches":
                    return new List<string>
                    {
                        "vigil patches",
                        "  Writes Vigil-patches.tsv: every Harmony patch on a game method, with its kind and",
                        "  its mod (Vigil's own timers left out). It is also written once a session in a world.",
                    };
                case "trace":
                    return new List<string>
                    {
                        string.Format(Ci, "vigil trace materials [seconds]   (default {0})   or   vigil trace materials stop", TraceSeconds),
                        "  Names the code that gives objects material copies of their own: objects with copies cannot",
                        "  be drawn together with others of their kind, which costs frame time at a big base. Writes",
                        "  Vigil-material-copies-<time>.txt with each caller, its mod and the objects it copied.",
                        "  Start it in the main menu, then join and go to the place: code that ran before the trace",
                        "  started can be missed. vigil census counts the objects that have copies.",
                    };
                case "spawns":
                    return new List<string>
                    {
                        "vigil spawns   or the key " + spawnKey + " (Spawn marks > Key in the config)",
                        "  Shows or hides a mark on every spot around you where a creature can still spawn: outside",
                        "  every base's spawn protection and with nothing solid above or below. The marks follow you",
                        "  and are checked again every few seconds. Radius, spacing, pillars or flat dots and their",
                        "  colour are set under Spawn marks in the config. Spawns allowed inside bases are counted in",
                        "  the message and named in the log, since the marks cannot show safety from them.",
                    };
                case "missing":
                    return new List<string>
                    {
                        string.Format(Ci, "vigil missing [remove] [radius] [hash]   (default {0} m)", MissingRadius),
                        "  Lists the world objects near you whose prefab no installed mod provides, nearest first:",
                        "  left behind by a removed mod, the game warns \"Missing prefab hash\" about each of them",
                        "  again and again. Nothing is removed without the word remove. With it, your game takes",
                        "  each listed object over and destroys it the way the game destroys any object, and the",
                        "  server drops it from the world for good. A hash limits it to one prefab. Back up first.",
                    };
                case "selftest":
                    return new List<string>
                    {
                        "vigil selftest [server]",
                        "  Checks Vigil against the running game, each area its own part: whether its hooks went in,",
                        "  the game members it reads are there, Steam's numbers can be read, the server answers.",
                        "  The report says at the top how it went and lists what failed first; it also goes into",
                        "  Vigil-selftest.txt. With server: the dedicated server's Vigil checks itself and answers",
                        "  (server admins only).",
                    };
                case "help":
                    return new List<string>
                    {
                        "vigil help [command]",
                        "  Without a command: every command, one line each. With one: that command in detail.",
                    };
                default:
                    return null;
            }
        }
    }
}
