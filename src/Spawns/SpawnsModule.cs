using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Spawns;

/// <summary>
/// The spawn marks: a key, or "vigil spawns", shows a mark on every spot around the player where
/// a creature can still spawn, and hides them again. Runs on a player's machine only.
/// </summary>
internal sealed class SpawnsModule : Module
{
    private readonly SpawnView _view = new();
    private VigilPlugin _host;

    internal static ManualLogSource Log { get; private set; }

    public override string Area => ConsoleHelp.SpawnMarks;

    public override string SwitchDescription =>
        "The spawn marks: a key shows every spot around you where a creature can still spawn. Off: the key does nothing, from the next start on.";

    /// <summary>The configured key, for the help; F6 while the area is not running.</summary>
    internal string KeyName() => Settings.Key != null ? Settings.Key.Value.ToString() : "F6";

    public override void Start(VigilPlugin host)
    {
        _host = host;
        Log = host.Log;
        Settings.Bind(host.Config);
    }

    public override void Update()
    {
        var player = Player.m_localPlayer;
        if (Input.GetKeyDown(Settings.Key.Value) && Guard.MayAct(player))
        {
            _view.Toggle(player);
        }

        _view.Tick(player);
    }

    public override void Destroy() => _view.Destroy();

    public override string Command(string word, string rest)
    {
        if (word == "spawns")
        {
            var player = Player.m_localPlayer;
            if (!player || player.IsDead())
            {
                return "Vigil: the spawn marks need you in a world, alive";
            }

            _view.Toggle(player);
            return "Vigil: spawn marks " + (_view.On ? "shown" : "hidden");
        }

        return null;
    }

    public override string Status() => "Spawn marks: " + (_view.On ? "shown" : "hidden") + " (key " + KeyName() + ", or vigil spawns)";

    public override IEnumerable<PanelSection> Sections()
    {
        yield return new PanelSection("World", "Spawn marks", Draw, () => _view.On ? new PanelBadge("shown", Skin.Tone.Accent) : null);
    }

    private void Draw()
    {
        string state = !_view.On ? "Hidden"
            : _view.OpenSpots >= 0
                ? _view.OpenSpots.ToString(CultureInfo.InvariantCulture) + " open spots within " + _view.Radius.ToString("0", CultureInfo.InvariantCulture) + " m"
                : "Checking the ground around you ...";
        if (Ui.Tool(state, "Where a creature can still spawn: outside every base, nothing solid above or below", _view.On ? "Hide" : "Show"))
        {
            _host.RunShown("spawns");
        }

        Ui.Note("Also with " + KeyName() + ". Radius, spacing, style and colour are under Spawn marks in the config.");
    }

    public override void SelfTest(List<SelfTestReport.Check> checks)
    {
        string shader = MarkMesh.FirstShader();
        checks.Add(shader != null
            ? new SelfTestReport.Check(Area, "a shader to draw the marks with", SelfTestReport.Result.Pass, shader)
            : new SelfTestReport.Check(Area, "a shader to draw the marks with", SelfTestReport.Result.Fail, "none of the shaders tried is in this game"));
        if (!ZNetScene.instance)
        {
            checks.Add(new SelfTestReport.Check(Area, "the game's spawn lists", SelfTestReport.Result.Info, "not in a world"));
            return;
        }

        var lists = SpawnLists.Live();
        checks.Add(lists != null && lists.Count > 0
            ? new SelfTestReport.Check(Area, "the game's spawn lists", SelfTestReport.Result.Pass, lists.Count.ToString(CultureInfo.InvariantCulture) + " entries")
            : new SelfTestReport.Check(Area, "the game's spawn lists", SelfTestReport.Result.Warn, "none could be read"));
    }
}
