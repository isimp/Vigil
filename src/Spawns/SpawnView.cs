using System.Diagnostics;
using Vigil.Core;
using UnityEngine;

namespace Vigil.Spawns;

/// <summary>
/// The view of spawn gaps: while it is on, the spots around the player are checked a share a
/// frame, and once a check is complete its open spots replace the marks on the ground. The view
/// follows the player and is checked again every few seconds, so pieces built or torn down show.
/// </summary>
internal sealed class SpawnView
{
    private const float Every = 3f;
    private const int Chunk = 64;
    private const double BudgetMs = 1.5;

    private readonly MarkMesh _marks = new();
    private readonly Stopwatch _clock = new();
    private Survey _survey;
    private bool _on;
    private bool _announce;
    private bool _everChecked;
    private float _lastStart;
    private Vector3 _lastCenter;
    private float _radius;
    private float _spacing;
    private int _ignoringBases;

    public bool On => _on;

    /// <summary>Open spots in the last finished check, -1 before the first.</summary>
    public int OpenSpots { get; private set; } = -1;

    /// <summary>The radius of the last check, in metres.</summary>
    public float Radius => _radius;

    public void Toggle(Player player)
    {
        if (_on)
        {
            TurnOff();
            Say(player, "Spawn gaps hidden");
            return;
        }

        _on = true;
        _announce = true;
        _everChecked = false;
        var ignoring = Readout.IgnoringBases(SpawnLists.Live());
        _ignoringBases = ignoring.Count;
        if (ignoring.Count > 0)
        {
            SpawnsModule.Log.LogInfo("These spawns may appear inside bases, so the marks do not show safety from them: " + string.Join(", ", ignoring));
        }
    }

    public void Tick(Player player)
    {
        if (!_on)
        {
            return;
        }

        if (!player)
        {
            TurnOff();
            return;
        }

        var here = player.transform.position;
        float moved = new Vector2(here.x - _lastCenter.x, here.z - _lastCenter.z).magnitude;
        bool checking = _survey != null && !_survey.Done;
        if (Recheck.Due(checking, _everChecked, Time.time, _lastStart, moved, _spacing, Every))
        {
            Start(here);
        }

        if (_survey != null && !_survey.Done)
        {
            _clock.Restart();
            while (!_survey.Done && _clock.Elapsed.TotalMilliseconds < BudgetMs)
            {
                _survey.Step(Chunk);
            }

            if (_survey.Done)
            {
                Publish(player);
            }
        }

        _marks.Draw();
    }

    public void Destroy() => _marks.Destroy();

    private void Start(Vector3 here)
    {
        _radius = Settings.Radius.Value;
        _spacing = Settings.Spacing.Value;
        _lastCenter = here;
        _lastStart = Time.time;
        _everChecked = true;
        _survey = new Survey(SampleGrid.Around(here.x, here.z, _radius, _spacing), GroundCheck.At);
    }

    private void Publish(Player player)
    {
        float water = ZoneSystem.instance ? ZoneSystem.instance.m_waterLevel : 30f;
        _marks.Build(_survey.Open, Settings.Style.Value, _spacing, Settings.PillarHeight.Value, Settings.MarkColor.Value, water);
        OpenSpots = _survey.Open.Count;
        if (_announce)
        {
            _announce = false;
            Say(player, Readout.Summary(_survey.Open.Count, _radius, _ignoringBases));
        }
    }

    private void TurnOff()
    {
        _on = false;
        _survey = null;
        _marks.Clear();
    }

    private static void Say(Player player, string text)
    {
        if (player && MessageHud.instance)
        {
            MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, text);
        }
    }
}
