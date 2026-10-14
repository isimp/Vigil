using System;
using System.Collections.Generic;
using Vigil.Core;

namespace Vigil;

/// <summary>
/// One area of Vigil: the recorder, the spawn marks or the cleanup. Each has a switch in the
/// config's Modules section; one that is switched off is never started, so it puts in no hooks
/// and costs nothing. The host passes a started module Unity's calls, the console words it owns,
/// the admin commands sent to the server, and draws its sections in the panel.
/// </summary>
internal abstract class Module
{
    /// <summary>The area's name: its switch under Modules in the config, and its heading in the help.</summary>
    public abstract string Area { get; }

    /// <summary>The description of the area's switch in the config.</summary>
    public abstract string SwitchDescription { get; }

    /// <summary>False for an area that has nothing to do on a dedicated server, which then never starts it.</summary>
    public virtual bool RunsOnServer => false;

    /// <summary>Binds the area's settings and puts in what it needs. Runs once, while the game loads its mods.</summary>
    public abstract void Start(VigilPlugin host);

    public virtual void Update()
    {
    }

    public virtual void FixedUpdate()
    {
    }

    /// <summary>Ticks of the area's last calls that the recorder already counts elsewhere, left out of Vigil's own row; read once per call.</summary>
    public virtual long TakeTicksTimedElsewhere() => 0;

    /// <summary>The game is quitting: anything still to write is written now.</summary>
    public virtual void Quit()
    {
    }

    /// <summary>Vigil is being unloaded: objects the area made are released.</summary>
    public virtual void Destroy()
    {
    }

    /// <summary>The answer to "vigil &lt;word&gt; &lt;rest&gt;" for a word this area owns; null for a word it does not know.</summary>
    public virtual string Command(string word, string rest) => null;

    /// <summary>The area's lines in "vigil status"; null when it has nothing to say.</summary>
    public virtual string Status() => null;

    /// <summary>The area's sections in the panel, each under one of its tabs, drawn while the panel is open.</summary>
    public virtual IEnumerable<PanelSection> Sections() => Array.Empty<PanelSection>();

    /// <summary>The area's self-test routines: each exercises a feature over a few frames and adds its checks; the full tier adds those that ask the server.</summary>
    public virtual IEnumerable<SelfTestRoutine> Routines(bool full) => Array.Empty<SelfTestRoutine>();

    /// <summary>Adds the area's checks against the running game to a self-test.</summary>
    public virtual void SelfTest(List<SelfTestReport.Check> checks)
    {
    }

    /// <summary>
    /// On a dedicated server: the answer to a command an admin sent from a player's game, as named
    /// values ("text" for the line to show, "ok" 0 when it failed); null for a command this area does not know.
    /// </summary>
    public virtual Dictionary<string, object> Admin(string command, Dictionary<string, string> args, ZNetPeer from) => null;
}

/// <summary>
/// What a card's tools do to the game, shown by its edge and a tag: they only look; they cost
/// frame time while they run; they load the server; they change the world.
/// </summary>
internal enum PanelZone
{
    Watch,
    Measure,
    Load,
    Change,
}

/// <summary>
/// One section of the panel: a card on one tab with its title, drawn by the area that adds it,
/// optionally a badge beside the title with the section's state at a glance (null for none), and
/// its zone.
/// </summary>
internal sealed class PanelSection
{
    public PanelSection(string tab, string title, Action draw, Func<PanelBadge> badge = null, PanelZone zone = PanelZone.Watch)
    {
        Tab = tab;
        Title = title;
        Draw = draw;
        Badge = badge;
        Zone = zone;
    }

    public string Tab { get; }

    public string Title { get; }

    public Action Draw { get; }

    public Func<PanelBadge> Badge { get; }

    public PanelZone Zone { get; }

    /// <summary>The words of the card's zone tag; null for a card that only looks.</summary>
    public string ZoneTag => Zone switch
    {
        PanelZone.Measure => "costs frame time",
        PanelZone.Load => "loads the server",
        PanelZone.Change => "changes the world",
        _ => null,
    };
}

/// <summary>A section's state beside its title: a few words in a tone.</summary>
internal sealed class PanelBadge
{
    public PanelBadge(string text, Skin.Tone tone)
    {
        Text = text;
        Tone = tone;
    }

    public string Text { get; }

    public Skin.Tone Tone { get; }
}

/// <summary>
/// One routine of a self-test: a coroutine that exercises a feature over frames and adds its
/// checks, given at most <see cref="Seconds"/>; one that runs over or throws is a check of its own.
/// </summary>
internal sealed class SelfTestRoutine
{
    public SelfTestRoutine(string area, string what, float seconds, Func<List<SelfTestReport.Check>, System.Collections.IEnumerator> run)
    {
        Area = area;
        What = what;
        Seconds = seconds;
        Run = run;
    }

    public string Area { get; }

    public string What { get; }

    public float Seconds { get; }

    public Func<List<SelfTestReport.Check>, System.Collections.IEnumerator> Run { get; }
}
