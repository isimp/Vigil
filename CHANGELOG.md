# Changelog

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
