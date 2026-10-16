# Changelog

## 0.31.0

The messages that keep coming at a fixed rate whatever the players do, per sender: the Network tab's new *Steady messages, per player* card shows the server's admins each one with its mod, who sends it, how often, how many kB per minute and in how many 10 s windows of the last 12 minutes it came, and each machine's summary lists its own.

## 0.30.0

What Vigil itself costs, in one place: a *Vigil's own cost* card in the Health tab and a block at the top of the summary give its time per frame (its own loops, its hooks in the game's methods and its timers on the mods' callbacks) and its share of your frame, what the open panel adds, its time on threads of its own, how fast its recordings grow and its own network messages. Its hooks are timed for 10 s two minutes into a session and whenever you press *Measure*, the timers' own cost taken off. Memory per mod cannot be told by the game's runtime and is said to be not measured.

## 0.29.0

Messages in the traffic tables carry their names instead of bare numbers: Vigil learns every name the game or a mod hashes while the game runs, names each mod's config sync message, and reads the names in the game's and the mods' files once every plugin has loaded. On the server, a message it only routes between players now shows its name and its mod. The summary and the self-test say how many kinds of message seen were named, and from where.

## 0.28.0

You can walk with the panel open: walking, running, jumping and crouching reach the game as with its inventory open, while attacks, blocking, using things, the hotbar and the game's menus stay held back, and while a field of the panel has the keyboard the keys go to it. After turning the camera with the right mouse off the panel, the cursor comes back where it was instead of the middle of the screen.

## 0.27.1

The self-test's check of OdinOnDemand's cog slider failed on every run, as it looked for the cog panels among the game's objects; it now finds them through the radios that hold them, and a failure there no longer keeps the music volume trial from running.

## 0.27.0

The self-test now tries Vigil against the game itself, on your game only, and puts everything back. It compares Vigil's object readings with the game's own within 30 m: the cover rays at fermenters, beehives and fires, a beehive's space, a crop's sun and space, a smelter's blocked smoke, and the comfort where you stand. It tries the server's weather on your game for a few seconds (set, frozen, back) and compares the roll Vigil works out with the game's. It checks OdinOnDemand's level against the game's master and music volume and its cog slider against the music slider, and tries a music volume from the slider without saving it. It makes a map pin and copies a path, then takes both back.

## 0.26.0

The server's weather, for every player running Vigil: the World tab's weather card lets the server's admins (or the world's host) set one weather for every biome, picked from a list of the biome's weathers and every weather the game knows, which narrows as you type; freeze each biome's weather and the time of day as they are, until everyone sleeps through a night; and go back to the game's own roll. Raids, dungeons and the intro keep their own weather, and the world's time goes on, so days, plants and the world's age are not held. The server keeps the choice over restarts, and the card says who chose it, when, and what the roll would give. The weather can no longer be set on the admin's own game only; the fog drawn, the wind and the time of day drawn still can. The console has `vigil weather set`, `freeze` and `back`.

## 0.25.0

With *OdinOnDemand follows the game's volume* on, OdinOnDemand's master volume slider in a player's cog panel is the game's music slider, labelled *Music Volume* with its percentage: moving either moves the other, and radios, screens and the girdle play at their own slider times the game's master and music volume. OdinOnDemand's own master volumes per kind of player are no longer counted while the patch is on; they stay in its config for when it is off, and the *Patches* tab shows them. The panel's buttons for reports, alert files and the recordings folder now copy the path to the clipboard (*Copy path*, reading *Copied* for a moment) instead of opening it in Explorer, which opened a folder of its own choosing for some paths.

## 0.24.0

A second game patch, *Skip winter*: Seasonality's seasons go spring, summer, fall, spring, as the separate SkipWinter mod did, which it replaces. Like every patch it is off unless the server switches it on, and checks itself when it starts: it asks Seasonality for the season after fall and steps aside, saying why, unless the answer is spring.

## 0.23.0

A new *Patches* tab holds Vigil's own game patches, each off unless the server switches it on for every player running Vigil; the server's admins switch them in the tab. The first, *OdinOnDemand follows the game's volume*, makes OdinOnDemand's radios, screens and girdle follow the game's master and music volume from the first join instead of playing at full level until the game has once lost focus, and gives the sound back at 100 % after AzuMiscPatches' mute in background, so the game's own sounds keep their level after tabbing out.

## 0.22.0

*Inspect an object* explains the objects the game judges by their surroundings. A fermenter shows its batch, its cover and roof, and that the game starts the batch over while either is missing; a beehive shows its cover against its limit, its biome and daylight; a crop shows what blocks its sun or space and when it is ready, and whether it withers if it is not healthy by then; a smelter or kiln shows its fuel, ore, roof and whether its smoke gets out; a fire shows whether it is wet or blocked; a creature shows how far its taming is and what pauses it, and why it does or does not breed. A fermenter's or beehive's cover rays can be drawn in the world like a windmill's. A new *World* card, *Rested and comfort*, reads where you stand: shelter, your comfort and the pieces that count, the ones skipped and the groups still missing.

## 0.21.1

A dropped connection whose traffic stopped all at once reads as the line dying again. The incoming rate Steam reports changes in steps and keeps decaying for a few seconds after the packets stop, which made a sudden stop look like a game slowing down; the verdict now compares the seconds before the real stop, found from the pause between packets as it grows and from unconfirmed data piling up.

## 0.21.0

*Spawns near you* lists the creatures hostile to you that came into a base, each once, with the base and, when your game spawned it, where and when it spawned. The game never spawns inside a base area unless an entry allows it, so these spawned outside and walked in. Each has a *Pin* button that marks your own map where it spawned, or where it came in. *Inspect an object* on a windmill shows its wind: the output now, its cover as rays blocked of 17 with what blocks each, and *Show rays* draws the rays in the world.

## 0.20.3

A player whose connection goes quiet is one alert, not one a second, and each player's connection alerts at most once in ten minutes. Reports a player's game shares on its own no longer alert; a lag or environment report sent by hand still does, and every shared report stays listed among the server's reports.

## 0.20.2

The first package, on Hexium only, so that my group's mod manager installs Vigil for our players; until now it was copied in by hand. What it does is in the README and the guide.

Vigil's own work shows in the per-mod timing as a mod of its own, area by area, so the spawn marks and the panel show what they cost. A dedicated server builds no results for a panel it does not have.
