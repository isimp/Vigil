# Spawn marks technical notes

## The game's rule

A biome spawn or a raid spawn picks a point and asks `SpawnSystem.IsSpawnPointGood` whether it may use it. Among its checks, two do not depend on the creature:

`ZoneSystem.IsBlocked` casts a ray from 2000 m above the point straight down through it, against the `Default`, `static_solid`, `Default_small` and `piece` layers. Anything it hits, above or below the point, refuses the point. A roof, a floor or a rock overhang therefore keeps spawns out of the ground under and over it.

`EffectArea.IsPointInsideArea(point, EffectArea.Type.PlayerBase)` looks for a `PlayerBase` effect area collider at the point. If one is there, the point is refused unless the spawn entry has `m_insidePlayerBase` set. `CreatureSpawner`, the fixed spawners of locations, makes the same check with its own `m_spawnInPlayerBase`.

The point's height comes from `ZoneSystem.GetGroundData`, a ray against the terrain layer only.

## The check

`GroundCheck.At` makes those calls for one spot, in the game's order, with the game's arguments: `GetGroundData` for the height and the slope, then `IsBlocked`, then the player base test. A spot where the terrain ray hits no heightmap is unloaded and left unmarked. The other checks of `IsSpawnPointGood` (biome, biome area, altitude, tilt, distance from the world centre, forest, lava, ocean depth, distance from players) are left out, because each spawn entry sets them differently.

`SampleGrid.Around` lists the spots: the points of a world grid with `Spacing` between them that lie within `Radius` of the player. The grid is anchored to the world, so a spot keeps its place and its dot does not move while the player walks.

A `Survey` checks those spots a share at a time, in steps of 64 for up to 1.5 ms a frame, and hands out its open spots only when every spot is checked. `Recheck.Due` starts the next check once the player has moved one grid step or 3 seconds have passed, but never while a check is underway, so a walking player still sees whole checks.

## Spawns allowed inside bases

When the view is turned on, `SpawnLists.Live` reads the spawn lists as the game holds them at that moment: every `SpawnSystemList` of every loaded `SpawnSystem` (through the private static `SpawnSystem.m_instances`), and the `m_spawn` list of every enabled raid in `RandEventSystem.m_events`. A change another mod made to those lists at load time is therefore seen. The enabled entries with `m_insidePlayerBase` set are counted in the message and named in the BepInEx log, each once, as "world: creature" or "raid name: creature".

## Drawing

`MarkShape.Build` turns the open spots into one mesh. Every spot gets a dot: a small quad turned to the ground's slope and lifted 8 cm along it. With `MarkStyle` at `Pillars`, the default, it also gets a pillar `PillarHeight` tall: two upright quads crossing at a right angle, standing straight up whatever the slope, so one of them faces the viewer from any side. A spot below the water level is marked on the surface instead, the dot facing up. One texture made at run time holds a soft round dot in its left half and a beam with soft sides fading towards its top in its right half; the colour is in the vertex colours. The mesh is drawn each frame with `Graphics.DrawMesh`, with no shadows, so the view costs one draw call and no game objects. The material uses the first shader found of `Sprites/Default`, `Particles/Standard Unlit`, `Legacy Shaders/Particles/Alpha Blended` and `Unlit/Transparent`.

## Key

The key (`Spawn marks.Key`, F6) is read with `Input.GetKeyDown`, and only while no console, chat, text box, menu or focused text field has the keyboard, so typing never toggles the view. `vigil spawns` in the console toggles it as well. A dedicated server never starts the spawn marks.

## Tests

`dotnet test tests/Vigil.Tests` runs the tests of `src/Core`, the spawn marks' among them: the grid, the step-by-step check, when to check again, the shape of the marks, and the messages. The same tests keep comments and notes free of dates, clock times and the terms of the local list in `.git/info/forbidden-terms`.
