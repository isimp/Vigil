# Vigil guide

Vigil runs on the players' games and on the dedicated server. It records what the game does, so lag and dropped connections can be explained afterwards, and gives the server's admins a panel to look at the server, its players and the world. Settings are in `BepInEx/config/isimp.Vigil.cfg`; each area (`Recorder`, `Spawn marks`, `Cleanup`) has a switch under `Modules`.

## Recorder

While in a world (on a server: while players are on), Vigil writes one row a second to `Vigil-timeline.tsv`: frame times, what each mod's callbacks cost, network numbers, memory and more. When something goes wrong (a long frame, a slow stretch, high ping, data piling up, a log flood, memory pressure, a dropped connection) it writes a report, `Vigil-incident-<time>-<kind>.txt`, saying what took the time. F9 writes one on demand. Recordings go to `LocalLow/IronGate/Valheim/Vigil` on a player's machine and `BepInEx/Vigil` on a server; earlier sessions are kept in `Vigil-runs`. `Recording.Mode` switches recording on, off or auto.

## Panel and HUD line

Shift+F9 opens the panel. *Overview* shows alerts (for admins), the recording and sharing. *Network* shows this connection and, for admins, the server, every player's connection and the server's reports. *World* shows who runs which objects, inspects the object under the crosshair (a windmill's wind and what blocks it; why a fermenter, beehive, crop, smelter or fire works or not; how far taming and breeding are and what holds them up; the cover rays of a windmill, fermenter or beehive drawn on request), reads where you stand for rest and comfort (shelter, the pieces that count), explains the weather, lists spawns and the creatures that came into a base with where they spawned (each pinnable on your map) and what could spawn at a spot, and, for admins, everyone's spawns and leftovers of removed mods. *Tools* holds the census, memory list, patch map and load test. *Health* runs the self-test and compares mods and configs with the server. *Patches* lists Vigil's own game patches, each off unless the server switches it on for everyone: *OdinOnDemand follows the game's volume* lets OdinOnDemand's radios, screens and girdle follow the game's master and music volume from the first join, and gives the sound back at 100 % after AzuMiscPatches' mute in background; *Skip winter* makes Seasonality go from fall straight to spring. Holding the right mouse button off the panel turns the camera. The HUD line in a corner shows frame rate, ping, queued data, memory and, for admins, the server's frame rate and new alerts.

## With the server

When the server runs Vigil too, the game greets it and learns whether you are an admin (`adminlist.txt`). Admins can ask the server for its state, reports, a player's last minute of connection, a load test or player simulation, and get alerts: drops, the server falling behind or short of memory, log floods, spawns inside bases, lag and environment reports a player sent, and a start after a crash. Players can share their own reports and spawn log with the server under *Sharing* (off by default; the server takes them only with `Sharing.Accept`). Before a restart with players on, the server puts a countdown on everyone's screen.

## Spawn marks and cleanup

F6 (or `vigil spawns`) marks every spot around you where a creature could still spawn: outside base protection and with nothing solid above or below. `vigil missing` lists objects of removed mods near you, and `vigil missing remove` deletes them; back up the world first.

## Console

`vigil help` lists every command, `vigil help <command>` explains one, and `vigil selftest` checks Vigil against the running game.
