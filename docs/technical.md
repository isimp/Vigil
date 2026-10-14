# Vigil technical notes

## Layout

One plugin, `isimp.Vigil`. `src/VigilPlugin.cs` is the host: it starts the switched-on areas (a dedicated server runs only the recorder), passes them Unity's calls, and answers the `vigil` console command. `src/Host` holds the panel (IMGUI, `Ui` blocks and `Skin`), the HUD line, the link to the server and the module base. `src/Recorder`, `src/Spawns` and `src/Cleanup` are the areas. `src/Core` holds everything that needs no game; `tests/Vigil.Tests` tests it on its own. The version is set only in `Vigil.csproj`.

## Files

`Vigil-timeline.tsv` has a row per second; `Vigil-incident-*.txt` are reports, `Vigil-events.tsv` lists joins, leaves, saves, weather changes and spawns, `Vigil-peers.tsv` (server) has a row per player per second, and `Vigil-summary.txt` sums up the session. `Vigil-state/` is never archived: the hook journal and skip list (a hook that crashed the game is skipped from the next start), `session.running` (present while running, touched once a minute, so a crash is noticed), panel state, and on a server `alerts.tsv` and `online.txt`. At start the previous session moves to `Vigil-runs/`.

## Timing

Mods' Unity callbacks and coroutines are timed with a Harmony prefix and finalizer, on the main thread only. A deep capture (on a slow stretch or `vigil deep`) times every game method mods patch, installed a few per frame. Vigil's own calls cannot be hooked, since the recorder closes each frame inside its own Update, so the plugin times them itself and reports them as the mod Vigil.

## The link to the server's Vigil

Four routed messages (`Vigil_Hello`, `Vigil_HelloReply`, `Vigil_Admin`, `Vigil_AdminReply`) carry one line of named values each. The hello tells each side the other's version and capabilities and whether the player is an admin. The server checks admin status for every question; sharing, spawn logs and parity are open to every player. Settings the server decides (its fog limit) go through ServerSync.

## Alerts

The server turns its incidents, shared spawn logs, shared reports and an unclean previous run into alerts, keeps a week of them in `Vigil-state/alerts.tsv`, and an admin's game asks for new ones every 10 s. Falling behind alerts at most once in ten minutes, spawns at one base once an hour.

## Load test and player simulation

`vigil load` sends world-update traffic in rising steps between a player's game and the server, then reports what each direction carried and what held it back. `vigil load players` adds simulated players on the server in doubling steps and reports how many it kept up with. Only admins can start either, and the server refuses while other players are on unless `force` is added.

## Building

`dotnet build Vigil.csproj -c Release` builds against the installed game and copies the plugin into the Gale profile; `-p:LibsDir=lib` builds against the committed reference stubs instead, as CI does. `dotnet test tests/Vigil.Tests` runs the tests. Releasing is in [releasing.md](releasing.md).
