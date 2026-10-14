using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Cleanup;

/// <summary>
/// The cleanup: "vigil missing [radius] [hash]" lists world objects near the player whose prefab
/// no installed mod provides (left behind by a removed mod; the game warns "Missing prefab hash"
/// about each of them over and over). "vigil missing remove [radius] [hash]" removes them: this
/// game takes ownership and destroys them the way the game destroys any object, and the server
/// drops them from the world. Nothing happens without the word "remove"; in the panel the remove
/// button asks twice. Runs on a player's machine only.
/// </summary>
internal sealed class CleanupModule : Module
{
    private const int MaxListed = 25;

    private static readonly char[] Space = { ' ' };

    private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> Objects =
        AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

    private readonly Confirm _confirm = new(5);
    private VigilPlugin _host;
    private string _radiusText = ConsoleHelp.MissingRadius.ToString(CultureInfo.InvariantCulture);
    private Found _shown;

    public override string Area => ConsoleHelp.Cleanup;

    public override string SwitchDescription =>
        "The cleanup: vigil missing lists, and on request removes, world objects whose prefab no installed mod provides. Off: the command does nothing.";

    public override void Start(VigilPlugin host) => _host = host;

    public override string Command(string word, string rest)
    {
        return word == "missing" ? Missing(rest) : null;
    }

    /// <summary>What a search found: the objects without a prefab within the radius, nearest first, and the nearest one anywhere.</summary>
    private sealed class Found
    {
        public List<WorldObject> Picked;
        public List<ZDO> Zdos;
        public WorldObject Nearest;
        public float Radius;
    }

    private string Missing(string rest)
    {
        var said = new StringBuilder();
        void Say(string s)
        {
            said.Append(said.Length > 0 ? "\n" : "").Append("Vigil: ").Append(s);
            _host.Log.LogInfo(s);
        }

        try
        {
            var words = rest.ToLowerInvariant().Split(Space, StringSplitOptions.RemoveEmptyEntries).ToList();
            bool remove = words.Contains("remove");
            var numbers = words.Where(w => w != "remove").ToList();
            float radius = numbers.Count > 0 && float.TryParse(numbers[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) ? r : ConsoleHelp.MissingRadius;
            int? only = numbers.Count > 1 && int.TryParse(numbers[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) ? h : null;
            var found = Find(radius, only);
            if (found == null)
            {
                Say("load into a world first");
            }
            else
            {
                foreach (var line in Describe(found))
                {
                    Say(line);
                }

                if (found.Picked.Count > 0)
                {
                    Say(remove
                        ? "removed " + Remove(found).ToString(CultureInfo.InvariantCulture) + " object(s); the server drops them from the world"
                        : "nothing removed; add 'remove' to delete these");
                }
            }
        }
        catch (Exception e)
        {
            Say("failed: " + e.Message);
            _host.Log.LogError(e);
        }

        return said.ToString();
    }

    private static Found Find(float radius, int? only)
    {
        var player = Player.m_localPlayer;
        if (player == null || ZDOMan.instance == null || ZNetScene.instance == null)
        {
            return null;
        }

        var zdos = Objects(ZDOMan.instance).Values.Where(z => z != null).ToList();
        var view = zdos.Select((z, i) =>
        {
            var p = z.GetPosition();
            return new WorldObject(i, z.GetPrefab(), p.x, p.y, p.z);
        }).ToList();
        var me = player.transform.position;
        bool Exists(int hash) => ZNetScene.instance.GetPrefab(hash) != null;
        var picked = Orphans.Select(view, Exists, me.x, me.y, me.z, radius, only);
        var nearest = picked.Count > 0 ? null : Orphans.Select(view, Exists, me.x, me.y, me.z, float.MaxValue, only).FirstOrDefault();
        return new Found { Picked = picked, Zdos = zdos, Nearest = nearest, Radius = radius };
    }

    private static List<string> Describe(Found f)
    {
        var lines = new List<string>();
        string within = f.Radius.ToString("0", CultureInfo.InvariantCulture) + " m";
        if (f.Picked.Count == 0)
        {
            lines.Add("no objects without a prefab within " + within);

            // The game loads objects well beyond the default radius, so say where the nearest one is.
            if (f.Nearest != null)
            {
                lines.Add(string.Format(CultureInfo.InvariantCulture, "the nearest one, {0}, is {1:0} m away at ({2:0}, {3:0}, {4:0}); use: vigil missing {5:0}",
                    KnownPrefabs.NameOf(f.Nearest.Hash), f.Nearest.Distance, f.Nearest.X, f.Nearest.Y, f.Nearest.Z, Math.Ceiling(f.Nearest.Distance + 5)));
            }

            return lines;
        }

        lines.Add(f.Picked.Count.ToString(CultureInfo.InvariantCulture) + " object(s) without a prefab within " + within + ":");
        foreach (var o in f.Picked.Take(MaxListed))
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture, "  {0}  {1:0} m away at ({2:0}, {3:0}, {4:0})", KnownPrefabs.NameOf(o.Hash), o.Distance, o.X, o.Y, o.Z));
        }

        if (f.Picked.Count > MaxListed)
        {
            lines.Add("  ... and " + (f.Picked.Count - MaxListed).ToString(CultureInfo.InvariantCulture) + " more");
        }

        return lines;
    }

    /// <summary>Takes each found object over and destroys it the way the game destroys any object.</summary>
    private static int Remove(Found f)
    {
        long sessionId = ZDOMan.GetSessionID();
        int removed = 0;
        foreach (var o in f.Picked)
        {
            // The game reuses the objects of destroyed ones, so one found earlier must still be the same missing prefab.
            var zdo = f.Zdos[(int)o.Id];
            if (zdo == null || zdo.GetPrefab() != o.Hash)
            {
                continue;
            }

            zdo.SetOwner(sessionId);
            ZDOMan.instance.DestroyZDO(zdo);
            removed++;
        }

        return removed;
    }

    // ------------------------------------------------------------------ panel and self-test

    public override IEnumerable<PanelSection> Sections()
    {
        yield return new PanelSection("World", "Objects of removed mods", Draw, Badge, PanelZone.Change);
    }

    private PanelBadge Badge() =>
        _shown == null ? null
        : _shown.Picked.Count == 0 ? new PanelBadge("none found", Skin.Tone.Good)
        : new PanelBadge(_shown.Picked.Count.ToString(CultureInfo.InvariantCulture) + " found", Skin.Tone.Caution);

    private void Draw()
    {
        if (Ui.Tool("Search around you", "Objects whose prefab no installed mod provides; the game warns about each again and again", "Find", ref _radiusText, 50, "m"))
        {
            float radius = float.TryParse(_radiusText, NumberStyles.Float, CultureInfo.InvariantCulture, out float r) ? r : ConsoleHelp.MissingRadius;
            _shown = Find(radius, null);
            _confirm.Disarm();
            if (_shown == null)
            {
                _host.Show("Vigil: load into a world first");
            }
        }

        if (_shown == null)
        {
            return;
        }

        int n = _shown.Picked.Count;
        if (n == 0)
        {
            foreach (var line in Describe(_shown))
            {
                Ui.Note(char.ToUpperInvariant(line[0]) + line.Substring(1) + ".");
            }

            return;
        }

        Ui.Heading("Found, nearest first");
        Ui.Table(
            new[] { new Ui.Column("Object", 0, false), new Ui.Column("Away", 70, true), new Ui.Column("At", 170, true) },
            _shown.Picked.Take(MaxListed).Select(o => new[]
            {
                KnownPrefabs.NameOf(o.Hash),
                o.Distance.ToString("0", CultureInfo.InvariantCulture) + " m",
                string.Format(CultureInfo.InvariantCulture, "{0:0}, {1:0}, {2:0}", o.X, o.Y, o.Z),
            }));
        if (n > MaxListed)
        {
            Ui.Note("And " + (n - MaxListed).ToString(CultureInfo.InvariantCulture) + " more.");
        }

        string what = n.ToString(CultureInfo.InvariantCulture) + " objects";
        double now = Time.realtimeSinceStartupAsDouble;
        bool armed = _confirm.Armed(what, now);
        Ui.BeginLine();
        if ((armed ? Ui.Danger("Press again to remove " + what + " for good") : Ui.Button("Remove these " + what)) && _confirm.Press(what, now))
        {
            // Removing empties the list above this button, so it happens before the next layout pass.
            var listed = _shown;
            Ui.Later(() =>
            {
                if (_shown != listed)
                {
                    return;
                }

                int removed = Remove(listed);
                _shown = null;
                _host.Show("Vigil: removed " + removed.ToString(CultureInfo.InvariantCulture) + " object(s); the server drops them from the world");
            });
        }

        Ui.Note("Can't be undone. Back up the world first.");
        Ui.EndLine();
    }

    public override void SelfTest(List<SelfTestReport.Check> checks)
    {
        const string what = "the game's list of world objects";
        if (ZDOMan.instance == null)
        {
            checks.Add(new SelfTestReport.Check(Area, what, SelfTestReport.Result.Info, "not in a world"));
            return;
        }

        try
        {
            int count = Objects(ZDOMan.instance).Count;
            checks.Add(new SelfTestReport.Check(Area, what, SelfTestReport.Result.Pass, count.ToString(CultureInfo.InvariantCulture) + " objects loaded"));
        }
        catch (Exception e)
        {
            checks.Add(new SelfTestReport.Check(Area, what, SelfTestReport.Result.Fail, e.GetType().Name + " " + e.Message));
        }
    }
}
