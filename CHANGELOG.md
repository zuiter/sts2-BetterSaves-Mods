# Changelog

## 0.4.2 - 2026-04-28

### English

- Added a `Sync Off` mode that keeps BetterSaves loaded but disables automatic copying, deletion, startup reconciliation, file-watcher mirroring, and first-sync prompts between vanilla and modded save folders.
- Documented that saves made after enabling `Sync Off` stay only on the save side the game is currently using, and that players should save and wait for sync before turning it off if they need both sides to stay aligned.

### 中文

- 新增 `关闭同步` 模式：保留 BetterSaves 加载与界面，但关闭原版存档与模组存档之间的自动复制、删除、启动对账、文件监听镜像和首次同步弹窗。
- 补充说明：启用 `关闭同步` 后，之后产生的保存只会保存在游戏当前使用的存档侧；如果需要两侧保持一致，应先保存并等待同步完成后再关闭。

## 0.4.1 - 2026-04-28

### English

- Hardened first-sync protection for legacy BetterSaves configs by migrating configs without a bootstrap state back into the pending first-sync flow instead of treating them as already resolved.
- Changed first-sync prompt dismissal to skip only the current session, so closing or skipping the prompt no longer permanently disables the first-sync confirmation and protection flow.
- Fixed the English sync-mode title on the modding settings screen being auto-shrunk by the game's `MegaLabel` sizing logic, so it now matches the Chinese title's display style more closely.

### 中文

- 强化旧版 BetterSaves 配置的首次同步保护：当配置中缺少 bootstrap 状态时，会迁移回待确认状态，而不是直接视为已经处理完成。
- 调整首次同步弹窗关闭/跳过逻辑：现在只跳过本次启动，不会永久关闭首次同步确认与保护流程。
- 修复模组设置页面中英文互通模式标题被游戏原生 `MegaLabel` 自动缩小的问题，使英文显示效果更接近中文标题。

## 0.4.0 - 2026-04-19

### English

- Fixed `Save Only` startup slot restoration so BetterSaves now preserves the slot currently pointed to by `profile.save` instead of auto-jumping to another "richer" slot.
- Fixed the "first launch after switching sides" delay by reloading the current profile from disk after `SyncCloudToLocal` only when BetterSaves actually changed synced files during that startup reconciliation.
- Changed `Data Only` launches to use automatic direction selection instead of always forcing `modded -> vanilla`, so new timeline/progression changes can flow the correct way on the first switch.
- Added semantic `progress.save` source selection that compares concrete epoch, character, unlock, and achievement sets, reducing cases where stale progress overwrote newly unlocked timeline data.
- Added guarded `Data Only` startup protection for local single-player current-run files so cloud sync no longer strips a local in-progress run unless there is evidence that the run has already been completed and recorded in history.
- Hardened progress semantic comparisons against null/default states so save interop no longer fails to initialize when a `progress.save` cannot be parsed or is temporarily unavailable.
- Reduced duplicate watcher-driven sync work by suppressing repeated processing of unchanged file snapshots and short-window duplicate delete events, which cuts down repeated `Mirrored ...` logs and lowers file-watcher overflow risk.
- Cleaned up stale bootstrap prompt UI code that no longer had an active hook, reducing maintenance noise.

### 中文

- 修复“仅同步存档”模式下的启动槽位恢复问题：现在会保留 `profile.save` 当前指向的槽位，不再因为别的槽位看起来更“成熟”就自动跳过去。
- 修复切换到另一边后“第一次启动看不到同步结果、要第二次启动才显示”的问题：`SyncCloudToLocal` 完成后，如果 BetterSaves 在本次启动里确实改动了同步文件，会受控地重载当前 profile 数据。
- 调整“仅同步数据”模式在带模组启动时的选源方向，不再固定强制 `modded -> vanilla`，而是改为自动判定，让新产生的时间线/进度变化能够在第一次切边时按正确方向流动。
- 为 `progress.save` 增加语义级选源：现在会比较具体的 epoch、角色、解锁与成就集合，降低旧进度把新解锁的时间线数据反向覆盖掉的概率。
- 增加“仅同步数据”模式下的启动保护：当云同步错误移除本地单人当前局时，BetterSaves 只会在没有完成记录证据的情况下恢复本地当前局，避免重进后误丢进度。
- 强化 `progress.save` 语义比较的空安全处理，避免某些 `progress.save` 临时不可读或解析失败时，直接导致 save interop 初始化失败。
- 增加 watcher 去重/防抖：对同一路径、同一快照的重复变更和短时间重复删除事件只处理一次，减少重复 `Mirrored ...` 日志并降低文件监听器溢出的风险。
- 清理 bootstrap prompt UI 中已失效的残留逻辑，简化代码路径并减轻后续维护负担。

## 0.3.0 - 2026-04-15

### English

- Added a first-sync confirmation flow that asks players to choose whether vanilla saves or modded saves should be authoritative the first time BetterSaves manages an existing profile.
- Locked the confirmed first-sync direction to the current profile for a short reconciliation window so follow-up save hooks and delayed scans cannot immediately undo the player's choice.
- Expanded pending first-sync protection beyond `progress.save` and `prefs.save` to also cover `current_run.save`, run history, and replay files before the player confirms a direction.
- Added first-sync automatic backups before startup reconciliation so players have a recovery snapshot if a first-time import choice needs to be reversed manually.
- Documented the backup location at `%AppData%/SlayTheSpire2/mods/BetterSaves/backups/first-sync-<timestamp>/` so players can find and restore their first-sync snapshots more easily.

### 中文

- 增加首次同步确认流程：当 BetterSaves 第一次接管已有存档槽位时，会询问玩家以原版存档还是模组存档为准。
- 玩家确认首次同步方向后，会在当前槽位上短时间锁定该方向，避免后续保存钩子或延迟扫描立刻把玩家的选择反向覆盖。
- 将首次确认前的保护范围从 `progress.save` / `prefs.save` 扩展到 `current_run.save`、历史记录和 replay 文件，避免玩家确认前这些关键文件先按默认方向同步。
- 在启动对账前加入首次同步自动备份，如果玩家需要手动回退首次导入选择，可以从备份快照恢复。
- 在文档中补充首次同步备份目录 `%AppData%/SlayTheSpire2/mods/BetterSaves/backups/first-sync-<时间戳>/`，方便玩家自行查找和恢复备份存档。

## 0.2.2 - 2026-04-14

### English

- Changed startup and delayed reconciliation to follow the current launch mode's preferred sync direction instead of always using automatic timestamp-based resolution.
- This lets modded launches consistently treat the modded side as authoritative during bootstrap, so an older `modded-save` can still replace a newer vanilla save when that is the expected direction.
- Prevented startup scans from silently re-promoting the vanilla side just because it was edited more recently before the next modded launch.

### 中文

- 调整启动扫描与延迟对账逻辑，使其跟随当前启动模式的同步方向，而不再一律使用基于时间戳的自动判定。
- 这样在带其他模组启动时，BetterSaves 会更稳定地把模组档视为启动阶段的权威源；即便原版档后来被手动改得更新，也不会轻易把应由模组档覆盖回去的情况判断反了。
- 避免在下一次模组启动前，仅因为原版档时间戳更新，就让启动阶段再次把原版侧悄悄提升为优先来源。

## 0.2.1 - 2026-04-12

### English

- Moved the single-player overwrite protection earlier into source selection so BetterSaves now prefers the richer side before timestamp-based reconciliation can choose a low-data save.
- Strengthened the low-data save guard for `progress.save` and `prefs.save` so first-time modded save creation is less likely to push a mostly empty profile back into an existing vanilla save.
- Added diagnostics that log when BetterSaves explicitly prefers a richer single-player source over a low-data counterpart during reconciliation.

### 中文

- 将单人档保护前移到“选源阶段”，让 BetterSaves 在进入基于时间戳的对比前，就优先选择内容更完整的一侧作为同步源。
- 进一步强化 `progress.save` 与 `prefs.save` 的低数据档保护，降低首次创建模组单人档时把近乎空白的档案反向覆盖已有原版档的概率。
- 增加新的诊断日志：当 BetterSaves 在对账过程中明确选择“更成熟的单人档”而拒绝低数据 counterpart 时，会直接记录这一决策。

## 0.2.0 - 2026-04-10

### English

- Restored Steam cloud uploads for mirrored counterpart save files so BetterSaves can keep cross-device vanilla/modded interop in sync again.
- Disabled BetterSaves-driven Steam cloud deletions so mirrored cleanup can no longer remove files that another device still needs.
- Rebalanced cloud behavior to preserve cross-device consistency by allowing cloud writes but preventing destructive cloud-side cleanup.

### 中文

- 恢复 BetterSaves 对镜像后 counterpart 存档的 Steam 云上传，使跨设备切换时原版档与模组档的互通结果能够再次跟随云端同步。
- 禁用 BetterSaves 主动删除 Steam 云文件的行为，避免本地镜像清理把另一台设备仍然需要的云端存档一起删掉。
- 重新平衡云存档策略：允许写云、禁止删云，在保留互通能力的同时降低跨设备同步被破坏的风险。

## 0.1.9 - 2026-04-10

### English

- Added a conservative first-session bootstrap pass for players who install `BetterSaves` after already creating vanilla and/or modded profile data.
- Startup and delayed bootstrap scans now prefer copying a clearly richer vanilla profile into modded saves, copy modded data back only when vanilla has no meaningful single-player data, and otherwise skip automatic overwrite when both sides already contain real progress.
- Added bootstrap diagnostics that log whether BetterSaves copied vanilla to modded, copied modded to vanilla, or deliberately skipped a risky first-sync overwrite.

### 中文

- 增加首次接入 `BetterSaves` 时的保守引导逻辑，专门处理玩家在安装本模组之前就已经存在原版档和/或模组档的数据场景。
- 启动扫描与延迟扫描现在会优先把明显更完整的原版档同步到模组档；只有在原版侧几乎没有有效单人数据时才会反向采用模组档；如果两边都已有真实进度且没有明显唯一权威源，则会跳过自动覆盖，避免首轮互通直接冲掉已有存档。
- 增加首轮引导日志，明确记录 BetterSaves 在首次同步时是选择了原版→模组、模组→原版，还是为了规避风险而主动跳过自动覆盖。

## 0.1.8 - 2026-04-09

### English

- Added a low-data single-player save guard so a freshly created modded `progress.save` / `prefs.save` can no longer overwrite a much richer vanilla profile on first modded startup.
- Added diagnostics that log both sides' single-player save maturity when BetterSaves blocks one of these destructive first-sync overwrites.

### 中文

- 增加“低数据单人档保护”，避免首次带模组启动时新生成的 modded `progress.save` / `prefs.save` 反向覆盖内容更完整的原版单人存档。
- 增加这类首轮覆盖保护的诊断日志；当 BetterSaves 阻止这类危险同步时，会同时记录两侧单人档的成熟度信息，方便后续继续排查玩家反馈。

## 0.1.7 - 2026-04-08

### English

- Added bidirectional multiplayer current-run interop between modded and vanilla save paths.
- Re-enabled syncing for current_run_mp.save and current_run_mp.save.backup under save-sync modes.
- Added multiplayer local-player ID normalization when mirroring multiplayer current runs between modded and vanilla environments.
- Improved handling of broken multiplayer run artifacts so .VAL.corrupt files are not treated as normal sync targets.
- Added extra diagnostics around multiplayer current-run syncing to make future compatibility issues easier to trace.
- Fixed vanilla-compatibility sync direction so when only `BetterSaves` is enabled, profile data can mirror in the correct direction instead of getting stuck effectively one-way.
- Fixed compatibility-mode current-run deletion propagation so abandon/cleanup can clear both save roots when the active direction is auto-resolved.
- Suppressed the bottom-right modded warning label in vanilla compatibility mode for the 0.99.1 release branch.
- Pruned debug-only dev-console commands in vanilla compatibility mode so the vanilla console no longer exposes modded-only commands.

### 中文

- 增加模组多人当前局与原版多人当前局之间的双向互通。
- 在“存档类同步”模式下重新启用 current_run_mp.save 与 current_run_mp.save.backup 的同步。
- 增加多人当前局在模组环境与原版环境之间互通时的本机玩家 ID 规范化处理。
- 改善损坏多人存档的处理方式，不再把 .VAL.corrupt 这类文件当作正常同步目标。
- 增加多人当前局同步的额外诊断日志，方便后续继续定位兼容性问题。
- 修复只启用 `BetterSaves` 时的原版兼容模式同步方向问题，避免档案互通卡成“看起来只剩单向同步”。
- 修复兼容模式下当前局删除传播的问题，使放弃/清理当前局时在自动方向判定下也能同时清理两边存档根。
- 针对 0.99.1 正式版兼容模式，隐藏右下角的 modded 提示标签。
- 在原版兼容模式下移除控制台中的 debug-only 命令，避免原版控制台继续暴露模组专用命令。

## 0.1.5 - 2026-03-30

### English

- Replaced the old two-mode sync setup with three modes: `Save Only`, `Data Only`, and `Full Sync`.
- Split active-run syncing from profile-data syncing so players can choose to mirror only the current run or only progression data such as timeline, discoveries, and run history.
- Refined save hooks and path filtering for the three-mode split, reducing accidental writes when a mode should only sync one category of data.
- Stabilized profile-slot handling across restarts, vanilla/modded mode switches, and cloud-sync timing.
- Fixed destructive slot-realignment edge cases in `Save Only` mode so current-run files are no longer moved or deleted from the wrong slot.
- Continued cleanup of placeholder profile slots and ghost-profile behavior.

### 中文

- 将原本的双模式互通扩展为三种模式：`仅同步存档`、`仅同步数据`、`完整同步`。
- 将当前局存档同步与元进度数据同步彻底拆分，玩家现在可以只同步当前局，或只同步时间线、发现、历史记录等数据。
- 重构三档模式下的保存钩子与路径过滤逻辑，降低“本来只想同步一类数据，却误同步到另一类数据”的概率。
- 进一步稳定重启游戏、切换原版/模组模式以及云同步时的存档槽位恢复行为。
- 修复 `仅同步存档` 模式下一些会误搬移或误删除当前局文件的边界情况，避免把当前局从错误槽位移走。
- 持续清理空白占位槽位与“幽灵存档”行为，进一步降低幽灵存档再次出现的概率。

## 0.1.4 - 2026-03-30

### English

- Fixed stale `current_run.save` files being restored after abandoning a run.
- Stopped transient delete events during save-file replacement from wiping the counterpart save.
- Reworked profile-slot handling between vanilla and modded sessions so slot selection is more stable during mode switching and cloud sync.
- Added conservative recovery for placeholder profile slots so the mod can return to a mature save slot instead of sticking to an empty one.
- Changed preferred-slot tracking to follow real save activity, reducing mismatches between vanilla and modded sessions after switching modes.
- Unified preferred-slot updates so successful saves in either mode align both sides to the same profile slot for interop.
- Added cleanup for placeholder empty profile slots to prevent phantom empty saves such as an accidental `profile3` from being mirrored or recreated.
- Fixed cases where restarting the game could jump to the wrong slot instead of staying on the slot selected before quitting.
- Stopped invalid slot-tracking sources such as profile list enumeration, profile-scoped path enumeration, and broad settings sync passes from overwriting the active slot selection.
- Adjusted startup recovery to prefer the current valid `profile.save` selection and only fall back when the current slot is clearly just a placeholder save.

### 中文

- 修复放弃游戏后旧的 `current_run.save` 被重新同步回来，导致无法正常开始新游戏的问题。
- 修复保存文件在重命名替换时产生的临时删除事件误删另一侧存档的问题。
- 重构原版模式与模组模式之间的存档槽位处理逻辑，降低切模式与云同步时跳错槽位的概率。
- 增加对空白占位存档槽的保守恢复逻辑，避免卡在空槽位而无法回到已有进度的成熟槽位。
- 调整首选槽位记录方式，使其跟随真实保存行为更新，减少原版模式和模组模式之间的槽位错位。
- 将原版与模组的首选槽位对齐到同一个实际使用中的存档槽位，提升两边互通时的稳定性。
- 增加空白占位存档槽清理，避免像误生成的 `profile3` 这类幽灵空档被镜像出来或反复重建。
- 修复重进游戏后可能自动跳到错误存档槽位，而不是停留在退出前所选存档的问题。
- 阻止档案列表枚举、profile 路径枚举以及过宽泛的设置同步流程误改当前槽位选择。
- 调整启动恢复逻辑，优先保留 `profile.save` 当前指向且有效的存档槽位，只有当前槽位明显是占位空档时才回退恢复。

## 0.1.3 - 2026-03-29

### English

- Changed the first-run default sync mode to `Full Sync`, so new installs start in full interop mode automatically.

### 中文

- 将首次安装时的默认同步模式改为 `完整同步`，使新安装的模组在首次加载时自动启用完整互通。

## 0.1.2 - 2026-03-28

### English

- Added a compatibility guard for treasure room hand-visibility updates so stale multiplayer peer-input state no longer black-screens chest interactions.

### 中文

- 为宝箱房中的手部可见性更新增加兼容保护，避免过期的多人输入状态导致开启宝箱时黑屏。

## 0.1.1 - 2026-03-28

### English

- Temporarily disabled syncing `current_run_mp.save` and `current_run_mp.save.backup` to prevent stale multiplayer state from causing treasure room black-screen issues.

### 中文

- 临时停用 `current_run_mp.save` 与 `current_run_mp.save.backup` 的同步，避免过期的多人状态导致宝箱房黑屏。

## 0.1.0 - 2026-03-28

### English

- Added bidirectional save interop between vanilla and modded Slay the Spire 2 profiles.
- Added an in-game sync mode setting with `Only Current Run` and `Full Sync` options.
- Added vanilla compatibility mode so enabling only `BetterSaves` loads the vanilla save path.
- Added a profile screen badge that shows whether the current profile is using vanilla or modded saves.
- Unified the settings row styling across vanilla and modded sessions and stabilized paginator behavior.
- Cleaned up obsolete experimental settings UI code paths and removed the unused custom arrow/audio prototype.

### 中文

- 增加《杀戮尖塔 2》原版档与模组档之间的双向存档互通。
- 增加游戏内同步模式设置，提供 `仅同步当前局` 与 `完整同步` 两种选项。
- 增加原版兼容模式，使只启用 `BetterSaves` 时会读取原版存档路径。
- 在存档界面增加标识，用于显示当前档位读取的是原版存档还是模组存档。
- 统一原版模式与模组模式下的设置项样式，并稳定分页切换交互。
- 清理已废弃的实验性设置界面代码，并移除未使用的自定义箭头与音效原型。
