# 更好的存档 BetterSaves

中文文档。英文版请见 [README.md](./README.md)。

`BetterSaves` 是一个《杀戮尖塔 2》模组，用来在原版存档和模组存档之间进行互通。

当前版本：`0.1.2`

## 功能

- 原版存档和模组存档双向同步
- 只启用 `BetterSaves` 时，游戏会加载原版存档
- 启用其他玩法模组时，游戏会加载模组存档
- 游戏设置中可切换两种同步模式
- 档案界面会标注当前读取的是原版存档还是模组存档

## 同步模式

游戏设置中的 `BetterSaves互通模式` 提供两种模式：

- `仅同步当前局`
  只同步当前正在进行中的单人 run 存档
- `完整同步`
  同步整个存档位下的可支持数据

提示：

- 在原版和模组之间切换前，建议先回主菜单或退出游戏，并等待 2 到 3 秒，让最后一次同步落盘
- 如果某些大型玩法模组会往存档里写原版不认识的扩展数据，原版可能仍然无法正确读取这些被扩展过的存档

## 当前行为

- 只开启 `BetterSaves`
  游戏按原版环境处理，读取原版存档
- 开启 `BetterSaves` 加其他玩法模组
  游戏按模组环境处理，读取模组存档
- `BetterSaves` 会在两边之间做同步

## 安装

把 `BetterSaves` 文件夹放到游戏目录下的 `mods` 目录：

```text
Slay the Spire 2\mods\BetterSaves
```

目录中至少需要这些文件：

- `BetterSaves.dll`
- `BetterSaves.json`
- `BetterSaves.pck`

## 配置

运行后配置会保存到：

```text
%AppData%\SlayTheSpire2\mods\BetterSaves\config.json
```

一般不需要手动修改，直接在游戏设置中切换即可。

## 已知限制

- 多人当前局存档同步目前已临时禁用
  原因是旧的多人输入状态可能导致宝箱房黑屏
- 因此当前更推荐把 `BetterSaves` 视为单人存档互通模组
- 模组环境下如果有会深度改写存档格式的模组，原版兼容性无法完全保证

## 更新记录

详见 [CHANGELOG.md](./CHANGELOG.md)。
