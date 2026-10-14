using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Vigil.Core;

namespace Vigil.Spawns;

/// <summary>
/// The spawn lists as the game holds them now, so a change made by another mod at load time is
/// seen too: the biome spawns of every loaded spawn system and the spawns of every enabled raid.
/// Each entry carries the game's own "inside player base" flag.
/// </summary>
internal static class SpawnLists
{
    private static readonly FieldInfo Instances = typeof(SpawnSystem).GetField("m_instances", BindingFlags.NonPublic | BindingFlags.Static);
    private static bool _warned;

    public static List<SpawnEntry> Live()
    {
        var entries = new List<SpawnEntry>();
        try
        {
            if (Instances?.GetValue(null) is IEnumerable systems)
            {
                foreach (var item in systems)
                {
                    if (item is not SpawnSystem system || !system)
                    {
                        continue;
                    }

                    foreach (var list in system.m_spawnLists)
                    {
                        if (!list)
                        {
                            continue;
                        }

                        foreach (var data in list.m_spawners)
                        {
                            Add(entries, "world", data);
                        }
                    }
                }
            }

            var events = RandEventSystem.instance;
            if (events)
            {
                foreach (var raid in events.m_events)
                {
                    if (raid == null || !raid.m_enabled)
                    {
                        continue;
                    }

                    foreach (var data in raid.m_spawn)
                    {
                        Add(entries, "raid " + raid.m_name, data);
                    }
                }
            }
        }
        catch (Exception e)
        {
            if (!_warned)
            {
                _warned = true;
                SpawnsModule.Log.LogWarning("Could not read the game's spawn lists: " + e.Message);
            }
        }

        return entries;
    }

    private static void Add(List<SpawnEntry> entries, string source, SpawnSystem.SpawnData data)
    {
        if (data == null)
        {
            return;
        }

        string creature = data.m_prefab ? data.m_prefab.name : data.m_name;
        entries.Add(new SpawnEntry(source, creature, data.m_enabled && !data.m_devDisabled, data.m_insidePlayerBase));
    }
}
