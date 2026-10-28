using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// What a frame in the world counts as for the build tool: put away, out with its menu shut,
    /// or with the menu open. The frames in which the menu opens or closes are a change, counted
    /// with neither, since they carry the open or the close itself.
    /// </summary>
    public enum BuildState : byte { Away = 0, Tool = 1, Menu = 2, Change = 3 }

    public static class BuildTool
    {
        public static BuildState StateOf(bool placeMode, bool menuOpen, bool menuWasOpen)
        {
            if (menuOpen != menuWasOpen) return BuildState.Change;
            if (menuOpen) return BuildState.Menu;
            return placeMode ? BuildState.Tool : BuildState.Away;
        }

        /// <summary>The timeline's build column: "menu" when the menu was open in any frame, "tool" when the tool was out in most, else "".</summary>
        public static string SecondLabel(int toolFrames, int menuFrames, int frames)
        {
            if (frames <= 0) return "";
            if (menuFrames > 0) return "menu";
            return toolFrames * 2 > frames ? "tool" : "";
        }
    }

    /// <summary>
    /// Frames with the build tool out against frames with it put away: the mean frame on each
    /// side, and the timed work that grows most with the tool out, in ms per frame.
    /// </summary>
    public static class BuildCompare
    {
        public const double GrowsMs = 0.05;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        /// <summary>Frame counts and summed frame and drawing times on each side.</summary>
        public sealed class Frames
        {
            public long AwayCount, ToolCount;
            public double AwayMs, ToolMs, AwayRenderMs, ToolRenderMs;
        }

        /// <summary>One timed thing: its time summed over the frames on each side.</summary>
        public sealed class Item
        {
            public Item(string label, double awayMs, double toolMs)
            {
                Label = label;
                AwayMs = awayMs;
                ToolMs = toolMs;
            }

            public string Label { get; }
            public double AwayMs { get; }
            public double ToolMs { get; }
        }

        public static List<string> Lines(Frames f, IEnumerable<Item> items, int top, long minFrames)
        {
            var lines = new List<string>();
            if (f.AwayCount < minFrames || f.ToolCount < minFrames)
            {
                lines.Add(string.Format(Ci, "   too few frames to compare: put away {0:N0}, tool out {1:N0} (each side needs {2:N0})", f.AwayCount, f.ToolCount, minFrames));
                return lines;
            }
            double away = f.AwayMs / f.AwayCount, tool = f.ToolMs / f.ToolCount;
            lines.Add(string.Format(Ci, "   frame: put away {0:0.0} ms ({1:N0} frames), tool out {2:0.0} ms ({3:N0} frames), {4}{5:0.0} ms",
                away, f.AwayCount, tool, f.ToolCount, tool >= away ? "+" : "-", Math.Abs(tool - away)));
            lines.Add(string.Format(Ci, "   drawing: put away {0:0.0} ms, tool out {1:0.0} ms", f.AwayRenderMs / f.AwayCount, f.ToolRenderMs / f.ToolCount));
            var growing = items
                .Select(i => new { i.Label, Away = i.AwayMs / f.AwayCount, Tool = i.ToolMs / f.ToolCount })
                .Where(r => r.Tool - r.Away >= GrowsMs)
                .OrderByDescending(r => r.Tool - r.Away).ThenBy(r => r.Label, StringComparer.Ordinal)
                .Take(top)
                .ToList();
            if (growing.Count == 0)
            {
                lines.Add(string.Format(Ci, "   no timed work grows by {0:0.00} ms or more with the tool out", GrowsMs));
                return lines;
            }
            lines.Add("   timed work that grows most with the tool out, ms per frame (put away -> tool out):");
            foreach (var r in growing)
                lines.Add(string.Format(Ci, "      +{0:0.00} ms  {1}   {2:0.00} -> {3:0.00}", r.Tool - r.Away, r.Label, r.Away, r.Tool));
            return lines;
        }
    }

    /// <summary>
    /// One open of the build menu, timed step by step in its frame: the key's handler (the mods'
    /// hooks on it included), the menu's open, its tag buttons with the piece buttons inside them,
    /// each button's setup with its material check, and the screen layout. Every step is the
    /// time of that game method whole, so a step's own share is what is left after the steps
    /// inside it.
    /// </summary>
    public sealed class MenuOpen
    {
        public double FrameMs, ToggleMs, OpenMs, TagsMs, PiecesMs, SetupMs, ChecksMs, LayoutMs, NextLayoutMs, NavigationMs;
        public int Buttons;
        /// <summary>Buttons the menu kept as they were instead of setting them up again (the patch "The build menu opens faster"), 0 when rebuilt.</summary>
        public int Kept;
        /// <summary>Handlers on the game's language change, or -1 when not read.</summary>
        public int Handlers = -1;
        /// <summary>The navigation linked in the next frame (the patch "a frame later"), or -1 when it was not put off.</summary>
        public double LaterNavigationMs = -1;

        public string Describe()
        {
            var ci = CultureInfo.InvariantCulture;
            double outer = Math.Max(ToggleMs, OpenMs);
            var parts = new List<string>
            {
                Kept > 0
                    ? string.Format(ci, "{0:N0} piece buttons kept as they were, refreshed in {1:0} ms (material checks {2:0} ms)", Kept, PiecesMs, Math.Min(ChecksMs, PiecesMs))
                    : string.Format(ci, "{0:N0} piece buttons {1:0} ms (setting them up {2:0} ms, material checks in that {3:0} ms)", Buttons, PiecesMs, Math.Min(SetupMs, PiecesMs), Math.Min(ChecksMs, Math.Min(SetupMs, PiecesMs))),
                string.Format(ci, "tag buttons {0:0} ms", Pos(TagsMs - PiecesMs)),
                string.Format(ci, "the rest of the open {0:0} ms", Pos(OpenMs - Math.Max(TagsMs, PiecesMs))),
            };
            if (ToggleMs > 0) parts.Add(string.Format(ci, "mods' hooks around the key {0:0} ms", Pos(ToggleMs - OpenMs)));
            if (NavigationMs > 0) parts.Add(string.Format(ci, "navigation setup {0:0} ms in all", NavigationMs));
            parts.Add(string.Format(ci, "screen layout {0:0} ms", LayoutMs));
            parts.Add(string.Format(ci, "untimed {0:0} ms", Pos(FrameMs - outer - LayoutMs)));
            string s = string.Format(ci, "opened, frame {0:0} ms: ", FrameMs) + string.Join(", ", parts.ToArray()) +
                string.Format(ci, "; next frame's layout {0:0} ms", NextLayoutMs);
            if (LaterNavigationMs >= 0) s += string.Format(ci, ", the navigation set up there in {0:0.0} ms", LaterNavigationMs);
            if (Handlers >= 0) s += string.Format(ci, "; {0:N0} language handlers", Handlers);
            return s;
        }

        internal static double Pos(double v) => v > 0 ? v : 0;
    }

    /// <summary>
    /// One close of the build menu: by picking a piece (which sets up its placement ghost, unless
    /// the piece was already selected) or by the key or anything else, and what the frame after
    /// it spent its time on.
    /// </summary>
    public sealed class MenuClose
    {
        private const double NextItemMinMs = 1;
        private const int NextItems = 5;

        public double FrameMs, CloseMs, PickMs, GhostMs, LayoutMs, NextFrameMs;
        public bool Picked;
        /// <summary>The picked piece was the one already selected, so the game kept its placement ghost.</summary>
        public bool GhostKept;
        /// <summary>
        /// The click that picked the piece was handled at the start of the frame after this one,
        /// before Vigil's frame start, so its time belongs to that frame and is counted there.
        /// </summary>
        public bool PickRanNext;
        /// <summary>The frame after the close, or null when it was not read.</summary>
        public FrameBreakdown Next;
        /// <summary>What the screen layout rebuilt in the close's frame and in the frame after it, or null when not read.</summary>
        public UiRebuild Ui, NextUi;

        public string Describe()
        {
            var ci = CultureInfo.InvariantCulture;
            bool pickNext = Picked && PickRanNext;
            string ghost = GhostKept
                ? string.Format(ci, "picking it {0:0} ms (the piece already selected, its placement ghost kept), ", PickMs)
                : string.Format(ci, "picking it {0:0} ms (the placement ghost {1:0} ms), ", PickMs, Math.Min(GhostMs, PickMs));
            string s = !Picked
                ? string.Format(ci, "closed by the key or another way, frame {0:0} ms: ", FrameMs)
                : pickNext
                    ? string.Format(ci, "closed by picking a piece, its click handled at the start of the next frame, so the pick's time is that frame's; frame before it {0:0} ms: ", FrameMs) + ghost
                    : string.Format(ci, "closed by picking a piece, frame {0:0} ms: ", FrameMs) + ghost;
            s += string.Format(ci, pickNext ? "closing {0:0.0} ms, its screen layout {1:0} ms; next frame {2:0} ms" : "closing {0:0.0} ms, screen layout {1:0} ms; next frame {2:0} ms",
                CloseMs, LayoutMs, NextFrameMs);
            if (Next != null) s += ": " + string.Join(", ", NextParts(Next, pickNext ? PickMs : 0).ToArray());
            if (NextUi != null) s += "; the next frame's screen layout " + NextUi.Describe();
            if (Ui != null) s += "; the close frame's screen layout " + Ui.Describe();
            return s;
        }

        // pickMs: a pick handled at this frame's start, counted here as top-level timed work.
        private static List<string> NextParts(FrameBreakdown f, double pickMs)
        {
            var ci = CultureInfo.InvariantCulture;
            if (pickMs > 0)
            {
                var g = new FrameBreakdown { DtMs = f.DtMs, GcCollections = f.GcCollections, FixedSteps = f.FixedSteps, RenderMs = f.RenderMs, OwnMs = f.OwnMs, PhysicsTimed = f.PhysicsTimed, PhysicsMs = f.PhysicsMs };
                g.Phases.Add(new Timed("BuildUi.OnSelectPiece", (float)pickMs));
                g.Phases.AddRange(f.Phases);
                g.Mods.AddRange(f.Mods);
                f = g;
            }
            var parts = f.Phases.Concat(f.Mods).Where(t => t.Ms >= NextItemMinMs).OrderByDescending(t => t.Ms).Take(NextItems)
                .Select(t => string.Format(ci, "{0} {1:0} ms{2}{3}", t.Name, t.Ms, t.Calls > 1 ? string.Format(ci, " in {0} calls", t.Calls) : "",
                    t.Parent != null ? " inside " + t.Parent : ""))
                .ToList();
            if (f.RenderMs >= NextItemMinMs) parts.Add(string.Format(ci, "drawing {0:0} ms", f.RenderMs));
            if (f.PhysicsTimed && f.PhysicsMs >= NextItemMinMs) parts.Add(string.Format(ci, "physics engine {0:0} ms in {1} steps", f.PhysicsMs, f.FixedSteps));
            parts.Add(string.Format(ci, "untimed {0:0} ms", HitchAnalysis.Untimed(f)));
            if (f.GcCollections > 0) parts.Add(f.GcCollections == 1 ? "a garbage collection" : string.Format(ci, "{0} garbage collections", f.GcCollections));
            return parts;
        }
    }

    /// <summary>
    /// What the screen layout rebuilt in one frame: its time in Unity's UI and in TextMeshPro, the
    /// parts it rebuilt, and where they sit, given by the first names of their path from the scene root.
    /// </summary>
    public sealed class UiRebuild
    {
        private const int Places = 4;

        public int Passes;
        public double UnityMs, TextMs;
        private readonly Dictionary<string, int> _byPlace = new Dictionary<string, int>(StringComparer.Ordinal);
        private int _parts, _texts;

        /// <summary>Counts one part; <paramref name="kind"/> (its type) and <paramref name="hidden"/> (not shown on screen) tell parts of one place apart.</summary>
        public void Add(string place, bool text, string kind = null, bool hidden = false)
        {
            string key = kind == null ? place : place + " (" + kind + (hidden ? ", hidden" : "") + ")";
            _byPlace.TryGetValue(key, out int n);
            _byPlace[key] = n + 1;
            _parts++;
            if (text) _texts++;
        }

        public string Describe()
        {
            var ci = CultureInfo.InvariantCulture;
            string s = string.Format(ci, "{0:0} ms in {1} pass{2} (Unity UI {3:0} ms, TextMeshPro {4:0} ms), ",
                UnityMs + TextMs, Passes, Passes == 1 ? "" : "es", UnityMs, TextMs);
            if (_parts == 0) return s + "nothing rebuilt";
            var top = _byPlace.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
            var shown = top.Take(Places).Select(p => p.Key + " " + p.Value.ToString(ci)).ToList();
            int rest = top.Skip(Places).Sum(p => p.Value);
            if (rest > 0) shown.Add(rest.ToString(ci) + " elsewhere");
            return s + string.Format(ci, "{0:N0} parts rebuilt, {1:N0} of them texts: ", _parts, _texts) + string.Join(", ", shown.ToArray());
        }

        /// <summary>Where a part sits: the first <paramref name="depth"/> names of its path from the scene root.</summary>
        public static string Place(IList<string> namesFromRoot, int depth)
        {
            if (namesFromRoot == null || namesFromRoot.Count == 0) return "(no object)";
            return string.Join("/", namesFromRoot.Take(depth).ToArray());
        }
    }

    /// <summary>
    /// One change of the build menu's search text: the search's own time (its outermost call; a
    /// first letter typed on a tag switches to all pieces and rebuilds the buttons inside it), the
    /// buttons it left shown, and its navigation setup.
    /// </summary>
    public sealed class MenuSearch
    {
        public double FrameMs, SearchMs, PiecesMs, NavigationMs;
        public int Letters, Shown, Buttons;
        /// <summary>The navigation setup was put off to the next frame (the patch "a frame later").</summary>
        public bool NavigationLater;

        public string Describe()
        {
            var ci = CultureInfo.InvariantCulture;
            string what = Letters == 0 ? "search emptied" : string.Format(ci, "searched for {0} letter{1}", Letters, Letters == 1 ? "" : "s");
            string s = string.Format(ci, "{0}, frame {1:0} ms: the search {2:0} ms, {3:N0} of {4:N0} piece buttons shown", what, FrameMs, SearchMs, Shown, Buttons);
            if (PiecesMs > 0) s += string.Format(ci, ", the buttons rebuilt for it {0:0} ms", PiecesMs);
            s += NavigationLater ? ", navigation setup put off to the next frame" : string.Format(ci, ", navigation setup {0:0} ms in it", NavigationMs);
            return s;
        }
    }

    /// <summary>The menu's opens over a session: how many, the middle and the worst frame, the most piece buttons.</summary>
    public sealed class MenuTally
    {
        private readonly List<double> _frames = new List<double>();
        private int _buttons;

        public int Count => _frames.Count;

        public void Add(double frameMs, int buttons)
        {
            _frames.Add(frameMs);
            if (buttons > _buttons) _buttons = buttons;
        }

        public string Describe()
        {
            if (_frames.Count == 0) return "none";
            var sorted = _frames.OrderBy(v => v).ToList();
            return string.Format(CultureInfo.InvariantCulture, "{0} open{1}: middle {2:0} ms, worst {3:0} ms, up to {4:N0} piece buttons",
                sorted.Count, sorted.Count == 1 ? "" : "s", sorted[(sorted.Count - 1) / 2], sorted[sorted.Count - 1], _buttons);
        }
    }
}
