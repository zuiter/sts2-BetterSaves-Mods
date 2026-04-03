# BetterSaves

English documentation. For Simplified Chinese, see [**简体中文**](README_ZH.md).

![Version](https://img.shields.io/badge/Version-0.1.6-blue.svg)
![Game](https://img.shields.io/badge/Slay_The_Spire_2-Mod-red.svg)
![Platform](https://img.shields.io/badge/Platform-Windows%20|%20Linux-lightgrey.svg)

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

### Linux (Ubuntu)

Linux uses the same mod folder layout as Windows, but the game executable and Godot binary names vary by distro/package.

1. Download the latest `BetterSaves-[version].zip` from the **Releases** page.
2. Extract the archive and copy the inner `BetterSaves` folder to:
   ```
   <Slay the Spire 2>/mods/
   ```
3. Start the game normally from Steam or your local executable.
4. The mod will be enabled automatically on launch.

> **Compatibility note:** All players must use the same mod version. Local settings may differ safely; only the host's configured limit determines how many players can actually join the lobby.

> **Linux troubleshooting:** If the mod fails during startup with a Harmony / `mm-exhelper.so` error mentioning `_Unwind_RaiseException`, make sure your system runtime libraries are available to the game process. Installing `libgcc-s1`, `libstdc++6`, and `libunwind8` is usually sufficient.
>
> **Sandboxed Linux (NixOS, Flatpak, etc.):** If your Steam runs in a sandbox, system libraries may not be visible to the game process even when installed. Add the following to your Steam launch options:
> ```
> LD_PRELOAD+=":/path/to/libgcc_s.so.1" %command%
> ```
> Replace `/path/to/` with the actual path on your system. For NixOS with nix-ld:
> ```
> LD_PRELOAD+=":/run/current-system/sw/share/nix-ld/lib/libgcc_s.so.1" %command%
> ```


## Known Limitations

- Multiplayer current-run interop is more fragile than single-player interop because modded and vanilla multiplayer sessions may use different local player identifiers.
- Steam cloud timing can still affect how quickly newly written files appear on the other side, but the mod performs additional reconciliation to reduce this.

## Changelog

See [CHANGELOG.md](./CHANGELOG.md).
