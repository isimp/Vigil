# Vigil technical notes

## Layout

One plugin, `isimp.Vigil`. `src/VigilPlugin.cs` is the host: it starts the switched-on areas (a dedicated server runs only the recorder), passes them Unity's calls, and answers the `vigil` console command. `src/Host` holds the panel (IMGUI, `Ui` blocks and `Skin`), the HUD line, the link to the server and the module base. `src/Recorder`, `src/Spawns` and `src/Cleanup` are the areas. `src/Core` holds everything that needs no game; `tests/Vigil.Tests` tests it on its own. The version is set only in `Vigil.csproj`.

## Files

`Vigil-timeline.tsv` has a row per second; `Vigil-incident-*.txt` are reports, `Vigil-events.tsv` lists joins, leaves, saves, weather changes and spawns, `Vigil-peers.tsv` (server) has a row per player per second, and `Vigil-summary.txt` sums up the session. `Vigil-state/` is never archived: the hook journal and skip list (a hook that crashed the game is skipped from the next start), `session.running` (present while running, touched once a minute, so a crash is noticed), panel state, and on a server `alerts.tsv` and `online.txt`. At start the previous session moves to `Vigil-runs/`.

## Timing

Mods' Unity callbacks and coroutines are timed with a Harmony prefix and finalizer, on the main thread only. A deep capture (on a slow stretch or `vigil deep`) times every game method mods patch, installed a few per frame. Vigil's own calls cannot be hooked, since the recorder closes each frame inside its own Update, so the plugin times them itself and reports them as the mod Vigil.

## Spawns, base arrivals and windmills

Spawns are seen at `SpawnSystem.Spawn`, `SpawnArea.SpawnOne` and `CreatureSpawner.Spawn`, after every other mod's prefixes. The game never spawns inside a base area unless the entry allows it, so once a second the creatures hostile to the player (`BaseAI.IsEnemy`) that stand inside a base area within 100 m are noted, each once, with the base's piece and builder and the spawn's record when this game made it (`Core/Recorder/BaseArrivals`). A windmill's wind check casts the game's own 17 cover rays (`Cover.GetCoverForPoint`, from the propeller, 29.5 m each) and names what each blocked ray hits; output is (1 - cover) times the wind, none under 10 % (`Windmill.GetPowerOutput`, `Core/Recorder/BaseArrivals.cs` `WindCheck`).

## Objects with rules of their own

`src/Recorder/ObjectParts.cs` reads, for the inspector, the objects the game judges by their surroundings, and `Core/Recorder/ObjectRules` puts each in words (assembly_valheim 1.0.20). Cover is the share of the 17 rays `Cover.GetCoverForPoint` casts that hit something (`ObjectParts.CastCover` casts the same rays and names what each hits). A fermenter needs 70 % cover and a roof, checked every 10 s, or its owner restarts the batch; a beehive works under its `m_maxCover`, in its biome and, unless set otherwise, by daylight; a crop needs its biome, cultivated ground where asked, warmth (a shield generator in the Ashlands, Mountain and Deep North), nothing solid straight above within 100 m and nothing in its grow radius but unhealthy plants, and grows when its own seeded time is up, withering then if its kind does when it cannot; a smelter stops without fuel, ore, a roof where it needs one, or when its smoke vent is blocked; a wet fire only burns low, a blocked one (terrain above, something solid just above, or blocked smoke) goes out; taming goes on only while the creature is fed and calm; breeding needs a ready partner within 3 m and fewer than its limit of its kind and their young within 10 m. Private values are read by reflection. The *World* tab's *Rested and comfort* card reads where the player stands: shelter is 80 % cover from the character's centre and a roof, and comfort counts only in shelter, the best piece of each comfort group within 10 m, one of each name; it is compared with the game's own count, which a mod may change.

## Drops

A connection that ends without a goodbye is read from its last minute (`Core/Recorder/DropAnalysis`): traffic that faded or packets that came further apart before the silence point at the other side's game, a stop all at once at the line. Steam's incoming rate is an estimate that changes in steps of a few seconds and decays after the packets stop, so the comparison uses the seconds before the real stop: the earliest of the last second with traffic, a pause between packets growing by about a second each second, and unconfirmed data piling up from one second to the end (`DropAnalysis.StopAt`).

## The link to the server's Vigil

Four routed messages (`Vigil_Hello`, `Vigil_HelloReply`, `Vigil_Admin`, `Vigil_AdminReply`) carry one line of named values each. The hello tells each side the other's version and capabilities and whether the player is an admin. The server checks admin status for every question; sharing, spawn logs and parity are open to every player. Settings the server decides (its fog limit) go through ServerSync.

## Alerts

The server turns its incidents, shared spawn logs, shared reports and an unclean previous run into alerts, keeps a week of them in `Vigil-state/alerts.tsv`, and an admin's game asks for new ones every 10 s. Falling behind and each player's connection alert at most once in ten minutes, spawns at one base once an hour. Of the reports players share, only those sent by hand (lag and environment reports) alert; what their games share on their own is listed among the server's reports.

## Load test and player simulation

`vigil load` sends world-update traffic in rising steps between a player's game and the server, then reports what each direction carried and what held it back. `vigil load players` adds simulated players on the server in doubling steps and reports how many it kept up with. Only admins can start either, and the server refuses while other players are on unless `force` is added.

## Building

`dotnet build Vigil.csproj -c Release` builds against the installed game (the plugin itself reaches the profile and the server through Gale from Hexium); `-p:LibsDir=lib` builds against the committed reference stubs instead, as CI does. `dotnet test tests/Vigil.Tests` runs the tests. Releasing is in [releasing.md](releasing.md).
