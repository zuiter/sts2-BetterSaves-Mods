# BetterSaves

Chinese documentation is available in [README_ZH.md](./README_ZH.md).

`BetterSaves` is a Slay the Spire 2 mod that keeps vanilla saves and modded saves in sync.

Current version: `0.1.4`

## Features

- Bidirectional sync between vanilla and modded save slots
- Vanilla compatibility mode when only `BetterSaves` is enabled
- In-game sync mode setting with `Only Current Run` and `Full Sync`
- Profile screen badge that shows whether the current slot is using vanilla or modded saves

## Sync Modes

- `Only Current Run`
  Syncs only the currently active single-player run files.
- `Full Sync`
  Syncs the supported save data for the whole profile slot.

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
- Steam cloud timing can still affect how quickly newly written files appear on the other side, but the mod now performs additional reconciliation to reduce this.

## Changelog

See [CHANGELOG.md](./CHANGELOG.md).
