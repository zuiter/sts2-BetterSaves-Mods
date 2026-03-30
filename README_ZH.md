# BetterSaves

中文文档。英文版请见 [**English**](README.md)。

![Version](https://img.shields.io/badge/Version-0.1.5-blue.svg)
![Game](https://img.shields.io/badge/Slay_The_Spire_2-Mod-red.svg)
![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey.svg)

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

## 已知限制

- 多人当前局同步仍然处于禁用状态。
- Steam 云存档的时序仍可能影响另一侧新写入文件出现的时间，但模组已经加入额外对账逻辑来降低这一问题。

## 更新记录

请见 [CHANGELOG.md](./CHANGELOG.md)。
