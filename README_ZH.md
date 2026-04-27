# BetterSaves

中文文档。英文版请见 [**English**](README.md)。

![Version](https://img.shields.io/badge/Version-0.4.1-blue.svg)
![Game](https://img.shields.io/badge/Slay_The_Spire_2-Mod-red.svg)
![Platform](https://img.shields.io/badge/Platform-Windows%20|%20Linux-lightgrey.svg)

`BetterSaves` 是一个用于《杀戮尖塔 2》的存档互通模组，用来在原版存档与模组存档之间进行同步。

## 功能

- 原版存档槽与模组存档槽之间的双向同步
- 只启用 `BetterSaves` 时可进入原版兼容模式
- 游戏内可切换 `仅同步存档`、`仅同步数据`、`完整同步`
- 存档界面会标识当前槽位读取的是原版档还是模组档

## 同步模式

- `仅同步存档`
  只同步当前正在进行中的单人局存档文件。
- `仅同步数据`
  只同步时间线、发现、历史记录等数据，不同步当前局存档文件。
- `完整同步`
  同时同步当前局存档与该槽位下受支持的档案数据。

## 行为说明

- 只启用 `BetterSaves` 时，游戏会读取原版存档路径。
- 启用其他玩法模组时，游戏会读取模组存档路径。
- 切换模式前，最好先回到主菜单，或者退出前稍等片刻，让最后一次同步完成。

## 首次同步

- 当 BetterSaves 第一次接管一个已有存档槽位时，会先询问你这次应当以哪一侧为准：`以原版为准` 或 `以模组为准`。
- 在首次对账开始前，BetterSaves 会自动创建一份备份快照，方便你在必要时手动恢复这个槽位。
- 在你确认首次同步方向前，BetterSaves 不会自动对该槽位受保护的单人关键文件执行同步覆盖。
- 如果你关闭或跳过首次同步弹窗，BetterSaves 只会在本次启动中暂时跳过导入，下次启动仍会再次询问。
- 对于在首次同步状态字段加入前创建的旧配置，BetterSaves 会重新进入待确认流程，避免把旧存档误认为已经确认处理过。

### 备份位置

首次同步的自动备份会保存在：

```text
%AppData%/SlayTheSpire2/mods/BetterSaves/backups/first-sync-<时间戳>/
```

在这个目录下，BetterSaves 会保留首次对账前对应的 `profile.save`、`settings.save`、`profile1~3` 以及 `modded/profile1~3` 数据，方便你手动恢复。

## 安装位置

将模组放到：

```text
Slay the Spire 2/mods/BetterSaves
```

## 配置文件

配置文件位于：

```text
%AppData%/SlayTheSpire2/mods/BetterSaves/config.json
```

### Linux (Ubuntu)

Linux 的模组目录结构和 Windows 基本一致，但不同发行版里游戏可执行文件和 Godot 命令名可能不同。

1. 从 **Releases** 页面下载最新的 `BetterSaves-[version].zip` 压缩包。
2. 解压并将内部的 `BetterSaves` 文件夹整体复制到游戏的：
   ```
   <Slay the Spire 2>/mods/
   ```
3. 正常通过 Steam 或本地可执行文件启动游戏即可。
4. 启动游戏后，模组将自动启用。

> **兼容性说明：** 如果你在多台设备之间，或在原版/模组启动之间共享存档，建议每个安装位置都使用同一版本的 BetterSaves。

> **Linux 排错：** 如果模组启动时因为 Harmony / `mm-exhelper.so` 报 `_Unwind_RaiseException` 而初始化失败，通常是系统运行库没有被游戏进程正确看到。一般安装 `libgcc-s1`、`libstdc++6` 和 `libunwind8` 就够了。
>
> **沙箱化 Linux（NixOS、Flatpak 等）：** 如果你的 Steam 在沙箱中运行，即使已安装运行库也可能无法被游戏进程访问。请在 Steam 启动选项中添加：
> ```
> LD_PRELOAD+=":/path/to/libgcc_s.so.1" %command%
> ```
> 将 `/path/to/` 替换为你系统上的实际路径。NixOS + nix-ld 示例：
> ```
> LD_PRELOAD+=":/run/current-system/sw/share/nix-ld/lib/libgcc_s.so.1" %command%
> ```

## 已知限制

- 多人当前局互通比单人互通更脆弱，因为模组多人和原版多人在某些环境下会使用不同的本机玩家标识。
- Steam 云存档的时序仍可能影响另一侧新写入文件出现的时间，但模组已经加入额外对账逻辑来降低这一问题。

## 更新记录

请见 [CHANGELOG.md](./CHANGELOG.md)。
