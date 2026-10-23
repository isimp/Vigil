# Changelog

## 0.38.0

*Overview > Fire sounds*, a player's own setting: a slider sets the level of fire loops in percent of each fire's own, live for the loaded fires and saved when let go (`Sound.FireLevel`), reaching either the fires whose loop came without the game's sound rules or every fire (`Sound.FireLevelAppliesTo`). The game's fires play their loop through a sound component that keeps a hearth at 40 to 60 % of its own level, fades it in and lets only the nearest few of a kind be heard; a mod's fire that leaves it out plays its loop at full level, every one of them. The switch *Game's sound rules* (`Sound.ModFiresFollowGameRules`, on) gives such a fire the component with the game's hearth's values as it loads, and puts a loop that plays through no mixer group on the game's SFX group, where the SFX volume applies. The section counts the fire loops within 20 m and names the kinds without the rules; the self-test checks the loaded loops' levels and gives the rules to a muted stand-in.

## 0.37.4

Leaving the world ends a deep capture at once: one that is measuring writes its report with what it has, and all its timers come out in that frame instead of a few per frame. Before, a capture under way at a logout went on taking its timers out in the main menu for many minutes, with the recording kept on meanwhile. The logout takes a few seconds longer when a capture was under way; the events file says how many timers came out and how long it took. The self-test checks it on a short capture.

## 0.37.3

The census counts shadow casters out to the shadow distance whatever its radius, so the per-layer shadow estimate no longer stops at 60 m while shadows reach 120 m or more; everything else stays within the radius. Its lights table says how many lights the game's light distance rule switches, which is what the game's point-light cap counts. One character, moving object or particle system reads in the singular, and the cost of a character another game runs is left out when no other game runs any.

## 0.37.2

*Tools > Count objects* now shows the census's four findings above its counts: characters and their time a physics step, the shadow casters per-layer distances would drop, building particle systems simulating off screen, and lights with shadows. The shadow and particle lines are marked when they found something to change.

## 0.37.1

The census (`vigil census`, *Tools > Count objects*) now reads over a second or two instead of stalling one frame, then times every character it found for 5 s, and writes `Vigil-census-<time>.txt`: characters by kind and whose game runs them, tamed, with their time in the physics steps; shadow casters by layer and distance from the camera, with how many per-layer sun shadow distances would drop; renderers by mod with instancing and the ones that could be drawn together; particle systems on building pieces that keep simulating off screen; lights and the shadows they draw. The material copies moved into that report. Hitch reports and the timeline (`physics_ms`) now time Unity's own physics step, so a frame full of physics catch-up says how much the physics engine took (`Hooks.PhysicsStep`). The self-test runs a small census.

## 0.37.0

*Join packages built once* is now one of the patches in the *Patches* tab, switched by the server like the others (`Patches.JoinPackages`, off until the server sets it; it replaces `Server fixes.JoinPackages`). The tab names the Drop That and Spawn That versions found on your game and, for admins, what the server built and how many joins got the packages from the cache.

## 0.36.0

Every join stood the server still for about 1.5 s while Drop That and Spawn That built the config packages for the joining player from scratch. The server now builds them once and sends every later join the same bytes (`Server fixes.JoinPackages`, on by default); a dedicated server builds them a minute after its world is up, while nobody is connected. They are built again after `dropthat:reload` or when the world's location count changes, and either mod builds its own again when a part Vigil uses is missing from its version. The events file and the summary say what was built and sent, and the self-test checks that two builds come out the same.

## 0.35.1

A player's join is now timed from their connection on, so the first join on an empty server shows which mods' messages took the time, and the server records while a player is still joining. A long frame no longer ends the 10 s window before its hitch report reads it, and the minute's summary no longer empties that window, so a report shows the messages that made the frame long instead of "0 calls".

## 0.35.0

*Vigil's own cost* shows its steady work as the headline and its one-off tools apart: the self-test and deep captures per frame while they ran, and the reports taken per report. A session line gives the cost with the tools spread over the whole session. The summary's block follows the card.

## 0.34.0

Names that mods put together while playing are in no file, so their bytes and messages showed as unknown. Vigil now notes which mod hashed each name it has not seen before, once the game is in a world, and gives those values and messages to that mod. The look is capped at 2,000 a minute, and the self-test says how many names got a mod.

## 0.33.1

The panel's tables size their columns by what is in them: no more columns cutting their text off while one column takes all the room; when the card is too narrow, numbers keep their width and the longest text gives way, ending in "…". A crop's space check also counts the vines within its vine radius, as the game does, so a plant held back by a vine is explained. The self-test names a timed patch by its mod.

## 0.33.0

Each mod's own patch code in the deep capture report: once the capture has found the costliest game methods, it times every mod's prefix, postfix and finalizer on them by itself and lists the mods by what their own patches cost per frame, each with its costliest patch, and which mods change the game's code with transpilers. The section that gave every mod patching a method that method's whole time stays, under a clearer name.

## 0.32.0

World-object data by mod: the Network tab's new card captures 60 s of what your game sends (and runs on its own during a deep capture) and shows, for each kind of object, how often it was sent, how big it was each time and how much of that the game's values and each mod's values take, and which values, by changing, made objects send again, movement included. The summary lists the last capture, and the self-test checks the sizes against the objects actually sent.

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
