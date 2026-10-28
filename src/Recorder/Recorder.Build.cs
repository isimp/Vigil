using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Vigil.Core;

namespace Vigil.Recorder
{
    // The build tool: what a frame in the world counts as (put away, out, menu open), a line in
    // Vigil-events.tsv for every open and close of the build menu with its steps, the session
    // summary's comparison of tool out with put away, the same comparison in a deep capture, and
    // "vigil hammer", a deep capture that asks for the tool out and then put away.
    internal sealed partial class Recorder
    {
        // Frames each side needs before the summary or a deep capture compares them.
        private const long BuildMinFramesSession = 600, BuildMinFramesDeep = 300;

        private bool _menuWasOpen;
        private int _secToolFrames, _secMenuFrames;
        private readonly BuildCompare.Frames _buildRun = new BuildCompare.Frames();
        private BuildCompare.Frames _buildDeep = new BuildCompare.Frames(), _buildSplit = new BuildCompare.Frames();
        private readonly MenuTally _menuOpens = new MenuTally();
        private MenuOpen _pendingOpen;
        private MenuClose _pendingClose;
        private bool _hammerGuide, _hammerSwitched;
        private PhaseTimer.Slot _phToggle, _phOpen, _phTags, _phPieces, _phSetup, _phChecks, _phClose, _phPick, _phGhost, _phLayout, _phNav;
        private bool _buildSlotsRead;

        /// <summary>
        /// Called once per frame before the timers close it, with the frame's situation and length:
        /// says what the frame counts as, and notes an open or close of the build menu in it.
        /// </summary>
        private BuildState BuildFrame(byte situation, float dtMs)
        {
            FinishMenuLine(dtMs);
            if (situation != 0)
            {
                _menuWasOpen = false;
                return BuildState.Change;
            }
            bool place = false, menu = false;
            try
            {
                var player = Player.m_localPlayer;
                place = player != null && player.InPlaceMode();
                menu = global::Hud.IsPieceSelectionVisible();
            }
            catch (Exception) { }
            var state = BuildTool.StateOf(place, menu, _menuWasOpen);
            if (menu || _menuWasOpen) _secMenuFrames++;
            else if (place) _secToolFrames++;
            if (state == BuildState.Change) StartMenuLine(opened: menu, dtMs);
            _menuWasOpen = menu;

            float render = _engine != null ? _engine.LastRenderMs : 0f;
            Count(_buildRun, state, dtMs, render);
            if (_timer.DeepRunning)
            {
                Count(_buildDeep, state, dtMs, render);
                Count(_buildSplit, state, dtMs, render);
            }
            return state;
        }

        private static void Count(BuildCompare.Frames f, BuildState state, float dtMs, float renderMs)
        {
            if (state == BuildState.Away) { f.AwayCount++; f.AwayMs += dtMs; f.AwayRenderMs += renderMs; }
            else if (state == BuildState.Tool) { f.ToolCount++; f.ToolMs += dtMs; f.ToolRenderMs += renderMs; }
        }

        private void ReadBuildSlots()
        {
            if (_buildSlotsRead) return;
            _buildSlotsRead = true;
            _phToggle = _timer.Game("Hud.TogglePieceSelection");
            _phOpen = _timer.Game("BuildUi.OpenBuildMenu");
            _phTags = _timer.Game("BuildUi.UpdateTagButtons");
            _phPieces = _timer.Game("BuildUi.UpdatePieceButtons");
            _phSetup = _timer.Game("BuildUiPieceButton.Setup");
            _phChecks = _timer.Game("BuildUiPieceButton.UpdateRequirements");
            _phClose = _timer.Game("BuildUi.Close");
            _phPick = _timer.Game("BuildUi.OnSelectPiece");
            _phGhost = _timer.Game("Player.SetupPlacementGhost");
            _phLayout = _timer.Game("UnityEngine.Canvas.SendWillRenderCanvases");
            _phNav = _timer.Game("BuildUi.ConfigureButtonNavigation");
        }

        private static double FrameMs(PhaseTimer.Slot s) => s != null ? PhaseTimer.TicksToMs(s.FrameTicks) : 0;

        // The frame's steps are read now, before the timers close it; the line waits one frame for
        // the next frame's layout and length.
        private void StartMenuLine(bool opened, float dtMs)
        {
            ReadBuildSlots();
            if (opened)
            {
                _pendingOpen = new MenuOpen
                {
                    FrameMs = dtMs, ToggleMs = FrameMs(_phToggle), OpenMs = FrameMs(_phOpen), TagsMs = FrameMs(_phTags), PiecesMs = FrameMs(_phPieces),
                    SetupMs = FrameMs(_phSetup), ChecksMs = FrameMs(_phChecks), LayoutMs = FrameMs(_phLayout), Buttons = _phSetup != null ? _phSetup.FrameCalls : 0,
                    NavigationMs = FrameMs(_phNav),
                    // The open may fall in the frame before the one whose start reads it.
                    Kept = BuildMenuPatch.KeptFrame >= UnityEngine.Time.frameCount - 1 ? BuildMenuPatch.KeptButtons : 0,
                };
                _pendingClose = null;
            }
            else
            {
                _pendingClose = new MenuClose
                {
                    FrameMs = dtMs, CloseMs = FrameMs(_phClose), Picked = _phPick != null && _phPick.FrameCalls > 0,
                    PickMs = FrameMs(_phPick), GhostMs = FrameMs(_phGhost), LayoutMs = FrameMs(_phLayout),
                };
                _pendingOpen = null;
            }
        }

        private void FinishMenuLine(float dtMs)
        {
            if (_pendingOpen != null)
            {
                var open = _pendingOpen;
                _pendingOpen = null;
                open.NextLayoutMs = FrameMs(_phLayout);
                open.Handlers = LanguageHandlers();
                _menuOpens.Add(open.FrameMs, open.Buttons);
                EventLog.Write("build menu", "", open.Describe());
            }
            else if (_pendingClose != null)
            {
                var close = _pendingClose;
                _pendingClose = null;
                close.NextFrameMs = dtMs;
                EventLog.Write("build menu", "", close.Describe());
            }
        }

        // Each piece button the menu sets up adds itself to the game's language change and nothing
        // takes it off again, so the count shows whether that list keeps growing over a session.
        private static int LanguageHandlers()
        {
            try { return Localization.OnLanguageChange?.GetInvocationList().Length ?? 0; }
            catch (Exception) { return -1; }
        }

        /// <summary>The timeline's build column for the second just ended; starts the next second's count.</summary>
        private string BuildSecond()
        {
            string label = BuildTool.SecondLabel(_secToolFrames, _secMenuFrames, _secFrames);
            _secToolFrames = _secMenuFrames = 0;
            return label;
        }

        private static List<BuildCompare.Item> Items(IEnumerable<PhaseTimer.Slot> slots, bool deep) =>
            slots.Select(s => new BuildCompare.Item(s.Label,
                PhaseTimer.TicksToMs(deep ? s.DeepAwayTicks : s.RunAwayTicks), PhaseTimer.TicksToMs(deep ? s.DeepToolTicks : s.RunToolTicks))).ToList();

        /// <summary>The session summary's build tool section.</summary>
        private string BuildSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Build tool (frames in the world; the frames where its menu opens or closes count with neither side):");
            sb.AppendLine("   build menu: " + _menuOpens.Describe() + " (each open and close has a line in Vigil-events.tsv)");
            var timed = _timer.Slots.Where(s => !s.Deep && s.PatchKind == null && s.RunAwayTicks + s.RunToolTicks > 0);
            foreach (var line in BuildCompare.Lines(_buildRun, Items(timed, deep: false), 15, BuildMinFramesSession)) sb.AppendLine(line);
            sb.AppendLine("   These frames come from wherever you were at the time; vigil hammer compares at one spot, by mod.");
            return sb.ToString();
        }

        /// <summary>The deep capture's comparison of tool out with put away: whole game methods, then each mod's own patch code.</summary>
        private string BuildDeepSection()
        {
            var sb = new StringBuilder();
            sb.AppendLine("== Build tool out against put away, per frame (the menu's frames left out) ==");
            var patched = _timer.Slots.Where(s => s.PatchedBy != null && !s.Dropped && s.DeepAwayTicks + s.DeepToolTicks > 0);
            foreach (var line in BuildCompare.Lines(_buildDeep, Items(patched, deep: true), 25, BuildMinFramesDeep)) sb.AppendLine(line);
            var mods = _timer.Slots.Where(s => s.PatchKind != null && s.DeepAwayTicks + s.DeepToolTicks > 0)
                .GroupBy(s => s.Mod)
                .Select(g => new BuildCompare.Item(g.Key + " (own patch code)", PhaseTimer.TicksToMs(g.Sum(s => s.DeepAwayTicks)), PhaseTimer.TicksToMs(g.Sum(s => s.DeepToolTicks))));
            var callbacks = _timer.Slots.Where(s => s.Mod != null && s.PatchKind == null && s.DeepAwayTicks + s.DeepToolTicks > 0);
            sb.AppendLine("   Each mod's own patch code on the split methods (from when the split went on, timers' own cost left in) and mods' own updates:");
            foreach (var line in BuildCompare.Lines(_buildSplit, mods.Concat(Items(callbacks, deep: true)).ToList(), 20, BuildMinFramesDeep)) sb.AppendLine("   " + line);
            return sb.ToString();
        }

        /// <summary>"vigil hammer": a deep capture of the tool out, then put away.</summary>
        public string StartHammer()
        {
            if (_server) return "the hammer comparison needs a player's game";
            float secs = 2 * ConsoleHelp.HammerSeconds;
            string r = StartDeep(secs, "for the hammer comparison");
            if (_timer.DeepActive && r.StartsWith("deep capture:", StringComparison.Ordinal))
            {
                _hammerGuide = true;
                _hammerSwitched = false;
                Hud("Vigil: hammer comparison getting ready; take out your build tool, menu shut, and stand still");
                return r + ". Hold the build tool out (menu shut) until the screen says to put it away.";
            }
            return r;
        }

        private string DeepMeasuringText(float seconds) => _hammerGuide
            ? string.Format(CultureInfo.InvariantCulture, "Vigil: measuring; keep the build tool out, menu shut, for {0} s", ConsoleHelp.HammerSeconds)
            : string.Format(CultureInfo.InvariantCulture, "Vigil: deep capture measuring for {0:0} s", seconds);

        // Halfway through the hammer comparison: the second half is with the tool put away.
        private void GuideHammer(float now)
        {
            if (!_hammerGuide || _hammerSwitched || !_timer.DeepRunning || _deepRunStarted <= 0) return;
            if (now - _deepRunStarted < ConsoleHelp.HammerSeconds) return;
            _hammerSwitched = true;
            Hud(string.Format(CultureInfo.InvariantCulture, "Vigil: now put the build tool away and stand still for {0} s", ConsoleHelp.HammerSeconds));
        }

        private void OnDeepMeasuring()
        {
            _buildDeep = new BuildCompare.Frames();
            _buildSplit = new BuildCompare.Frames();
        }

        private void OnDeepSplit() => _buildSplit = new BuildCompare.Frames();
    }
}
