## 创意工坊描述

- 修改了边缘模拟经营框架模组的补货逻辑，现在应该不会出现不补货的情况了（或者说现在殖民者补货补得十分疯狂）。
- 使货架菜单显示仓库内的物品和数量，不用再来回翻找自己家里有什么东西了。
- 现在搬运机可以进行补货了。请在“经商管理”页面 → “店员” → “店员配置” → “岗位列表” → “补货”页面中，将搬运机加入补货岗位。

模组未经太多测试，欢迎进行反馈。

模组反馈QQ群：672646837

## Mod information

| | |
|---|---|
| **Author** | yyyyy |
| **Package ID** | `yyyyy.rsmf.restockfix` |
| **Game version** | 1.6 |
| **License** | MIT |

## Features

- More reliable auto-restock with threshold checks, reservation tracking, queue self-healing, and silent background wake.
- Inventory filter and warehouse stock column in the goods manager.
- Optional [Adaptive Storage Framework](https://steamcommunity.com/workshop/filedetails/?id=3033905855) compatibility.
- Colony hauling mechs can participate in restock when assigned the shop Restocker role.

## Requirements

- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)
- RimSimManagementFramework（边缘模拟经营框架）
- Optional: Adaptive Storage Framework

## Install

1. Download this repository or a release archive.
2. Place the folder under `RimWorld/Mods/`.
3. Enable **RSMF Restock Fix** in the mod list.

## Load order

```text
Harmony
RimSimManagementFramework
Adaptive Storage Framework   (optional)
RSMF Restock Fix
```

## Compatibility

- Safe to add mid-save.
- Does not replace RSMF; load after the framework.
