# BetterSaves

English documentation. For Simplified Chinese, see [**简体中文**](README_ZH.md).

![Version](https://img.shields.io/badge/Version-0.1.5-blue.svg)
![Game](https://img.shields.io/badge/Slay_The_Spire_2-Mod-red.svg)
![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey.svg)

`BetterSaves` is a Slay the Spire 2 mod that keeps vanilla saves and modded saves in sync.

## Features

- Bidirectional sync between vanilla and modded save slots
- Vanilla compatibility mode when only `BetterSaves` is enabled
- In-game sync mode setting with `Save Only`, `Data Only`, and `Full Sync`
- Profile-screen badge that shows whether the current slot is using vanilla or modded saves

## Sync Modes

- `Save Only`
  Syncs only the currently active single-player run save files.
- `Data Only`
  Syncs progression data such as timeline, discoveries, and run history, but does not sync in-progress run save files.
- `Full Sync`
  Syncs both the active run and the supported profile data for the whole slot.

## Behavior

- When only `BetterSaves` is enabled, the game loads the vanilla save path.
- When other gameplay mods are enabled, the game loads the modded save path.
- Switching modes is safest after returning to the main menu or waiting a moment before quitting.

## Installation

Place the mod in:

```text
Slay the Spire 2/mods/BetterSaves
```

## Config

The config file is stored at:

```text
%AppData%/SlayTheSpire2/mods/BetterSaves/config.json
```

## Known Limitations

- Multiplayer current-run syncing is still disabled.
- Steam cloud timing can still affect how quickly newly written files appear on the other side, but the mod performs additional reconciliation to reduce this.

## Changelog

See [CHANGELOG.md](./CHANGELOG.md).
