# RSMF 商品管理增强 (Goods Manager Plus)

RimWorld 1.6 companion mod for **RimSimManagementFramework** that adds warehouse inventory visibility to the goods manager and lets colony hauling mechs restock.

> 前身为 "RSMF Restock Fix"。自 2026-07 主模组更新起，补货修复已由框架原生实现，本模组于 2026-07-19 移除全部补货修复补丁并改名转型。packageId `yyyyy.rsmf.restockfix` 不变。

## 描述

- 使货架菜单显示仓库内的物品和数量，不用再来回翻找自己家里有什么东西了。
- 现在搬运机可以进行补货了。请在“经商管理”页面 → “店员” → “店员配置” → “岗位列表” → “补货”页面中，将搬运机加入补货岗位。
- 补货修复已由边缘模拟经营框架原生实现（2026-07 更新），本模组不再包含补货修复补丁。

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

- Inventory filter and warehouse stock column in the goods manager, with capacity-aware batch select/clear.
- Colony hauling mechs (e.g. Lifter) can participate in restock when assigned the shop Restocker role.
- Optional [Adaptive Storage Framework](https://steamcommunity.com/workshop/filedetails/?id=3033905855) compatibility.
- Does not modify original RSMF files.

## Requirements

- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)
- RimSimManagementFramework（边缘模拟经营框架）
- Optional: Adaptive Storage Framework

## Install

1. Download this repository or a release archive.
2. Place the folder under `RimWorld/Mods/`.
3. Enable **RSMF 商品管理增强 (Goods Manager Plus)** in the mod list.

## Load order

```text
Harmony
RimSimManagementFramework
Adaptive Storage Framework   (optional)
RSMF 商品管理增强 (Goods Manager Plus)
```

## Compatibility

- Safe to add mid-save.
- Does not replace RSMF; load after the framework.
