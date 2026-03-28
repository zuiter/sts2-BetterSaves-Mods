# Changelog

## 0.1.2 - 2026-03-28

- Added a compatibility guard for treasure room hand visibility updates so stale multiplayer peer-input state no longer black-screens chest interactions.

## 0.1.1 - 2026-03-28

- Temporarily disabled syncing `current_run_mp.save` and `current_run_mp.save.backup` to prevent stale multiplayer state from causing treasure room black-screen issues.

## 0.1.0 - 2026-03-28

- Added bidirectional save interop between vanilla and modded Slay the Spire 2 profiles.
- Added an in-game sync mode setting with `Only Current Run` and `Full Sync` options.
- Added vanilla compatibility mode so enabling only `BetterSaves` loads the vanilla save path.
- Added a profile screen badge that shows whether the current profile is using vanilla or modded saves.
- Unified the settings row styling across vanilla and modded sessions and stabilized paginator behavior.
- Cleaned up obsolete experimental settings UI code paths and removed the unused custom arrow/audio prototype.
