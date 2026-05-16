# LC Late Join

Experimental BepInEx mod for Lethal Company landed late joining.

This is not a fork of VeryLateCompany's RPC replay approach. The mod keeps the Steam lobby alive, only approves late joins during stable phases, and sends a landed-world snapshot to the joining client after that client regenerates the current moon.

## Current Sync Coverage

- Connection approval after the game starts, gated by ship state.
- Steam lobby stays joinable while landed and not leaving.
- Joining client regenerates the current level with host seed, moon, mold, and weather.
- Host sends targeted landed snapshots for:
  - round phase and ship state
  - time, quota, credits, early-leave vote state
  - factory power
  - player slot state, positions, suits
  - grabbable item position, parent space, scrap value, pocket/held flags, battery state
  - door lock/open state
  - animated trigger bool state
  - enemy position, behavior state, HP, dead state, target player

## Build

```powershell
dotnet build -c Release
```

The output DLL is:

```text
bin\Release\netstandard2.1\LC_LateJoin.dll
```

Copy it to the game's BepInEx plugins folder for manual testing.

## Test Notes

For the first pass, test in this order:

1. Host a lobby, start a moon, wait until the ship fully lands.
2. Join from a second client.
3. Check that the joining player spawns inside the ship after loading.
4. Verify time, weather, doors, breaker power, held items, and visible scrap values.
5. Walk inside the facility and check enemy positions/door states.

Enemy AI has many subclass-specific private fields, so the first version syncs common `EnemyAI` state only. If a specific enemy type still desyncs, add a small per-enemy snapshot for that subclass rather than expanding the common snapshot blindly.
