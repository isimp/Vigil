# Changelog

## 0.42.4

Three new patches in the *Patches* tab work on Protective Wards' wards, each off until the server sets it. *Wards repair the whole connected base* (`Patches.WardNetworkRepair`): starting repair at a ward with its alt-use starts it on every active ward chained to it by overlapping areas, and each ward repairs its own area, one damaged piece every `Patches.WardRepairSeconds` (10), until nothing is damaged; every player sees how many pieces each ward has left. Protective Wards' own repair reaches only the wards touching the one used, one piece every 10 s for all of them, and stops when its player walks away. *Wards repair by themselves* (`Patches.WardAutoRepair`): every active ward does the same without being started, by Protective Wards' own repair rules. A ward repairs on the game that runs it, so only while a player is near the base; a ward run by a game without this Vigil is left out.

*Server password for wards* (`Patches.WardPassword`, set only in the dedicated server's own config and never sent to players): a player without access to an active ward is asked for a password at it, with Protective Wards' own prompt, and the server's password adds them to that ward's permitted list; with connected access that opens its whole base. A ward with its own password still takes that one, and a switched-off ward keeps Protective Wards' own way in. After a wrong guess a player waits 2 s before the server's password is checked for them again. Never put the password into a player's config, as Gale copies a profile's configs to everyone who uses it. The server reads its config at its start, so a new password needs a restart.

The full self-test tries all three wherever the player stands. The server picks a spot near the player where a small test ward keeps clear of every switched-on ward, and the test places its own objects there for a few seconds and removes them after; what a test cut short leaves, the server removes later. Repair: a wall loses 1 health and has to be repaired by its ward without a start, then again after the ward's alt-use, which has to start, show the count and stop. Password: at a ward of nobody the player knows, the prompt has to appear, a wrong password and the right one right after it have to be refused, and the right one after the wait has to put the player on the list. God mode, which opens every ward to a ward admin, is off while that runs. A self-test routine that runs over its time now puts back what it changed.

## 0.41.0

A new patch in the *Patches* tab, *Away players don't hold up sleep* (`Patches.AwaySleep`, off until the server sets it): a player whose game menu has stayed open for the server's minutes (`Patches.AwayMinutes`, 5) is away, and the night is skipped once everyone else is in bed. Somebody has to be in bed; moving the mouse in the menu does not count, closing it does. An away player out of bed gets no morning: no Rested, no good morning, no night counted. The players in bed are told who was away and not counted. The server and the players' games need this Vigil; a player without it is never away. The self-test, for one player alone: the game menu opened for real with seconds instead of minutes, the server told "away" and "back" as a self-test it never counts, the game's own "sleep now" tried on the player and refused, and, for admins, the server's own sleep check asked with the player up, away, and away with nobody in bed against a stand-in sleeper, all inside one call so the night is never skipped by a test, and the notice shown to the player alone.

## 0.40.0

The game starts quicker: Vigil's patcher holds mods' config saves while it starts. BepInEx rewrites a mod's whole settings file each time the mod binds a setting, so a mod with a thousand settings rewrites its file thousands of times while it loads; StartupAccelerator's delayed config save misses a mod that sets BepInEx's save switch itself, and settings bound while the main menu sets up. Now a save only marks its file until the menu shows, and then each file is written once. The files end up as they would have without the hold: a file about to be read back or opened by another mod is written first, and a file a mod writes itself stays as it wrote it. A mod that opens, checks for or copies a held settings file itself, past BepInEx, gets it written first too, so it reads what it would have, and the startup report names it. A mod's file watcher looking for its file is no mod reading it, so a watched file stays held and the watcher sees it change once, at the menu. On for a player's game (`Startup.HoldConfigSaves`), off on a dedicated server unless `Startup.HoldConfigSavesOnServer` is set. *Health > Startup*, the startup report and the self-test say how many saves were held for which files, and check each written file against its settings. If the game crashes before its menu, the files stay as they were before that start.

A mod checking for an optional mod that is not installed makes BepInEx search every folder under the plugins folder for its DLL, on every ask. While the game starts, Vigil's patcher remembers a search that found nothing and answers the next ask for the same assembly in the same folder at once; from the menu on, every search runs again, and an assembly already loaded is found as before. On for a player's game (`Startup.RememberFailedLookups`), off on a dedicated server unless `Startup.RememberFailedLookupsOnServer` is set. The startup report and *Health > Startup* say how many searches were spared, for which assemblies.

Joining is quicker with Jotunn: when the server's synced settings arrive, Jotunn checks each one with the file's list of keys, which BepInEx copies whole for every setting, seconds for thousands of settings. A new patch in the *Patches* tab, *Jotunn's config sync with one copy of the keys*, hands Jotunn one copy of each file's keys per sync, which answers every check the same way; its first answers are asked both ways, and Jotunn's own way comes back at a disagreement. Outside Jotunn's sync the keys are BepInEx's as before. It is each player's own switch (`Patches.JotunnConfigSync`, on), and it steps aside, saying so, if a Jotunn update changes that code.

The hook that watches patch changes found Harmony's rebuild method through Harmony's search by name, which lists every type of every assembly; it now asks Harmony's own assembly, so no lookup in Vigil goes through that search any more.

## 0.39.1

*Skip winter* works again with Seasonality 3.8.5, which moved its next-season rule; it stepped aside there. From 3.8.5 every game turns the season itself, so a player without this Vigil sees winter for up to 2 s at the end of fall, until the server's spring reaches them; the server needs this Vigil for the patch to hold.

Vigil starts quicker. It looked up about 80 of the game's types by name with Harmony's lookup, which lists every type of every loaded assembly on each call and, for a name not found, makes a mod with a type that cannot load log an error; each assembly is now asked for the one name and the answer kept. The startup report now splits Vigil's own start into its steps, and names the longest quiet stretches of the start: every log line until the menu shows is kept with its time in `Vigil-startup.tsv`, and each stretch of 2 s or more without one is listed with the plugin or part of the start under way and the lines either side; a stretch spent in a batch of deferred patches says so.

## 0.39.0

Vigil now comes with a patcher, `Vigil.Preloader.dll` in `BepInEx/patchers`, which BepInEx starts before any plugin. It times the game's start: Unity's own start, BepInEx and the patchers, each plugin from BepInEx's line that it is loading to the next plugin's, StartupAccelerator's patching, and the menu (a dedicated server: until its world is up). *Health > Startup* shows the last start, its slowest plugins (all of them on request), the last join to a world with its handshake and slowest messages, and the bundle cache; each start writes `Vigil-startup.txt` and `Vigil-startup.tsv`, and `vigil startup` prints the same in the console. The bundle cache, on a player's game by default (`Startup.BundleCache`) and off on a dedicated server (`Startup.BundleCacheOnServer`): the first time a mod loads an asset bundle packed with LZMA, which Unity has to unpack whole on every load, the bundle loads as before and Unity writes an LZ4 copy of it into `BepInEx/cache/Vigil/bundles`; later starts load that copy, which reads only what is used. A copy is checked against the original's asset names when it loads and refused for good if it fails or differs, and the bundle then loads the usual way in the same call. Copies unused for 30 days go (`Startup.BundleCacheKeepDays`), and *Reset the cache* or `vigil startup reset` empties the cache at the next start. The self-test checks the timeline, the patcher's hook in Unity's bundle loading, that every LZMA bundle came from a copy or got one made, that the copies matched, and a reset. The package now holds `plugins/Vigil.dll` and `patchers/Vigil.Preloader.dll`.

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
