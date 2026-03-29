# 更好的存档 BetterSaves

English documentation. For Chinese,
[**简体中文**](README_ZH.md)

![Version](https://img.shields.io/badge/Version-0.1.2-blue.svg)
![Game](https://img.shields.io/badge/Slay_The_Spire_2-Mod-red.svg)
![Platform](https://img.shields.io/badge/Platform-Windows%20-lightgrey.svg)

`BetterSaves` is a Slay the Spire 2 mod that keeps vanilla saves and modded saves in sync.

Current version: `0.1.2`

## Features

- Bidirectional sync between vanilla and modded save slots
- When only `BetterSaves` is enabled, the game loads the vanilla save path
- When other gameplay mods are enabled, the game loads the modded save path
- An in-game setting lets players switch between two sync modes
- The profile screen shows whether the current slot is using a vanilla or modded save

## Sync Modes

The in-game `BetterSaves Interop Mode` setting provides two modes:

- `Only Current Run`
  Syncs only the active single-player run save
- `Full Sync`
  Syncs the supported data under the entire save slot

Notes:

- Before switching between vanilla and modded play, it is recommended to return to the main menu or quit the game and wait 2 to 3 seconds so the final sync can finish
- If a large gameplay mod writes custom data that vanilla does not understand, vanilla may still fail to read that expanded save correctly

## Current Behavior

- With only `BetterSaves` enabled
  The game behaves like vanilla and loads the vanilla save
- With `BetterSaves` plus other gameplay mods enabled
  The game behaves like modded and loads the modded save
- `BetterSaves` syncs data between the two sides

## Installation

Place the `BetterSaves` folder under the game's `mods` directory:

```text
Slay the Spire 2\mods\BetterSaves
```

The folder should contain at least:

- `BetterSaves.dll`
- `BetterSaves.json`
- `BetterSaves.pck`

## Configuration

The config file is saved at:

```text
%AppData%\SlayTheSpire2\mods\BetterSaves\config.json
```

You normally do not need to edit it manually. The in-game setting is enough.

## Known Limitations

- Multiplayer current-run syncing is currently disabled
  This was temporarily turned off because stale multiplayer input state could cause treasure room black screens
- For now, `BetterSaves` should be treated primarily as a single-player save interop mod
- If a modded environment contains mods that heavily rewrite the save format, vanilla compatibility cannot be guaranteed

## Changelog

See [CHANGELOG.md](./CHANGELOG.md).
