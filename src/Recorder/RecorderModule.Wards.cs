using System;
using System.Collections.Generic;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>The Patches tab's Protective Wards sections and their self-test lines.</summary>
    internal sealed partial class RecorderModule
    {
        private void DrawWardPatches(bool may)
        {
            Ui.Heading("Wards repair the whole connected base");
            SwitchLine(may, _wardNetwork, "WardNetworkRepair");
            Ui.Note("Starting repair at a ward (Protective Wards' alt-use) starts it on every active ward chained to it by overlapping areas, and each ward repairs its own area, one damaged piece at a time, until none is left. Every player sees how many pieces are left on each ward. A ward's repair runs on the game that owns it, so it goes on while any player is near the base; a ward owned by a game without Vigil is left out.");

            Ui.Heading("Wards repair by themselves");
            SwitchLine(may, _wardAuto, "WardAutoRepair");
            if (may)
            {
                if (Ui.Tool("Seconds per piece", "For both ward repairs, " + WardRepair.MinSeconds + " to " + WardRepair.MaxSeconds, "Set", ref _wardSecondsText, 40, "s"))
                {
                    string text = _wardSecondsText;
                    Ui.Later(() =>
                    {
                        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, Ic, out int seconds)) return;
                        _wardSeconds.Value = WardRepair.ClampSeconds(seconds);
                        EventLog.Write("patch-set", PlayerName(), "WardRepairSeconds " + _wardSeconds.Value.ToString(Ic));
                    });
                }
            }
            else Ui.Row("Seconds per piece", _wardSeconds.Value.ToString(Ic) + " s");
            Ui.Note("Every active ward repairs its own area by itself, one damaged piece at a time, by Protective Wards' own repair rules (structures not placed by players, a crafting station near). The dedicated server loads no bases, so a ward repairs only while a player is near it.");
            Ui.Row("Set by", "the server, for every player running Vigil (Patches.WardNetworkRepair, Patches.WardAutoRepair, Patches.WardRepairSeconds)");
            Ui.Row("On this game", Toned(WardRepairPatch.State + (WardRepairPatch.Repaired > 0 ? " · " + WardRepairPatch.Repaired + " pieces repaired here" : ""),
                WardRepairPatch.Problem != null ? ResultTone.Caution : (_wardNetwork.Value || _wardAuto.Value) && WardRepairPatch.Found ? ResultTone.Good : ResultTone.Quiet));

            Ui.Heading("Server password for wards");
            Ui.Row("Password", _server
                ? (WardPasswordPatch.Password.Length > 0 ? "set in this server's config" : "none: nobody is asked")
                : (WardPasswordPatch.ServerHasOne ? "the server has one" : "the server has none: nobody is asked"));
            Ui.Note("A player without access to an active ward is asked for a password at it (Protective Wards' own prompt); the server's password adds them to that ward's permitted list, as a ward's own password would, and with connected access that opens its whole base. A ward with its own password still takes that one; a switched-off ward keeps Protective Wards' own way in. The password is set only in the dedicated server's config (Patches.WardPassword), never here and never on a player's game: Gale copies a profile's configs to everyone who uses it.");
            Ui.Row("On this game", Toned(WardPasswordPatch.State + (_server && WardPasswordPatch.LetIn > 0 ? " · " + WardPasswordPatch.LetIn + " let in since the start" : ""),
                WardPasswordPatch.Problem != null && WardPasswordPatch.ServerHasOne ? ResultTone.Caution : WardPasswordPatch.ServerHasOne && WardPasswordPatch.Found ? ResultTone.Good : ResultTone.Quiet));
        }

        private void SwitchLine(bool may, BepInEx.Configuration.ConfigEntry<bool> entry, string name)
        {
            Ui.BeginFormLine("Switch");
            if (may)
            {
                int picked = Ui.Choice(entry.Value ? 0 : 1, OnOffWords);
                if (picked >= 0 && (picked == 0) != entry.Value)
                {
                    bool on = picked == 0;
                    Ui.Later(() =>
                    {
                        entry.Value = on;
                        EventLog.Write("patch-set", PlayerName(), name + " " + (on ? "on" : "off"));
                    });
                }
            }
            else Ui.Text(entry.Value ? "On" : "Off");
            Ui.EndLine();
        }

        private static void WardChecks(string area, List<SelfTestReport.Check> checks)
        {
            Add(checks, area, "game patch: wards repair the whole connected base, or by themselves", WardRepairPatch.State);
            Add(checks, area, "game patch: server password for wards", WardPasswordPatch.State);
        }

        private static void Add(List<SelfTestReport.Check> checks, string area, string what, string state) =>
            checks.Add(new SelfTestReport.Check(area, what,
                state.StartsWith("working", StringComparison.Ordinal) ? SelfTestReport.Result.Pass
                : state.StartsWith("stepped aside", StringComparison.Ordinal) ? SelfTestReport.Result.Warn
                : SelfTestReport.Result.Info, state));
    }
}
