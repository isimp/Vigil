using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Vigil.Core
{
    /// <summary>A rigidbody as the physics engine holds it: moving and simulated, at rest, or moved by code only.</summary>
    public enum Body { None, Awake, Asleep, Kinematic }

    /// <summary>A renderer's materials: all drawn with GPU instancing, some, none, or a skinned mesh (never instanced).</summary>
    public enum Instancing { On, Some, Off, Skinned }

    /// <summary>A particle system's culling mode as set on it (the same four as Unity's).</summary>
    public enum ParticleCulling { Automatic, PauseAndCatchup, Pause, AlwaysSimulate }

    public enum LightShadow { None, Hard, Soft }

    /// <summary>One loaded character: whose game runs it, and its own time in the physics steps the census timed (NaN when not timed).</summary>
    public sealed class CharacterFacts
    {
        public string Prefab, Mod;
        public bool Yours, Tamed, Player, AiAsleep;
        public Body Body;
        public double Ms = double.NaN;
    }

    /// <summary>A moving world object that is not a character (a cart, a ship, a dropped item), with its own time in the timed steps.</summary>
    public sealed class MoverFacts
    {
        public string Prefab, Mod;
        public bool Yours;
        public Body Body;
        public double Ms = double.NaN;
    }

    /// <summary>
    /// One switched-on renderer: its layer, its distance from the camera, whether it casts a shadow and
    /// was drawn last frame, its instancing, and <see cref="Batch"/>, the mesh and materials it draws
    /// (null for skinned meshes), which tells renderers that could be drawn together.
    /// </summary>
    public sealed class RendererFacts
    {
        public string Prefab, Mod, Layer, Batch;
        public bool Shadows, Drawn, Copy, WorldObject = true;
        public float Distance;
        public Instancing Instancing;
    }

    /// <summary>One particle system: on a building piece or not, its culling mode, whether it plays, is in the main camera's view, and whether Unity counts it as seen by any camera.</summary>
    public sealed class ParticleFacts
    {
        public string Prefab, Mod;
        public bool OnPiece, Playing, InView, UnitySees;
        public ParticleCulling Mode;
        public int Particles;
    }

    /// <summary>One light as it is now (after the game's light distance rule), with the shadow casters within its range when it casts shadows.</summary>
    public sealed class LightFacts
    {
        public string Prefab, Mod, Type;
        public bool On;
        public LightShadow Shadows;
        public float Range;
        public int CastersInRange;
    }

    /// <summary>Where and when the census read, the game's settings it compares against, and what the census cost.</summary>
    public sealed class CensusScene
    {
        public DateTime At;
        /// <summary>Metres around the player; 0 for everything loaded.</summary>
        public float Radius;
        public string Place;
        public float ShadowDistance = 150f;
        public int Cascades = 4;
        /// <summary>The sun's per-layer shadow distances as set now, in words; null when none is set.</summary>
        public string SunLayerDistances;
        /// <summary>The engine's own counters on the census's first frame; -1 when this build does not give them.</summary>
        public int DrawCalls = -1, ShadowCasters = -1, Batches = -1, PixelLights = -1;
        /// <summary>The game's caps from the graphics settings: null unknown, negative for none.</summary>
        public int? PointLightCap, PointShadowCap;
        public double WalkSeconds, OwnMs, MaxFrameMs;
        public int Frames;
        /// <summary>The window in which characters were timed, its physics steps, all FixedUpdate scripts' time and the engine's physics step time in it (NaN when not known).</summary>
        public double TimedSeconds;
        public long Steps;
        public double FixedUpdateMs = double.NaN, PhysicsEngineMs = double.NaN;
        /// <summary>Why the characters were not timed; null when they were.</summary>
        public string TimingProblem;
        /// <summary>World objects that threw while being read, left out of every count.</summary>
        public int Unreadable;
    }

    /// <summary>
    /// The base census: what is loaded around the player that the frame pays for, added up for the
    /// report <c>Vigil-census-*.txt</c>. Characters by kind and whose game runs them, with their time in
    /// the physics steps; moving objects; renderers and shadow casters by layer, by distance from the
    /// camera and by mod, with instancing; particle systems by culling mode and visibility; lights. Each
    /// section leads with the finding that decides a change: how many shadow casters per-layer sun shadow
    /// distances would drop, how many renderers could be drawn together, how many building particle
    /// systems keep simulating off screen, which lights draw shadows. The Add methods keep none of the
    /// facts objects, so one can be filled again for every renderer.
    /// </summary>
    public sealed class BaseCensus
    {
        public const string NoMod = "the game, or no mod matched";

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        /// <summary>Per-layer sun shadow distances to estimate against: ShadowTrim's defaults (buildings, decor, small objects, dropped items).</summary>
        public static readonly IReadOnlyList<KeyValuePair<string, float>> LayerDistances = new List<KeyValuePair<string, float>>
        {
            new KeyValuePair<string, float>("piece", 60f),
            new KeyValuePair<string, float>("piece_nonsolid", 40f),
            new KeyValuePair<string, float>("Default_small", 30f),
            new KeyValuePair<string, float>("item", 25f),
        };

        private static readonly float[] Steps = { 25f, 30f, 40f, 60f };

        /// <summary>The upper edges of the distance bands: the layer distances below the shadow distance, then the shadow distance.</summary>
        public static float[] BandEdges(float shadowDistance) => Steps.Where(e => e < shadowDistance).Concat(new[] { shadowDistance }).ToArray();

        private sealed class Kind
        {
            public string Mod;
            public int Yours, Others, Tamed, Awake, Kinematic, Timed, TimedYours, TimedOthers;
            public double Ms, MsYours, MsOthers;
        }

        private sealed class LayerRow
        {
            public int Renderers, Drawn, Beyond;
            public readonly List<float> Casters = new List<float>();
        }

        private sealed class ModRow
        {
            public int Renderers, Drawn, Casters, On, Off, Some, Skinned, Copies;
        }

        private sealed class BatchRow
        {
            public int Count;
            public readonly Dictionary<string, int> Uninstanced = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        private sealed class ParticleRow
        {
            public string Mod;
            public bool OnPiece;
            public int Systems, Playing, Always, Pausable, PausableParticles, OtherCamera, Particles;
        }

        private sealed class LightRow
        {
            public string Mod, Type;
            public int Lights, On, Shadows, Casters;
            public float Range;
        }

        private readonly Dictionary<string, Kind> _characters = new Dictionary<string, Kind>(StringComparer.Ordinal);
        private readonly Dictionary<string, Kind> _movers = new Dictionary<string, Kind>(StringComparer.Ordinal);
        private readonly Dictionary<string, LayerRow> _layers = new Dictionary<string, LayerRow>(StringComparer.Ordinal);
        private readonly Dictionary<string, LayerRow> _apart = new Dictionary<string, LayerRow>(StringComparer.Ordinal);
        private readonly Dictionary<string, ModRow> _mods = new Dictionary<string, ModRow>(StringComparer.Ordinal);
        private readonly Dictionary<string, BatchRow> _batches = new Dictionary<string, BatchRow>(StringComparer.Ordinal);
        private readonly Dictionary<string, ParticleRow> _particles = new Dictionary<string, ParticleRow>(StringComparer.Ordinal);
        private readonly Dictionary<string, LightRow> _lights = new Dictionary<string, LightRow>(StringComparer.Ordinal);

        private static string Name(string s) => string.IsNullOrEmpty(s) ? "?" : s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        private static string ModName(string mod) => string.IsNullOrEmpty(mod) ? NoMod : Name(mod);

        private static Kind KindOf(Dictionary<string, Kind> rows, string prefab, string mod)
        {
            string key = Name(prefab);
            if (!rows.TryGetValue(key, out var k)) rows[key] = k = new Kind { Mod = mod };
            return k;
        }

        private static void AddTime(Kind k, bool yours, double ms)
        {
            if (double.IsNaN(ms)) return;
            k.Timed++;
            k.Ms += ms;
            if (yours) { k.TimedYours++; k.MsYours += ms; }
            else { k.TimedOthers++; k.MsOthers += ms; }
        }

        public void AddCharacter(CharacterFacts c)
        {
            if (c == null) return;
            var k = KindOf(_characters, c.Prefab, c.Mod);
            if (c.Yours) k.Yours++; else k.Others++;
            if (c.Tamed) k.Tamed++;
            if (c.Body == Body.Awake) k.Awake++;
            if (c.Body == Body.Kinematic) k.Kinematic++;
            AddTime(k, c.Yours, c.Ms);
        }

        public void AddMover(MoverFacts m)
        {
            if (m == null) return;
            var k = KindOf(_movers, m.Prefab, m.Mod);
            if (m.Yours) k.Yours++; else k.Others++;
            if (m.Body == Body.Awake) k.Awake++;
            if (m.Body == Body.Kinematic) k.Kinematic++;
            AddTime(k, m.Yours, m.Ms);
        }

        public void AddRenderer(RendererFacts r)
        {
            if (r == null) return;
            var rows = r.WorldObject ? _layers : _apart;
            string layer = Name(r.Layer);
            if (!rows.TryGetValue(layer, out var row)) rows[layer] = row = new LayerRow();
            row.Renderers++;
            if (r.Drawn) row.Drawn++;
            if (r.Shadows) row.Casters.Add(r.Distance);
            if (!r.WorldObject) return;

            string mod = ModName(r.Mod);
            if (!_mods.TryGetValue(mod, out var m)) _mods[mod] = m = new ModRow();
            m.Renderers++;
            if (r.Drawn) m.Drawn++;
            if (r.Shadows) m.Casters++;
            if (r.Copy) m.Copies++;
            switch (r.Instancing)
            {
                case Instancing.On: m.On++; break;
                case Instancing.Some: m.Some++; break;
                case Instancing.Off: m.Off++; break;
                default: m.Skinned++; break;
            }

            if (r.Instancing == Instancing.Skinned || string.IsNullOrEmpty(r.Batch)) return;
            if (!_batches.TryGetValue(r.Batch, out var b)) _batches[r.Batch] = b = new BatchRow();
            b.Count++;
            if (r.Instancing != Instancing.On) b.Uninstanced[mod] = (b.Uninstanced.TryGetValue(mod, out int n) ? n : 0) + 1;
        }

        public void AddParticles(ParticleFacts p)
        {
            if (p == null) return;
            string key = Name(p.Prefab) + (p.OnPiece ? "\u0001piece" : "");
            if (!_particles.TryGetValue(key, out var row)) _particles[key] = row = new ParticleRow { Mod = p.Mod, OnPiece = p.OnPiece };
            row.Systems++;
            if (!p.Playing) return;
            row.Playing++;
            row.Particles += Math.Max(0, p.Particles);
            if (p.Mode != ParticleCulling.AlwaysSimulate) return;
            row.Always++;
            if (p.InView || !p.OnPiece) return;
            row.Pausable++;
            row.PausableParticles += Math.Max(0, p.Particles);
            if (p.UnitySees) row.OtherCamera++;
        }

        public void AddLight(LightFacts l)
        {
            if (l == null) return;
            string key = Name(l.Prefab) + "\u0001" + Name(l.Type);
            if (!_lights.TryGetValue(key, out var row)) _lights[key] = row = new LightRow { Mod = l.Mod, Type = Name(l.Type) };
            row.Lights++;
            row.Range = Math.Max(row.Range, l.Range);
            if (!l.On) return;
            row.On++;
            if (l.Shadows == LightShadow.None) return;
            row.Shadows++;
            row.Casters += Math.Max(0, l.CastersInRange);
        }

        // ------------------------------------------------------------------ findings

        /// <summary>Shadow casters within <paramref name="shadowDistance"/> that lie past their layer's distance in <paramref name="limits"/>, and all casters within the shadow distance.</summary>
        public (int Dropped, int Casters) ShadowsDropped(float shadowDistance, IEnumerable<KeyValuePair<string, float>> limits)
        {
            var byLayer = (limits ?? Enumerable.Empty<KeyValuePair<string, float>>()).ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
            int dropped = 0, casters = 0;
            foreach (var rows in new[] { _layers, _apart })
            {
                foreach (var kv in rows)
                {
                    bool limited = byLayer.TryGetValue(kv.Key, out float limit) && limit > 0 && limit < shadowDistance;
                    foreach (float d in kv.Value.Casters)
                    {
                        if (d > shadowDistance) continue;
                        casters++;
                        if (limited && d > limit) dropped++;
                    }
                }
            }
            return (dropped, casters);
        }

        /// <summary>Loaded characters counted, players included.</summary>
        public int CharacterCount => _characters.Values.Sum(k => k.Yours + k.Others);

        /// <summary>Renderers drawn without instancing whose mesh and materials another renderer shares: with instancing on they could share draw calls.</summary>
        public int CouldGroup => _batches.Values.Where(b => b.Count >= 2).Sum(b => b.Uninstanced.Values.Sum());

        /// <summary>Playing particle systems on building pieces set to always simulate and outside the main camera's view, with their particles.</summary>
        public (int Systems, int Particles) Pausable => (_particles.Values.Sum(p => p.Pausable), _particles.Values.Sum(p => p.PausableParticles));

        private static string Within(CensusScene s) => s.Radius > 0 ? "within " + s.Radius.ToString("0", Ci) + " m" : "loaded";

        private static string Ms(double ms) => ms.ToString("0.000", Ci);

        private static string Pct(double part, double whole) => whole > 0 ? (part / whole * 100).ToString("0", Ci) + " %" : "-";

        private static bool Timed(CensusScene s) => s.Steps > 0 && s.TimingProblem == null;

        public string CharactersLead(CensusScene s)
        {
            int yours = _characters.Values.Sum(k => k.Yours), others = _characters.Values.Sum(k => k.Others);
            if (yours + others == 0) return "No characters " + Within(s) + ".";
            var sb = new StringBuilder();
            sb.AppendFormat(Ci, "{0} characters {1}: {2} run by your game, {3} by other players' games; {4} tamed.",
                yours + others, Within(s), yours, others, _characters.Values.Sum(k => k.Tamed));
            if (!Timed(s))
            {
                sb.Append(" Their physics-step time was not timed: ").Append(s.TimingProblem ?? "no physics step ran").Append('.');
                return sb.ToString();
            }

            double ms = _characters.Values.Sum(k => k.Ms);
            sb.AppendFormat(Ci, " In {0} physics steps over {1:0.0} s they took {2} ms a step", s.Steps, s.TimedSeconds, Ms(ms / s.Steps));
            if (!double.IsNaN(s.FixedUpdateMs) && s.FixedUpdateMs > 0) sb.AppendFormat(Ci, ", {0} of the game's FixedUpdate work", Pct(ms, s.FixedUpdateMs));
            sb.Append('.');
            int ty = _characters.Values.Sum(k => k.TimedYours), to = _characters.Values.Sum(k => k.TimedOthers);
            if (ty > 0 || to > 0)
            {
                sb.Append(" On average one your game runs takes ")
                  .Append(ty > 0 ? Ms(_characters.Values.Sum(k => k.MsYours) / ty / s.Steps) + " ms a step" : "nothing (none timed)")
                  .Append(", one another game runs ")
                  .Append(to > 0 ? Ms(_characters.Values.Sum(k => k.MsOthers) / to / s.Steps) + " ms" : "nothing (none timed)")
                  .Append('.');
            }
            var top = _characters.Where(kv => kv.Value.Timed > 0).OrderByDescending(kv => kv.Value.Ms).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(3).ToList();
            if (top.Count > 0)
            {
                sb.Append(" Most: ").Append(string.Join(", ", top.Select(kv => string.Format(Ci, "{0} x{1} {2} ms a step", kv.Key, kv.Value.Yours + kv.Value.Others, Ms(kv.Value.Ms / s.Steps))))).Append('.');
            }
            if (!double.IsNaN(s.PhysicsEngineMs)) sb.AppendFormat(Ci, " Unity's physics step itself took {0} ms a step.", Ms(s.PhysicsEngineMs / s.Steps));
            return sb.ToString();
        }

        private static string Distances(IEnumerable<KeyValuePair<string, float>> limits) =>
            string.Join(", ", limits.Select(l => l.Key + " " + l.Value.ToString("0", Ci))) + " m";

        public string ShadowLead(CensusScene s)
        {
            var (dropped, casters) = ShadowsDropped(s.ShadowDistance, LayerDistances);
            if (casters == 0) return "No shadow casters " + Within(s) + ".";
            int drawn = _layers.Values.Concat(_apart.Values).Sum(r => r.Drawn);
            var sb = new StringBuilder();
            sb.AppendFormat(Ci, "Sun shadow distances by layer ({0}, ShadowTrim's defaults) would stop {1} of the {2} shadow casters within the {3:0} m shadow distance ({4}).",
                Distances(LayerDistances), dropped, casters, s.ShadowDistance, Pct(dropped, casters));
            sb.AppendFormat(Ci, " {0} renderers in all, {1} drawn last frame", _layers.Values.Concat(_apart.Values).Sum(r => r.Renderers), drawn);
            if (s.DrawCalls >= 0) sb.AppendFormat(Ci, "; the engine counted {0} draw calls", s.DrawCalls);
            if (s.ShadowCasters >= 0) sb.AppendFormat(Ci, " and {0} shadow casters", s.ShadowCasters);
            sb.Append('.');
            return sb.ToString();
        }

        public string InstancingLead()
        {
            int n = CouldGroup;
            if (_mods.Count == 0) return "No renderers.";
            if (n == 0) return "No renderers draw without instancing while another renderer shares their mesh and material.";
            var byMod = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var b in _batches.Values.Where(b => b.Count >= 2))
                foreach (var kv in b.Uninstanced) byMod[kv.Key] = (byMod.TryGetValue(kv.Key, out int m) ? m : 0) + kv.Value;
            return string.Format(Ci, "{0} renderers draw without instancing though their mesh and material repeat; with instancing they could share draw calls. Most: {1}.",
                n, string.Join(", ", byMod.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(3).Select(kv => kv.Key + " " + kv.Value.ToString(Ci))));
        }

        public string ParticlesLead()
        {
            if (_particles.Count == 0) return "No particle systems.";
            var (systems, particles) = Pausable;
            int playing = _particles.Values.Sum(p => p.Playing), onPieces = _particles.Values.Where(p => p.OnPiece).Sum(p => p.Playing);
            var sb = new StringBuilder();
            if (systems == 0) sb.Append("No particle systems on building pieces keep simulating off screen.");
            else
            {
                sb.AppendFormat(Ci, "{0} particle systems on building pieces keep simulating off screen (always simulate), {1} particles; pausing them off screen would stop that.", systems, particles);
                int other = _particles.Values.Sum(p => p.OtherCamera);
                if (other > 0) sb.AppendFormat(Ci, " {0} of them Unity counts as seen by another camera (the game's depth or reflection camera), which would wake them now and then.", other);
            }
            sb.AppendFormat(Ci, " {0} systems playing, {1} on building pieces.", playing, onPieces);
            return sb.ToString();
        }

        private static string Cap(int? cap) => cap == null ? "unknown" : cap < 0 ? "none" : cap.Value.ToString(Ci);

        public string LightsLead(CensusScene s)
        {
            if (_lights.Count == 0) return "No lights.";
            int lights = _lights.Values.Sum(l => l.Lights), on = _lights.Values.Sum(l => l.On);
            int points = _lights.Values.Where(l => l.Type == "Point").Sum(l => l.On);
            int shadows = _lights.Values.Sum(l => l.Shadows), casters = _lights.Values.Sum(l => l.Casters);
            var sb = new StringBuilder();
            sb.AppendFormat(Ci, "{0} of {1} lights on: {2} point lights (the game's cap: {3}), {4} casting shadows (the game's cap: {5})",
                on, lights, points, Cap(s.PointLightCap), shadows, Cap(s.PointShadowCap));
            if (shadows > 0) sb.AppendFormat(Ci, ", with {0} shadow casters in their range (Unity draws a point light's shadow in six views)", casters);
            sb.Append('.');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ the report

        private static void Table(StringBuilder sb, string[] head, IEnumerable<string[]> rows, int textColumns)
        {
            var all = new List<string[]> { head };
            all.AddRange(rows);
            var widths = new int[head.Length];
            foreach (var r in all)
                for (int i = 0; i < head.Length && i < r.Length; i++) widths[i] = Math.Max(widths[i], r[i].Length);
            foreach (var r in all)
            {
                sb.Append("   ");
                for (int i = 0; i < head.Length; i++)
                {
                    string cell = i < r.Length ? r[i] : "";
                    if (i < textColumns) sb.Append(cell.PadRight(widths[i]));
                    else sb.Append(cell.PadLeft(widths[i]));
                    if (i < head.Length - 1) sb.Append("  ");
                }
                sb.Append("\r\n");
            }
        }

        private static string N(int n) => n.ToString(Ci);

        private string[] KindCells(string name, Kind k, CensusScene s, bool character)
        {
            bool timed = Timed(s) && k.Timed > 0;
            string perStep = timed ? Ms(k.Ms / s.Steps) : "-";
            string share = timed && !double.IsNaN(s.FixedUpdateMs) ? Pct(k.Ms, s.FixedUpdateMs) : "-";
            return character
                ? new[] { name, ModName(k.Mod), N(k.Yours), N(k.Others), N(k.Tamed), N(k.Awake), perStep, share }
                : new[] { name, ModName(k.Mod), N(k.Yours), N(k.Others), N(k.Awake), perStep, share };
        }

        private static IEnumerable<KeyValuePair<string, Kind>> ByTime(Dictionary<string, Kind> rows) =>
            rows.OrderByDescending(kv => kv.Value.Ms).ThenByDescending(kv => kv.Value.Yours + kv.Value.Others).ThenBy(kv => kv.Key, StringComparer.Ordinal);

        private static string[] LayerCells(string layer, LayerRow r, float[] edges, float shadowDistance)
        {
            var cells = new List<string> { layer, N(r.Renderers), N(r.Drawn), N(r.Casters.Count(d => d <= shadowDistance)) };
            float low = float.NegativeInfinity;
            foreach (float e in edges)
            {
                float from = low;
                cells.Add(N(r.Casters.Count(d => d > from && d <= e)));
                low = e;
            }
            cells.Add(N(r.Casters.Count(d => d > shadowDistance)));
            return cells.ToArray();
        }

        public string Format(CensusScene s, IEnumerable<string> materialCopies)
        {
            var sb = new StringBuilder();
            sb.Append("Vigil census   ").Append(s.At.ToString("yyyy-MM-dd HH:mm:ss", Ci)).Append("\r\n");
            sb.Append("What is loaded ").Append(Within(s)).Append(s.Radius > 0 ? " of you" : "").Append(string.IsNullOrEmpty(s.Place) ? "" : ", " + s.Place).Append(".\r\n");
            sb.AppendFormat(Ci, "Settings: sun shadows to {0:0} m in {1} cascades; the sun's per-layer shadow distances: {2}; pixel lights {3}; point lights cap {4}, point-light shadows cap {5}.\r\n",
                s.ShadowDistance, s.Cascades, s.SunLayerDistances ?? "none set", s.PixelLights >= 0 ? N(s.PixelLights) : "unknown", Cap(s.PointLightCap), Cap(s.PointShadowCap));
            if (s.DrawCalls >= 0 || s.ShadowCasters >= 0)
                sb.AppendFormat(Ci, "The engine's counters as the census began: {0} draw calls, {1} batches, {2} shadow casters.\r\n",
                    s.DrawCalls >= 0 ? N(s.DrawCalls) : "?", s.Batches >= 0 ? N(s.Batches) : "?", s.ShadowCasters >= 0 ? N(s.ShadowCasters) : "?");
            sb.AppendFormat(Ci, "The census took {0:0.0} s: {1:0} ms of Vigil's own work over {2} frames, at most {3:0.0} ms in one", s.WalkSeconds, s.OwnMs, s.Frames, s.MaxFrameMs);
            if (Timed(s)) sb.AppendFormat(Ci, "; then {0:0.0} s of character timing ({1} physics steps)", s.TimedSeconds, s.Steps);
            sb.Append(".\r\n");
            if (s.Unreadable > 0) sb.AppendFormat(Ci, "{0} objects could not be read and are left out.\r\n", s.Unreadable);

            sb.Append("\r\n== Characters ==\r\n").Append(CharactersLead(s)).Append("\r\n");
            if (_characters.Count > 0)
                Table(sb, new[] { "kind", "mod", "yours", "others", "tamed", "awake bodies", "ms a step", "share" },
                    ByTime(_characters).Select(kv => KindCells(kv.Key, kv.Value, s, character: true)), 2);
            sb.Append("   Times are each character's own FixedUpdate (Character/Humanoid.CustomFixedUpdate), its position sync and an animal's AI; the share is\r\n");
            sb.Append("   against MonoUpdaters.FixedUpdate, the game's loop over every character, AI and moving object. A monster's AI (MonsterAI.UpdateAI)\r\n");
            sb.Append("   and the physics engine's step are not split per character. A character another game runs moves no AI here, but its body still\r\n");
            sb.Append("   takes part in this game's physics.\r\n");

            sb.Append("\r\n== Other moving objects ==\r\n");
            if (_movers.Count == 0) sb.Append("No other moving objects ").Append(Within(s)).Append(".\r\n");
            else
            {
                sb.AppendFormat(Ci, "{0} moving objects that are not characters (carts, ships, dropped items, ...), {1} with an awake body.\r\n",
                    _movers.Values.Sum(k => k.Yours + k.Others), _movers.Values.Sum(k => k.Awake));
                Table(sb, new[] { "kind", "mod", "yours", "others", "awake bodies", "ms a step", "share" },
                    ByTime(_movers).Take(40).Select(kv => KindCells(kv.Key, kv.Value, s, character: false)), 2);
            }

            var edges = BandEdges(s.ShadowDistance);
            var bandHeads = new List<string> { "layer", "renderers", "drawn", "shadow casters" };
            float lo = 0;
            foreach (float e in edges) { bandHeads.Add(lo.ToString("0", Ci) + "-" + e.ToString("0", Ci) + " m"); lo = e; }
            bandHeads.Add("past " + s.ShadowDistance.ToString("0", Ci) + " m");

            sb.Append("\r\n== Renderers and shadow casters ==\r\n").Append(ShadowLead(s)).Append("\r\n");
            sb.Append("   By layer, world objects only. Shadow casters counted within the shadow distance, then by distance from the camera; those past it cast none.\r\n");
            if (_layers.Count > 0)
                Table(sb, bandHeads.ToArray(), _layers.OrderByDescending(kv => kv.Value.Casters.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => LayerCells(kv.Key, kv.Value, edges, s.ShadowDistance)), 1);

            sb.Append("\r\n== By mod ==\r\n").Append(InstancingLead()).Append("\r\n");
            if (_mods.Count > 0)
            {
                var groups = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var b in _batches.Values.Where(b => b.Count >= 2))
                    foreach (var kv in b.Uninstanced) groups[kv.Key] = (groups.TryGetValue(kv.Key, out int g) ? g : 0) + kv.Value;
                Table(sb, new[] { "mod", "renderers", "drawn", "shadow casters", "instancing on", "off", "some", "skinned", "could group", "own copies" },
                    _mods.OrderByDescending(kv => kv.Value.Renderers).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new[]
                    {
                        kv.Key, N(kv.Value.Renderers), N(kv.Value.Drawn), N(kv.Value.Casters), N(kv.Value.On), N(kv.Value.Off), N(kv.Value.Some), N(kv.Value.Skinned),
                        N(groups.TryGetValue(kv.Key, out int n) ? n : 0), N(kv.Value.Copies),
                    }), 1);
            }

            sb.Append("\r\n== Not part of a world object ==\r\n");
            if (_apart.Count == 0) sb.Append("None counted.\r\n");
            else
            {
                sb.Append("Terrain, water, sky, weather and effects without an object of their own, by layer. Grass is drawn without renderers and never shows here.\r\n");
                Table(sb, bandHeads.ToArray(), _apart.OrderByDescending(kv => kv.Value.Renderers).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => LayerCells(kv.Key, kv.Value, edges, s.ShadowDistance)), 1);
            }

            sb.Append("\r\n== Particle systems ==\r\n").Append(ParticlesLead()).Append("\r\n");
            if (_particles.Count > 0)
                Table(sb, new[] { "kind", "mod", "on a piece", "systems", "playing", "always simulate", "simulating off screen", "their particles", "particles" },
                    _particles.OrderByDescending(kv => kv.Value.Pausable).ThenByDescending(kv => kv.Value.Playing).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new[]
                    {
                        kv.Key.Split('\u0001')[0], ModName(kv.Value.Mod), kv.Value.OnPiece ? "yes" : "no", N(kv.Value.Systems), N(kv.Value.Playing), N(kv.Value.Always),
                        kv.Value.OnPiece ? N(kv.Value.Pausable) : "-", kv.Value.OnPiece ? N(kv.Value.PausableParticles) : "-", N(kv.Value.Particles),
                    }), 3);

            sb.Append("\r\n== Lights ==\r\n").Append(LightsLead(s)).Append("\r\n");
            if (_lights.Count > 0)
                Table(sb, new[] { "kind", "mod", "type", "lights", "on", "with shadows", "range", "shadow casters in range" },
                    _lights.OrderByDescending(kv => kv.Value.Shadows).ThenByDescending(kv => kv.Value.On).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new[]
                    {
                        kv.Key.Split('\u0001')[0], ModName(kv.Value.Mod), kv.Value.Type, N(kv.Value.Lights), N(kv.Value.On), N(kv.Value.Shadows),
                        kv.Value.Range.ToString("0", Ci) + " m", N(kv.Value.Casters),
                    }), 3);

            sb.Append("\r\n== Material copies ==\r\n");
            sb.Append("A renderer that asked for its material(s) draws with copies of its own, which cannot be drawn together with others of their kind.\r\n");
            sb.Append("vigil trace materials names the code that made them.\r\n");
            foreach (string l in materialCopies ?? Enumerable.Empty<string>()) sb.Append(l).Append("\r\n");

            sb.Append("\r\n== What the census cannot see ==\r\n");
            foreach (string l in Limits) sb.Append("   ").Append(l).Append("\r\n");
            return sb.ToString();
        }

        private static readonly string[] Limits =
        {
            "Only this game: other players' drawing and physics are their own.",
            "One snapshot spread over a second or more: objects that load or unload meanwhile can be missed (none is counted twice).",
            "Drawn last frame is Unity's own word for any camera, shadow passes included; the game's depth camera (once a second) and reflection probe (every 3 s) add to it.",
            "Shadow casters are candidates (casting on, within the shadow distance); the engine's counter counts what each cascade drew.",
            "Instancing is the materials' switch; the shader and per-renderer values decide the rest, so 'could group' is an upper bound.",
            "A culling mode of Automatic is left to Unity (looping systems pause off screen); particle and physics work inside the engine is not timed per object.",
            "Lights are read after the game's light distance rule: lights off past their distance, shadows only near the player, and the caps above.",
            "Mods by prefab name: a mod's copies of the game's prefabs, and names two mods share, count for no mod.",
        };
    }
}
