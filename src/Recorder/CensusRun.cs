using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Vigil.Core;
using Object = UnityEngine.Object;

namespace Vigil.Recorder
{
    /// <summary>
    /// "vigil census" as a run over several frames, so reading thousands of objects stalls no frame:
    /// it reads the loaded world objects around the player a slice per frame (renderers, particle
    /// systems, lights, characters, moving objects), then the renderers that belong to no world object,
    /// then times every character it found for a few seconds with timers of its own on the game's
    /// per-character methods, and writes <c>Vigil-census-*.txt</c> (<see cref="BaseCensus"/>) plus the
    /// per-type and per-mod tables of <see cref="Census"/>. Only reads; the timers come off again.
    /// <para>
    /// Timed per character: <c>Character.CustomFixedUpdate</c> (or <c>Humanoid.CustomFixedUpdate</c>, which
    /// runs it inside), the character's <c>ZSyncTransform.CustomFixedUpdate</c> and an animal's
    /// <c>AnimalAI.UpdateAI</c>, against <c>MonoUpdaters.FixedUpdate</c>, the game's loop over all of them.
    /// <c>MonsterAI.UpdateAI</c> is left out: another mod rewrites it with a transpiler, and putting a
    /// timer on it during play would build it again through that transpiler.
    /// </para>
    /// </summary>
    internal sealed class CensusRun
    {
        public const float TimingSeconds = 5f;
        private const double BudgetMs = 4.0;
        private const float ShadowMargin = 20f;

        private enum Stage { Walking, Apart, Hooking, Timing, Unhooking, Done }

        private const int CharacterTimer = 0, HumanoidTimer = 1, SyncTimer = 2, AnimalTimer = 3, LoopTimer = 4;

        private static readonly (Type Type, string Method)[] Timers =
        {
            (typeof(Character), "CustomFixedUpdate"),
            (typeof(Humanoid), "CustomFixedUpdate"),
            (typeof(ZSyncTransform), "CustomFixedUpdate"),
            (typeof(AnimalAI), "UpdateAI"),
            (typeof(MonoUpdaters), "FixedUpdate"),
        };

        private static CensusRun s_current;

        /// <summary>True while a census runs; its timers are shared, so only one runs at a time.</summary>
        public static bool Running => s_current != null && !s_current.Done;

        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> s_instances =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");

        // The census timers' sums, by timer and by instance; only while a window is open.
        private static readonly Dictionary<MethodBase, int> s_timerOf = new Dictionary<MethodBase, int>();
        private static readonly Dictionary<object, long>[] s_ticks = Enumerable.Range(0, Timers.Length).Select(_ => new Dictionary<object, long>()).ToArray();
        private static readonly long[] s_total = new long[Timers.Length];
        private static long s_steps;
        private static bool s_counting;

        private sealed class Seen
        {
            public Character Character;
            public ZSyncTransform Sync;
            public AnimalAI Animal;
            public CharacterFacts Facts;
            public MoverFacts Mover;
        }

        private readonly string _dir;
        private readonly float _radius, _timingSeconds;
        private readonly InstrumentJournal _journal;
        private readonly Harmony _harmony = new Harmony("isimp.Vigil.census");
        private readonly Vector3? _center;
        private readonly Vector3 _camera;
        private readonly Plane[] _frustum;
        private readonly BundleIndex _index;
        private readonly List<ZNetView> _views;
        private Renderer[] _all;
        private int _next;
        private Stage _stage;

        private readonly Census _old = new Census();
        private readonly CopySuspects _suspects = new CopySuspects();
        private readonly BaseCensus _census = new BaseCensus();
        private readonly CensusScene _scene;
        private readonly HashSet<int> _counted = new HashSet<int>();
        private readonly List<Vector3> _casters = new List<Vector3>();
        private readonly List<(LightFacts Facts, Vector3 At)> _lights = new List<(LightFacts, Vector3)>();
        private readonly List<Seen> _moving = new List<Seen>();
        private readonly string[] _layerNames = new string[32];

        private readonly List<Renderer> _rs = new List<Renderer>();
        private readonly List<Material> _mats = new List<Material>();
        private readonly List<ParticleSystem> _ps = new List<ParticleSystem>();
        private readonly List<Light> _ls = new List<Light>();
        private readonly List<AudioSource> _as = new List<AudioSource>();
        private readonly RendererFacts _rf = new RendererFacts();
        private readonly StringBuilder _key = new StringBuilder();

        private readonly Queue<MethodBase> _toHook = new Queue<MethodBase>(), _toUnhook = new Queue<MethodBase>();
        private readonly Stopwatch _wall = Stopwatch.StartNew();
        private float _timingFrom;
        private long _physicsTicks, _physicsSteps;
        private double _ownMs;
        private readonly Stopwatch _step = new Stopwatch();

        public CensusRun(string dir, float radius, float timingSeconds, InstrumentJournal journal, IDictionary<string, double> engine)
        {
            _dir = dir;
            _radius = Math.Max(0f, radius);
            _timingSeconds = timingSeconds;
            _journal = journal;
            s_current = this;
            _center = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : (Vector3?)null;
            var cam = Utils.GetMainCamera();
            _camera = cam != null ? cam.transform.position : _center ?? Vector3.zero;
            _frustum = cam != null ? GeometryUtility.CalculateFrustumPlanes(cam) : null;
            _index = ModAssets.Index(out _);
            _views = s_instances(ZNetScene.instance).Values.Where(v => v != null).ToList();
            for (int i = 0; i < 32; i++) { string n = LayerMask.LayerToName(i); _layerNames[i] = string.IsNullOrEmpty(n) ? "layer " + i.ToString(CultureInfo.InvariantCulture) : n; }
            _scene = new CensusScene
            {
                At = DateTime.Now,
                Radius = _center.HasValue ? _radius : 0,
                Place = _center.HasValue ? Heightmap.FindBiome(_center.Value) + ", " + SpawnShare.FromStart(_center.Value.x, _center.Value.z) : null,
                ShadowDistance = QualitySettings.shadowDistance,
                Cascades = QualitySettings.shadowCascades,
                PixelLights = QualitySettings.pixelLightCount,
                PointLightCap = LightLod.m_lightLimit,
                PointShadowCap = LightLod.m_shadowLimit,
                SunLayerDistances = SunDistances(),
                DrawCalls = Counter(engine, "draw_calls"),
                Batches = Counter(engine, "batches"),
                ShadowCasters = Counter(engine, "shadow_casters"),
            };
        }

        public bool Done => _stage == Stage.Done;

        /// <summary>The finished census's panel result and console answer; null until <see cref="Done"/>.</summary>
        public Result Result { get; private set; }
        public string Answer { get; private set; }

        /// <summary>How far the run is, for the panel and the console.</summary>
        public string Progress
        {
            get
            {
                var ci = CultureInfo.InvariantCulture;
                switch (_stage)
                {
                    case Stage.Walking: return string.Format(ci, "reading objects, {0} of {1}", _next, _views.Count);
                    case Stage.Apart: return "reading the renderers of no world object";
                    case Stage.Hooking: return "putting the character timers in";
                    case Stage.Timing: return string.Format(ci, "timing {0} characters and moving objects, {1:0} s left", _moving.Count, Math.Max(0, _timingFrom + _timingSeconds - Time.realtimeSinceStartup));
                    case Stage.Unhooking: return "taking the character timers out";
                    default: return "done";
                }
            }
        }

        private static int Counter(IDictionary<string, double> engine, string column) =>
            engine != null && engine.TryGetValue(column, out double v) && !double.IsNaN(v) ? (int)v : -1;

        private static string SunDistances()
        {
            var sun = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
            var d = sun != null ? sun.layerShadowCullDistances : null;
            if (d == null || d.All(x => x <= 0)) return null;
            var parts = new List<string>();
            for (int i = 0; i < d.Length; i++)
                if (d[i] > 0) parts.Add(LayerMask.LayerToName(i) + " " + d[i].ToString("0", CultureInfo.InvariantCulture));
            return string.Join(", ", parts) + " m";
        }

        /// <summary>One frame's share of the work, within the frame budget; gives the time it took.</summary>
        public double Step()
        {
            var sw = _step;
            sw.Restart();
            // Waiting out the timing window is no work; a frame that ends in the report counts itself there.
            bool counts = _stage != Stage.Timing;
            try
            {
                switch (_stage)
                {
                    case Stage.Walking:
                        while (_next < _views.Count && sw.Elapsed.TotalMilliseconds < BudgetMs)
                        {
                            try { ReadObject(_views[_next]); }
                            catch (Exception) { _scene.Unreadable++; }
                            _next++;
                        }
                        if (_next >= _views.Count) { _next = 0; _stage = Stage.Apart; }
                        break;
                    case Stage.Apart:
                        // The scene-wide list in a frame of its own.
                        if (_all == null) { _all = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None); break; }
                        while (_next < _all.Length && sw.Elapsed.TotalMilliseconds < BudgetMs)
                        {
                            try { ReadApart(_all[_next]); }
                            catch (Exception) { _scene.Unreadable++; }
                            _next++;
                        }
                        if (_next >= _all.Length) { _all = null; EndWalk(); }
                        break;
                    case Stage.Hooking:
                        if (_toHook.Count > 0) Hook(_toHook.Dequeue());
                        if (_toHook.Count == 0 && _stage == Stage.Hooking) OpenWindow();
                        break;
                    case Stage.Timing:
                        if (Time.realtimeSinceStartup - _timingFrom >= _timingSeconds) CloseWindow();
                        break;
                    case Stage.Unhooking:
                        if (_toUnhook.Count > 0)
                        {
                            var m = _toUnhook.Dequeue();
                            try { _harmony.Unpatch(m, HarmonyPatchType.All, _harmony.Id); } catch (Exception) { }
                        }
                        if (_toUnhook.Count == 0) Finish();
                        break;
                }
            }
            catch (Exception e)
            {
                Stop(e.GetType().Name + " " + e.Message);
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            sw.Stop();
            if (counts && _stage != Stage.Done)
            {
                _ownMs += ms;
                _scene.Frames++;
                _scene.MaxFrameMs = Math.Max(_scene.MaxFrameMs, ms);
            }
            return ms;
        }

        /// <summary>Ends the run at once: the timers come off and nothing is written.</summary>
        public void Stop(string why)
        {
            s_counting = false;
            UnhookAll();
            Answer = "census stopped: " + why;
            _stage = Stage.Done;
        }

        // ------------------------------------------------------------------ the walk

        private string LayerOf(GameObject go) => _layerNames[go.layer & 31];

        private void ReadObject(ZNetView view)
        {
            if (view == null) return;
            var go = view.gameObject;
            var at = go.transform.position;
            if (!InRadius(at))
            {
                // Past the radius only its shadow casters count, out to the shadow distance from the camera;
                // a large object's parts can lie nearer than its root, hence the margin.
                float reach = _scene.ShadowDistance + ShadowMargin;
                if ((at - _camera).sqrMagnitude <= reach * reach) ReadShadows(go);
                return;
            }
            string prefab = Utils.GetPrefabName(go);
            string mod = _index.OwnerOf(prefab);
            bool onPiece = go.GetComponent<Piece>() != null;

            int renderers = 0, shadows = 0, copied = 0;
            go.GetComponentsInChildren(false, _rs);
            foreach (var r in _rs)
            {
                if (r == null || !r.enabled) continue;
                _counted.Add(r.GetInstanceID());
                r.GetSharedMaterials(_mats);
                bool copy = HasCopy(_mats);
                bool shadow = r.shadowCastingMode != ShadowCastingMode.Off;
                renderers++;
                if (shadow) shadows++;
                if (copy) copied++;
                Fill(r, prefab, mod, shadow, copy, worldObject: true);
                _census.AddRenderer(_rf);
                if (shadow) _casters.Add(r.bounds.center);
            }

            go.GetComponentsInChildren(false, _ls);
            int lights = 0;
            foreach (var l in _ls)
            {
                if (l == null) continue;
                if (l.enabled) lights++;
                var lod = l.GetComponent<LightLod>();
                _lights.Add((new LightFacts
                {
                    Prefab = prefab, Mod = mod, Type = l.type.ToString(), On = l.enabled, Range = l.range,
                    UnderLightRule = lod != null && lod.enabled && lod.m_lightLod,
                    Shadows = l.shadows == LightShadows.Soft ? LightShadow.Soft : l.shadows == LightShadows.Hard ? LightShadow.Hard : LightShadow.None,
                }, l.transform.position));
            }

            go.GetComponentsInChildren(false, _ps);
            int particles = 0;
            foreach (var p in _ps)
            {
                if (p == null) continue;
                if (p.isPlaying) particles++;
                var pr = p.GetComponent<ParticleSystemRenderer>();
                bool shown = pr != null && pr.enabled;
                _census.AddParticles(new ParticleFacts
                {
                    Prefab = prefab, Mod = mod, OnPiece = onPiece, Playing = p.isPlaying, Particles = p.particleCount,
                    Mode = Culling(p.main.cullingMode),
                    InView = shown && _frustum != null && GeometryUtility.TestPlanesAABB(_frustum, pr.bounds),
                    UnitySees = shown && pr.isVisible,
                });
            }

            go.GetComponentsInChildren(false, _as);
            int audio = _as.Count(a => a != null && a.isPlaying);
            _old.Add(prefab, renderers, shadows, lights, particles, audio, copied, mod);
            // inactive parts too (a piece's worn and broken models), for which code made the copies
            if (renderers > 0)
                _suspects.Add(go.GetComponentsInChildren<Renderer>(true).Any(HasCopy),
                    go.GetComponentsInChildren<MonoBehaviour>(true).Where(b => b != null).Select(b => b.GetType().FullName));

            var character = go.GetComponent<Character>();
            var sync = go.GetComponent<ZSyncTransform>();
            var body = go.GetComponent<Rigidbody>();
            if (character != null)
            {
                var ai = character.GetBaseAI();
                var facts = new CharacterFacts
                {
                    Prefab = prefab, Mod = mod, Yours = view.IsOwner(), Tamed = character.IsTamed(), Player = character.IsPlayer(),
                    AiAsleep = ai != null && ai.IsSleeping(), Body = BodyOf(body),
                };
                _moving.Add(new Seen { Character = character, Sync = sync, Animal = ai as AnimalAI, Facts = facts });
            }
            else if (sync != null)
            {
                _moving.Add(new Seen { Sync = sync, Mover = new MoverFacts { Prefab = prefab, Mod = mod, Yours = view.IsOwner(), Body = BodyOf(body) } });
            }
        }

        private bool InRadius(Vector3 at) => !_center.HasValue || _radius <= 0 || (at - _center.Value).sqrMagnitude <= _radius * _radius;

        /// <summary>A world object past the radius: its shadow casters within the shadow distance, and nothing else of it.</summary>
        private void ReadShadows(GameObject go)
        {
            go.GetComponentsInChildren(false, _rs);
            foreach (var r in _rs)
            {
                if (r == null || !r.enabled || r.shadowCastingMode == ShadowCastingMode.Off) continue;
                var centre = r.bounds.center;
                float distance = Vector3.Distance(_camera, centre);
                if (distance > _scene.ShadowDistance) continue;
                _counted.Add(r.GetInstanceID());
                _rf.Prefab = null;
                _rf.Mod = null;
                _rf.Layer = LayerOf(r.gameObject);
                _rf.Shadows = true;
                _rf.Distance = distance;
                _rf.WorldObject = true;
                _rf.ShadowOnly = true;
                _rf.Batch = null;
                _census.AddRenderer(_rf);
                _casters.Add(centre);
            }
        }

        private void ReadApart(Renderer r)
        {
            if (r == null || !r.enabled || _counted.Contains(r.GetInstanceID())) return;
            var b = r.bounds;
            bool shadow = r.shadowCastingMode != ShadowCastingMode.Off;
            bool inRadius = !_center.HasValue || _radius <= 0 || b.SqrDistance(_center.Value) <= _radius * _radius;
            if (!inRadius && !(shadow && Vector3.Distance(_camera, b.center) <= _scene.ShadowDistance)) return;
            // A world object's renderer: the walk took it or left it out with its object.
            if (r.GetComponentInParent<ZNetView>() != null) return;
            r.GetSharedMaterials(_mats);
            Fill(r, null, null, shadow, false, worldObject: false);
            _rf.ShadowOnly = !inRadius;
            _census.AddRenderer(_rf);
            if (shadow) _casters.Add(b.center);
        }

        private void Fill(Renderer r, string prefab, string mod, bool shadow, bool copy, bool worldObject)
        {
            _rf.Prefab = prefab;
            _rf.Mod = mod;
            _rf.Layer = LayerOf(r.gameObject);
            _rf.Shadows = shadow;
            _rf.Distance = Vector3.Distance(_camera, r.bounds.center);
            _rf.Drawn = r.isVisible;
            _rf.Copy = copy;
            _rf.WorldObject = worldObject;
            _rf.ShadowOnly = false;
            _rf.Batch = null;
            if (r is SkinnedMeshRenderer) { _rf.Instancing = Instancing.Skinned; return; }
            int n = 0, on = 0;
            foreach (var m in _mats)
            {
                if (m == null) continue;
                n++;
                if (m.enableInstancing) on++;
            }
            _rf.Instancing = n > 0 && on == n ? Instancing.On : on > 0 ? Instancing.Some : Instancing.Off;
            if (!worldObject || !(r is MeshRenderer)) return;
            var mesh = r.GetComponent<MeshFilter>();
            if (mesh == null || mesh.sharedMesh == null) return;
            _key.Length = 0;
            _key.Append(mesh.sharedMesh.GetInstanceID());
            foreach (var m in _mats) _key.Append(':').Append(m != null ? m.GetInstanceID() : 0);
            _rf.Batch = _key.ToString();
        }

        // A material made for this renderer alone: Unity names it "<material> (Instance)". Reading
        // shared materials makes no copies.
        private static bool HasCopy(List<Material> mats)
        {
            foreach (var m in mats)
                if (m != null && m.name.EndsWith(" (Instance)", StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool HasCopy(Renderer r) => r.sharedMaterials.Any(m => m != null && m.name.EndsWith(" (Instance)", StringComparison.Ordinal));

        private static ParticleCulling Culling(ParticleSystemCullingMode mode) =>
            Enum.TryParse(mode.ToString(), out ParticleCulling c) ? c : ParticleCulling.Automatic;

        private static Body BodyOf(Rigidbody body) =>
            body == null ? Body.None : body.isKinematic ? Body.Kinematic : body.IsSleeping() ? Body.Asleep : Body.Awake;

        private void EndWalk()
        {
            foreach (var (facts, at) in _lights)
            {
                if (facts.On && facts.Shadows != LightShadow.None)
                {
                    float r2 = facts.Range * facts.Range;
                    facts.CastersInRange = _casters.Count(c => (c - at).sqrMagnitude <= r2);
                }
                _census.AddLight(facts);
            }
            _scene.WalkSeconds = _wall.Elapsed.TotalSeconds;
            if (_moving.Count == 0 || _timingSeconds <= 0)
            {
                _scene.TimingProblem = _moving.Count == 0 ? "nothing moving was loaded" : "timing was not asked for";
                Finish();
                return;
            }
            foreach (var (type, method) in Timers)
            {
                var m = AccessTools.DeclaredMethod(type, method);
                if (m == null) { _scene.TimingProblem = "the game has no " + type.Name + "." + method; Finish(); return; }
                _toHook.Enqueue(m);
            }
            _stage = Stage.Hooking;
        }

        // ------------------------------------------------------------------ the character timers

        private void Hook(MethodBase m)
        {
            string id = "census timer " + m.DeclaringType?.Name + "." + m.Name;
            if (!_journal.Allowed(id)) { Abandon("the timer on " + m.DeclaringType?.Name + "." + m.Name + " crashed the game once and is skipped"); return; }
            _journal.Begin(id);
            try
            {
                s_timerOf[m] = Array.FindIndex(Timers, t => t.Type == m.DeclaringType && t.Method == m.Name);
                _harmony.Patch(m, prefix: new HarmonyMethod(typeof(CensusRun), nameof(Pre)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(CensusRun), nameof(Fin)) { priority = Priority.Last });
                _toUnhook.Enqueue(m);
            }
            catch (Exception e) { Abandon("a timer could not be put in (" + e.GetType().Name + ")"); }
            finally { _journal.Done(id); }
        }

        private void Abandon(string why)
        {
            _scene.TimingProblem = why;
            _toHook.Clear();
            _stage = Stage.Unhooking;
            if (_toUnhook.Count == 0) Finish();
        }

        private void OpenWindow()
        {
            foreach (var d in s_ticks) d.Clear();
            Array.Clear(s_total, 0, s_total.Length);
            s_steps = 0;
            (_physicsTicks, _physicsSteps) = PhysicsStep.Totals;
            _timingFrom = Time.realtimeSinceStartup;
            s_counting = true;
            _stage = Stage.Timing;
        }

        private void CloseWindow()
        {
            s_counting = false;
            _scene.TimedSeconds = Time.realtimeSinceStartup - _timingFrom;
            _scene.Steps = s_steps;
            _scene.FixedUpdateMs = PhaseTimer.TicksToMs(s_total[LoopTimer]);
            var (ticks, steps) = PhysicsStep.Totals;
            _scene.PhysicsEngineMs = PhysicsStep.Installed && steps > _physicsSteps ? PhaseTimer.TicksToMs(ticks - _physicsTicks) : double.NaN;
            if (s_steps == 0) _scene.TimingProblem = "no physics step ran while the timers were in";
            foreach (var s in _moving)
            {
                long t = Ticks(SyncTimer, s.Sync);
                if (s.Character != null)
                {
                    t += Ticks(s.Character is Humanoid ? HumanoidTimer : CharacterTimer, s.Character) + Ticks(AnimalTimer, s.Animal);
                    s.Facts.Ms = PhaseTimer.TicksToMs(t);
                    CharactersMs += s.Facts.Ms;
                    if (t > 0) CharactersTimed++;
                }
                else
                {
                    s.Mover.Ms = PhaseTimer.TicksToMs(t);
                    MoversMs += s.Mover.Ms;
                }
            }
            _stage = Stage.Unhooking;
        }

        /// <summary>The timed characters' and moving objects' time in the window, and how many characters ran at all.</summary>
        public double CharactersMs { get; private set; }
        public double MoversMs { get; private set; }
        public int CharactersTimed { get; private set; }
        public int Characters => _census.CharacterCount;

        private static long Ticks(int timer, object instance) =>
            instance != null && s_ticks[timer].TryGetValue(instance, out long t) ? t : 0;

        private void UnhookAll()
        {
            while (_toUnhook.Count > 0)
            {
                var m = _toUnhook.Dequeue();
                try { _harmony.Unpatch(m, HarmonyPatchType.All, _harmony.Id); } catch (Exception) { }
            }
        }

        private static void Pre(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void Fin(MethodBase __originalMethod, object __instance, long __state)
        {
            if (!s_counting || !s_timerOf.TryGetValue(__originalMethod, out int timer) || timer < 0) return;
            long dt = Stopwatch.GetTimestamp() - __state;
            s_total[timer] += dt;
            if (timer == LoopTimer) { s_steps++; return; }
            if (__instance == null) return;
            var d = s_ticks[timer];
            d[__instance] = (d.TryGetValue(__instance, out long t) ? t : 0) + dt;
        }

        // ------------------------------------------------------------------ the report

        private void Finish()
        {
            foreach (var s in _moving)
            {
                if (s.Character != null) _census.AddCharacter(s.Facts);
                else _census.AddMover(s.Mover);
            }
            foreach (var d in s_ticks) d.Clear();
            _moving.Clear();
            if (_scene.WalkSeconds <= 0) _scene.WalkSeconds = _wall.Elapsed.TotalSeconds;

            string stamp = _scene.At.ToString("HHmmss", CultureInfo.InvariantCulture);
            string file = "Vigil-census-" + stamp + ".txt", types = "Vigil-census-" + stamp + ".tsv", mods = "Vigil-census-" + stamp + "-mods.tsv";
            File.WriteAllText(Path.Combine(_dir, types), _old.Format());
            File.WriteAllText(Path.Combine(_dir, mods), _old.FormatByOwner());
            // This frame's work so far counts too: it is the census's own as well.
            double now = _step.IsRunning ? _step.Elapsed.TotalMilliseconds : 0;
            _scene.OwnMs = _ownMs + now;
            _scene.Frames++;
            _scene.MaxFrameMs = Math.Max(_scene.MaxFrameMs, now);
            File.WriteAllText(Path.Combine(_dir, file), _census.Format(_scene, _suspects.Report(25)));

            var result = InspectResult.Census(DateTime.Now, _old, _scene.Radius, _census.Findings(_scene));
            result.File = file;
            result.Path = Path.Combine(_dir, file);
            Result = result;
            Answer = "census of what is loaded " + (_scene.Radius > 0 ? "within " + _scene.Radius.ToString("0", CultureInfo.InvariantCulture) + " m" : "anywhere")
                + " written to " + file + " (tables " + types + ", " + mods + ")";
            _stage = Stage.Done;
        }

        /// <summary>What the finished run found, for the self-test: characters timed, the timers' sum against the game's loop, the scene.</summary>
        public CensusScene Scene => _scene;

        public BaseCensus Counts => _census;

        /// <summary>True while any of the census's timers is still in a game method.</summary>
        public static bool AnyTimerIn() =>
            Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)?.Owners.Contains("isimp.Vigil.census") == true);
    }
}
