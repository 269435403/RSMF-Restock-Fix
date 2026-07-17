using HarmonyLib;
using RimWorld;
using SimManagementLib.Pojo;
using SimManagementLib.SimDialog;
using SimManagementLib.SimThingClass;
using SimManagementLib.SimThingComp;
using SimManagementLib.Tool;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

namespace RSMFInventoryGoodsFilter
{
    internal sealed class GoodsManagerState
    {
        internal bool InventoryMode = true;
        internal int ObservedCacheGeneration = -1;
    }

    internal static class GoodsManagerPatches
    {
        private const string LogPrefix = "[RSMF Inventory Goods Filter] ";
        private const float ModeRowHeight = 30f;
        private const float WarehouseColumnWidth = 76f;
        private const float WarehouseColumnGap = 8f;
        private const float ScrollbarWidth = 16f;
        private const float ModeButtonWidth = 120f;
        private const float RefreshButtonWidth = 88f;
        private static readonly Type DialogType = typeof(Dialog_GoodsManager);
        private static readonly ConditionalWeakTable<Dialog_GoodsManager, GoodsManagerState> States =
            new ConditionalWeakTable<Dialog_GoodsManager, GoodsManagerState>();

        private static readonly FieldInfo CompField = AccessTools.Field(DialogType, "comp");
        private static readonly FieldInfo StorageField = AccessTools.Field(DialogType, "storage");
        private static readonly FieldInfo DraftItemDataField = AccessTools.Field(DialogType, "draftItemData");
        private static readonly FieldInfo ActiveCategoryField = AccessTools.Field(DialogType, "draftActiveDefName");
        private static readonly FieldInfo ListScrollField = AccessTools.Field(DialogType, "listScroll");
        private static readonly MethodInfo InvalidateCacheMethod = AccessTools.Method(DialogType, "InvalidateFilteredItemsCache", Type.EmptyTypes);
        private static readonly MethodInfo MarkDraftInventoryViewDirtyMethod = AccessTools.Method(DialogType, "MarkDraftInventoryViewDirty", Type.EmptyTypes);
        private static readonly MethodInfo DrawMainPanelMethod = AccessTools.Method(DialogType, "DrawMainPanel", new[] { typeof(Rect) });
        private static readonly MethodInfo DrawHeaderMethod = AccessTools.Method(DialogType, "DrawHeader", new[] { typeof(Rect) });
        private static readonly MethodInfo DrawItemRowMethod = AccessTools.Method(DialogType, "DrawItemRow", new[]
            { typeof(Rect), typeof(ThingDef), typeof(bool), typeof(RuntimeGoodsCategory) });
        private static readonly MethodInfo GetFilteredItemsMethod = AccessTools.Method(DialogType, "GetFilteredItems", new[] { typeof(RuntimeGoodsCategory) });
        private static readonly MethodInfo ToggleAllCurrentDefMethod = AccessTools.Method(DialogType, "ToggleAllCurrentDef", new[] { typeof(bool) });

        private static bool filteringAvailable;
        private static bool uiAvailable;
        private static bool toggleAvailable;
        private static bool validated;
        private static bool runtimeErrorLogged;

        internal static void ValidateTargets()
        {
            if (validated)
                return;
            validated = true;

            filteringAvailable = CompField != null && DraftItemDataField != null && GetFilteredItemsMethod != null;
            uiAvailable = ActiveCategoryField != null && ListScrollField != null &&
                InvalidateCacheMethod != null && DrawMainPanelMethod != null &&
                DrawHeaderMethod != null && DrawItemRowMethod != null;
            toggleAvailable = filteringAvailable && ActiveCategoryField != null && ToggleAllCurrentDefMethod != null &&
                MarkDraftInventoryViewDirtyMethod != null;

            List<string> missing = new List<string>();
            AddMissing(missing, CompField, "Dialog_GoodsManager.comp");
            AddMissing(missing, StorageField, "Dialog_GoodsManager.storage");
            AddMissing(missing, DraftItemDataField, "Dialog_GoodsManager.draftItemData");
            AddMissing(missing, ActiveCategoryField, "Dialog_GoodsManager.draftActiveDefName");
            AddMissing(missing, ListScrollField, "Dialog_GoodsManager.listScroll");
            AddMissing(missing, InvalidateCacheMethod, "Dialog_GoodsManager.InvalidateFilteredItemsCache()");
            AddMissing(missing, MarkDraftInventoryViewDirtyMethod, "Dialog_GoodsManager.MarkDraftInventoryViewDirty()");
            AddMissing(missing, DrawMainPanelMethod, "Dialog_GoodsManager.DrawMainPanel(Rect)");
            AddMissing(missing, DrawHeaderMethod, "Dialog_GoodsManager.DrawHeader(Rect)");
            AddMissing(missing, DrawItemRowMethod, "Dialog_GoodsManager.DrawItemRow(Rect, ThingDef, bool, RuntimeGoodsCategory)");
            AddMissing(missing, GetFilteredItemsMethod, "Dialog_GoodsManager.GetFilteredItems(RuntimeGoodsCategory)");
            AddMissing(missing, ToggleAllCurrentDefMethod, "Dialog_GoodsManager.ToggleAllCurrentDef(bool)");

            if (missing.Count > 0)
                Log.Error(LogPrefix + "RSMF.InventoryGoodsFilter.Error.MissingTargets".Translate(string.Join(", ", missing).Named("targets")));
            else
                Log.Message(LogPrefix + "RSMF.InventoryGoodsFilter.Loaded".Translate());
        }

        private static void AddMissing(List<string> missing, MemberInfo member, string name)
        {
            if (member == null)
                missing.Add(name);
        }

        private static void LogRuntimeErrorOnce(Exception exception)
        {
            if (runtimeErrorLogged)
                return;
            runtimeErrorLogged = true;
            Exception actual = exception is TargetInvocationException && exception.InnerException != null
                ? exception.InnerException
                : exception;
            Log.Error(LogPrefix + "RSMF.InventoryGoodsFilter.Error.RuntimeFallback".Translate(actual.Message.Named("error")));
        }

        internal static ThingComp_GoodsData GetComp(Dialog_GoodsManager dialog)
        {
            return CompField?.GetValue(dialog) as ThingComp_GoodsData;
        }

        internal static Map GetStorageMap(Dialog_GoodsManager dialog)
        {
            Building_SimContainer container = StorageField?.GetValue(dialog) as Building_SimContainer;
            if (container?.Map != null)
                return container.Map;

            ThingComp_GoodsData comp = GetComp(dialog);
            return (comp?.parent as Building_SimContainer)?.Map;
        }

        internal static Dictionary<ThingDef, int> GetInventoryCounts(Dialog_GoodsManager dialog, bool forceRebuild = false)
        {
            SyncWindowToCacheGeneration(dialog);
            return MapInventorySnapshotCache.GetCounts(GetStorageMap(dialog), forceRebuild);
        }

        internal static GoodsManagerState GetState(Dialog_GoodsManager dialog)
        {
            return States.GetValue(dialog, key => new GoodsManagerState());
        }

        private static void SyncWindowToCacheGeneration(Dialog_GoodsManager dialog)
        {
            if (!uiAvailable || dialog == null)
                return;

            GoodsManagerState state = GetState(dialog);
            int generation = MapInventorySnapshotCache.Generation;
            if (state.ObservedCacheGeneration == generation)
                return;

            state.ObservedCacheGeneration = generation;
            try
            {
                InvalidateCacheMethod.Invoke(dialog, null);
            }
            catch (Exception exception)
            {
                LogRuntimeErrorOnce(exception);
            }
        }

        internal static bool FilteringAvailable => filteringAvailable;
        internal static bool UiAvailable => uiAvailable;
        internal static bool ToggleAvailable => toggleAvailable;
        internal static MethodInfo DrawMainPanelTarget => DrawMainPanelMethod;
        internal static MethodInfo DrawHeaderTarget => DrawHeaderMethod;
        internal static MethodInfo DrawItemRowTarget => DrawItemRowMethod;
        internal static MethodInfo GetFilteredItemsTarget => GetFilteredItemsMethod;
        internal static MethodInfo ToggleAllCurrentDefTarget => ToggleAllCurrentDefMethod;

        internal static void FilterResult(Dialog_GoodsManager dialog, ref List<ThingDef> result)
        {
            if (!filteringAvailable || result == null)
                return;

            try
            {
                GoodsManagerState state = GetState(dialog);
                if (!state.InventoryMode)
                    return;

                Dictionary<string, GoodsItemData> draft =
                    DraftItemDataField.GetValue(dialog) as Dictionary<string, GoodsItemData>;
                Dictionary<ThingDef, int> inventoryCounts = GetInventoryCounts(dialog);

                // 框架会直接返回内部 filteredItemsCache，必须复制后再筛选，避免污染其缓存。
                List<ThingDef> filtered = new List<ThingDef>(result.Count);
                for (int i = 0; i < result.Count; i++)
                {
                    ThingDef def = result[i];
                    if (def != null && (inventoryCounts.ContainsKey(def) || IsDraftEnabled(draft, def)))
                        filtered.Add(def);
                }
                result = filtered;
            }
            catch (Exception exception)
            {
                LogRuntimeErrorOnce(exception);
            }
        }

        private static bool IsDraftEnabled(Dictionary<string, GoodsItemData> draft, ThingDef def)
        {
            return draft != null && draft.TryGetValue(def.defName, out GoodsItemData data) && data != null && data.enabled;
        }

        internal static void DrawModeControls(Dialog_GoodsManager dialog, ref Rect rect)
        {
            if (!uiAvailable)
                return;

            try
            {
                string categoryId = ActiveCategoryField.GetValue(dialog) as string;
                if (string.IsNullOrEmpty(categoryId))
                    return;

                GoodsManagerState state = GetState(dialog);
                Rect row = new Rect(rect.x, rect.y, rect.width, ModeRowHeight);
                Rect inventoryRect = new Rect(row.x, row.y, ModeButtonWidth, 24f);
                Rect allRect = new Rect(inventoryRect.xMax + 8f, row.y, ModeButtonWidth, 24f);
                Rect refreshRect = new Rect(allRect.xMax + 8f, row.y, RefreshButtonWidth, 24f);

                if (Widgets.RadioButtonLabeled(inventoryRect, "RSMF.InventoryGoodsFilter.InventoryMode".Translate(), state.InventoryMode))
                    SwitchMode(dialog, state, true);
                TooltipHandler.TipRegion(inventoryRect, "RSMF.InventoryGoodsFilter.InventoryModeTip".Translate());

                if (Widgets.RadioButtonLabeled(allRect, "RSMF.InventoryGoodsFilter.AllMode".Translate(), !state.InventoryMode))
                    SwitchMode(dialog, state, false);
                TooltipHandler.TipRegion(allRect, "RSMF.InventoryGoodsFilter.AllModeTip".Translate());

                if (Widgets.ButtonText(refreshRect, "RSMF.InventoryGoodsFilter.Refresh".Translate()))
                    RefreshInventory(dialog);
                TooltipHandler.TipRegion(refreshRect, "RSMF.InventoryGoodsFilter.RefreshTip".Translate());

                RuntimeGoodsCategory category = GoodsCatalog.GetCategory(categoryId);
                if (category != null)
                {
                    int inventoryCandidates = CountInventoryCandidates(dialog, category);
                    int registered = category.Items?.Count ?? 0;
                    string summary = "RSMF.InventoryGoodsFilter.Summary".Translate(
                        inventoryCandidates.Named("inventory"),
                        registered.Named("registered"));
                    Rect summaryRect = new Rect(refreshRect.xMax + 10f, row.y, Math.Max(0f, row.xMax - refreshRect.xMax - 10f), 24f);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Text.Font = GameFont.Tiny;
                    GUI.color = new Color(0.75f, 0.75f, 0.75f);
                    Widgets.Label(summaryRect, summary);
                    TooltipHandler.TipRegion(summaryRect, "RSMF.InventoryGoodsFilter.SummaryTip".Translate());
                    Text.Font = GameFont.Small;
                    Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = Color.white;
                }

                rect.y += ModeRowHeight;
                rect.height -= ModeRowHeight;
            }
            catch (Exception exception)
            {
                LogRuntimeErrorOnce(exception);
            }
        }

        private static int CountInventoryCandidates(Dialog_GoodsManager dialog, RuntimeGoodsCategory category)
        {
            if (category?.Items == null)
                return 0;

            Dictionary<string, GoodsItemData> draft =
                DraftItemDataField.GetValue(dialog) as Dictionary<string, GoodsItemData>;
            Dictionary<ThingDef, int> inventoryCounts = GetInventoryCounts(dialog);
            int count = 0;
            for (int i = 0; i < category.Items.Count; i++)
            {
                ThingDef def = category.Items[i]?.thingDef;
                if (def == null)
                    continue;
                if (inventoryCounts.ContainsKey(def) || IsDraftEnabled(draft, def))
                    count++;
            }
            return count;
        }

        private static void SwitchMode(Dialog_GoodsManager dialog, GoodsManagerState state, bool inventoryMode)
        {
            if (state.InventoryMode == inventoryMode)
                return;

            try
            {
                state.InventoryMode = inventoryMode;
                ListScrollField.SetValue(dialog, Vector2.zero);
                InvalidateCacheMethod.Invoke(dialog, null);
            }
            catch (Exception exception)
            {
                state.InventoryMode = false;
                LogRuntimeErrorOnce(exception);
            }
        }

        private static void RefreshInventory(Dialog_GoodsManager dialog)
        {
            try
            {
                Map map = GetStorageMap(dialog);
                MapInventorySnapshotCache.Invalidate(map);
                GetInventoryCounts(dialog, forceRebuild: true);
                InvalidateCacheMethod.Invoke(dialog, null);
                Messages.Message("RSMF.InventoryGoodsFilter.RefreshDone".Translate(),
                    MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception exception)
            {
                LogRuntimeErrorOnce(exception);
            }
        }

        internal static void ReserveWarehouseHeaderColumn(ref Rect rect, out Rect originalRect)
        {
            originalRect = rect;
            rect.width = Math.Max(0f, rect.width - WarehouseColumnWidth - WarehouseColumnGap - ScrollbarWidth);
        }

        internal static void ReserveWarehouseColumn(ref Rect rect, out Rect originalRect)
        {
            originalRect = rect;
            rect.width = Math.Max(0f, rect.width - WarehouseColumnWidth - WarehouseColumnGap);
        }

        private static Rect GetWarehouseColumnRect(Rect originalRect)
        {
            return new Rect(originalRect.xMax - WarehouseColumnWidth, originalRect.y,
                WarehouseColumnWidth, originalRect.height);
        }

        private static Rect GetWarehouseHeaderRect(Rect originalRect)
        {
            return new Rect(originalRect.xMax - WarehouseColumnWidth - ScrollbarWidth, originalRect.y,
                WarehouseColumnWidth, originalRect.height);
        }

        internal static void DrawWarehouseHeader(Rect originalRect)
        {
            Rect columnRect = GetWarehouseHeaderRect(originalRect);
            Widgets.DrawBoxSolid(columnRect, new Color(0f, 0f, 0f, 0.2f));
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = new Color(0.8f, 0.8f, 0.8f);
            Widgets.Label(columnRect, "RSMF.InventoryGoodsFilter.WarehouseStock".Translate());
            TooltipHandler.TipRegion(columnRect, "RSMF.InventoryGoodsFilter.WarehouseStockTip".Translate());
            Widgets.DrawLineHorizontal(columnRect.x, columnRect.yMax - 1f, columnRect.width);
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        internal static void DrawWarehouseCount(Dialog_GoodsManager dialog, Rect originalRect, ThingDef thingDef)
        {
            Rect columnRect = GetWarehouseColumnRect(originalRect);
            Dictionary<ThingDef, int> inventoryCounts = GetInventoryCounts(dialog);
            int count = 0;
            if (thingDef != null)
                inventoryCounts.TryGetValue(thingDef, out count);

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = Color.white;
            Widgets.Label(columnRect, count.ToString());
            TooltipHandler.TipRegion(columnRect, "RSMF.InventoryGoodsFilter.WarehouseStockTip".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        private static int ClampVisibleCapacity(Dialog_GoodsManager dialog, RuntimeGoodsCategory category,
            Dictionary<string, GoodsItemData> draft, List<ThingDef> visible)
        {
            Building_SimContainer storage = StorageField.GetValue(dialog) as Building_SimContainer;
            if (storage == null || category == null || draft == null || visible == null)
                return 0;

            HashSet<ThingDef> visibleSet = new HashSet<ThingDef>(visible);
            int usedByHidden = 0;
            for (int i = 0; i < category.Items.Count; i++)
            {
                ThingDef thingDef = category.Items[i]?.thingDef;
                if (thingDef == null || visibleSet.Contains(thingDef))
                    continue;
                if (draft.TryGetValue(thingDef.defName, out GoodsItemData data) && data != null &&
                    data.enabled && data.count > 0)
                    usedByHidden += data.count;
            }

            int remaining = Math.Max(0, storage.MaxTotalCapacity - usedByHidden);
            int trimmed = 0;
            for (int i = 0; i < visible.Count; i++)
            {
                ThingDef thingDef = visible[i];
                if (thingDef == null || !draft.TryGetValue(thingDef.defName, out GoodsItemData data) || data == null)
                    continue;

                if (!data.enabled || data.count <= 0)
                {
                    if (data.enabled)
                        trimmed += Math.Max(0, data.count);
                    data.enabled = false;
                    data.count = 0;
                    data.countBuffer = "0";
                    data.restockThreshold = 0;
                    data.restockThresholdBuffer = "0";
                    continue;
                }

                if (remaining <= 0)
                {
                    trimmed += data.count;
                    data.enabled = false;
                    data.count = 0;
                    data.countBuffer = "0";
                    data.restockThreshold = 0;
                    data.restockThresholdBuffer = "0";
                    continue;
                }

                if (data.count > remaining)
                {
                    trimmed += data.count - remaining;
                    data.count = remaining;
                }

                data.restockThreshold = GoodsItemData.NormalizeRestockThreshold(data.restockThreshold, data.count);
                data.restockThresholdBuffer = data.restockThreshold.ToString();
                data.countBuffer = data.count.ToString();
                remaining -= data.count;
            }

            return trimmed;
        }

        internal static bool ToggleVisibleItems(Dialog_GoodsManager dialog, bool enable)
        {
            try
            {
                GoodsManagerState state = GetState(dialog);
                if (!state.InventoryMode)
                    return true;

                string categoryId = ActiveCategoryField.GetValue(dialog) as string;
                RuntimeGoodsCategory category = GoodsCatalog.GetCategory(categoryId);
                Dictionary<string, GoodsItemData> draft =
                    DraftItemDataField.GetValue(dialog) as Dictionary<string, GoodsItemData>;
                if (category == null || draft == null)
                    return false;

                // 通过框架原筛选入口取得“当前分类 + 当前搜索”，其 Postfix 再应用库存模式。
                List<ThingDef> visible = GetFilteredItemsMethod.Invoke(dialog, new object[] { category }) as List<ThingDef>;
                if (visible == null)
                    return false;

                for (int i = 0; i < visible.Count; i++)
                {
                    ThingDef thingDef = visible[i];
                    if (thingDef == null)
                        continue;

                    if (!draft.TryGetValue(thingDef.defName, out GoodsItemData data) || data == null)
                        draft[thingDef.defName] = data = new GoodsItemData();

                    data.enabled = enable;
                    if (!enable)
                        continue;

                    if (data.count <= 0)
                    {
                        data.count = 1;
                        data.countBuffer = "1";
                    }
                    data.restockThreshold = GoodsItemData.NormalizeRestockThreshold(data.restockThreshold, data.count);
                    data.restockThresholdBuffer = data.restockThreshold.ToString();
                    if (data.price <= 0f)
                    {
                        data.price = thingDef.BaseMarketValue > 0f ? thingDef.BaseMarketValue : 1f;
                        data.priceBuffer = data.price.ToString("F0");
                    }
                }

                int trimmed = ClampVisibleCapacity(dialog, category, draft, visible);
                MarkDraftInventoryViewDirtyMethod.Invoke(dialog, null);
                if (trimmed > 0)
                {
                    Messages.Message("RSMF.GoodsManager.AutoTrimNotice".Translate(trimmed.Named("trimmed")),
                        MessageTypeDefOf.NeutralEvent, false);
                }
                return false;
            }
            catch (Exception exception)
            {
                LogRuntimeErrorOnce(exception);
                return true;
            }
        }
    }

    [HarmonyPatch]
    internal static class DrawMainPanelPatch
    {
        private static bool Prepare() => GoodsManagerPatches.UiAvailable;
        private static MethodBase TargetMethod() => GoodsManagerPatches.DrawMainPanelTarget;
        private static void Prefix(Dialog_GoodsManager __instance, ref Rect rect) =>
            GoodsManagerPatches.DrawModeControls(__instance, ref rect);
    }

    [HarmonyPatch]
    internal static class DrawHeaderPatch
    {
        private static bool Prepare() => GoodsManagerPatches.UiAvailable;
        private static MethodBase TargetMethod() => GoodsManagerPatches.DrawHeaderTarget;

        private static void Prefix(ref Rect rect, out Rect __state) =>
            GoodsManagerPatches.ReserveWarehouseHeaderColumn(ref rect, out __state);

        private static void Postfix(Rect __state) => GoodsManagerPatches.DrawWarehouseHeader(__state);
    }

    [HarmonyPatch]
    internal static class DrawItemRowPatch
    {
        private static bool Prepare() => GoodsManagerPatches.UiAvailable;
        private static MethodBase TargetMethod() => GoodsManagerPatches.DrawItemRowTarget;

        private static void Prefix(ref Rect row, out Rect __state) =>
            GoodsManagerPatches.ReserveWarehouseColumn(ref row, out __state);

        private static void Postfix(Dialog_GoodsManager __instance, ThingDef td, Rect __state) =>
            GoodsManagerPatches.DrawWarehouseCount(__instance, __state, td);
    }

    [HarmonyPatch]
    internal static class GetFilteredItemsPatch
    {
        private static bool Prepare() => GoodsManagerPatches.FilteringAvailable;
        private static MethodBase TargetMethod() => GoodsManagerPatches.GetFilteredItemsTarget;
        private static void Postfix(Dialog_GoodsManager __instance, ref List<ThingDef> __result) =>
            GoodsManagerPatches.FilterResult(__instance, ref __result);
    }

    [HarmonyPatch]
    internal static class ToggleAllCurrentDefPatch
    {
        private static bool Prepare() => GoodsManagerPatches.ToggleAvailable;
        private static MethodBase TargetMethod() => GoodsManagerPatches.ToggleAllCurrentDefTarget;
        private static bool Prefix(Dialog_GoodsManager __instance, bool enable) =>
            GoodsManagerPatches.ToggleVisibleItems(__instance, enable);
    }
}
