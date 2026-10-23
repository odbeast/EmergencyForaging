# Emergency Foraging

Emergency Foraging adds a last-resort food behavior for hungry colonists in RimWorld 1.4.

When a colonist becomes very hungry and the normal food job cannot find suitable accessible food, the mod searches a configurable radius for fully grown, reachable plants that provide edible plant food. It can use berries, fruit trees, rice, potatoes, corn and compatible modded crops.

The mod excludes raw meat, corpses, animal products, drugs, immature plants and unreachable plants. It respects food policies, allowed areas, forbidden plants, reservations and pathfinding.

Harvesting reuses RimWorld's standard plant-harvesting driver, preserving normal work time, Plants skill experience, yield, sounds, effects and harvest failures. After harvesting, the colonist is given a standard eating job for the collected food.

If no suitable plant is found, the mod displays **No forageable food nearby** and includes diagnostic details for nearby rejected plants.

## Requirements

- RimWorld 1.4
- Harmony

## Installation

Copy the `EmergencyForaging` folder into `RimWorld/Mods`. Enable Harmony before Emergency Foraging in the mod list, then restart the game.

The compiled assembly is included in `Assemblies`. The source project and C# code are included in `Source`.

## Settings

The mod settings allow you to configure the search radius and hunger threshold. The default values are 30 tiles and 20% food, respectively. The maximum search radius is 100 tiles.

## Current status

Version 0.2.3 beta. Compiled against the supplied RimWorld 1.4 assemblies. The code has been metadata-checked and compiled successfully, but the final behavior should be tested in-game with the user's full mod list.
