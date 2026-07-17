using HarmonyLib;
using RimWorld;
using RSMFInventoryGoodsFilter.Restock;
using SimManagementLib.SimDialog;
using SimManagementLib.SimThingClass;
using SimManagementLib.SimThingComp;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

namespace RSMFInventoryGoodsFilter.Patches
{
    /// <summary>
    /// 补货 UI：阈值/目标拖动跟随、H=0 显示与 Tooltip、行状态 tip。
    /// 底栏「复制诊断 / 重整补货」仅开发者模式显示。
    /// 不接管整段 DrawItemRow，与 GoodsManagerPatches.DrawItemRowPatch 共存。
    /// </summary>
    internal static class RestockUiPatches
    {
        private const string LogPrefix = "[RSMF Inventory Goods Filter][Restock] ";
        private const string PlayerRepairReason = "RSMF.InventoryGoodsFilter: player repair";

        // 与 Dialog_GoodsManager / GoodsManagerPatches 布局常量对齐，仅用于 Tooltip 定位。
        private const float RowH = 46f;
        private const float FieldW = 65f;
        private const float ThresholdFieldW = 76f;
        private const float ColGap = 10f;
        private const float RowPad = 8f;
        private const float RestockButtonW = 96f;
        private const float RestockButtonH = 30f;
        private const float RestockButtonGap = 8f;

        private static readonly FieldInfo DraftItemDataField =
            AccessTools.Field(typeof(Dialog_GoodsManager), "draftItemData");
        private static readonly FieldInfo StorageField =
            AccessTools.Field(typeof(Dialog_GoodsManager), "storage");
        private static readonly MethodInfo DrawBottomBarMethod =
            AccessTools.Method(typeof(Dialog_GoodsManager), "DrawBottomBar", new[] { typeof(Rect) });

        private static readonly ConditionalWeakTable<GoodsItemData, ThresholdSessionState> SessionStates =
            new ConditionalWeakTable<GoodsItemData, ThresholdSessionState>();

        private static bool runtimeErrorLogged;
        private static bool appliedLogged;

        internal static bool IsEnabled()
        {
            return RestockFixGate.PlayerUxEnabled;
        }

        internal static bool CanPatchNormalize()
        {
            return IsEnabled() && RestockPatchTargets.HasNormalizeApi;
        }

        internal static bool CanPatchDrawItemRow()
        {
            return IsEnabled() && RestockPatchTargets.HasUiApi && DraftItemDataField != null;
        }

        // 补丁始终注册（总开关开时）；按钮绘制再按 DevMode 门控，避免中途开 Dev 无按钮。
        internal static bool CanPatchBottomBar()
        {
            return IsEnabled() && DrawBottomBarMethod != null && StorageField != null;
        }

        internal static MethodInfo DrawBottomBarTarget => DrawBottomBarMethod;

        internal static void LogAppliedOnce()
        {
            if (appliedLogged || !RestockFixGate.VerboseLog)
                return;
            appliedLogged = true;
            Log.Message(LogPrefix + "RSMF.InventoryGoodsFilter.Restock.R3UiApplied".Translate());
        }

        internal static void LogRuntimeErrorOnce(Exception exception)
        {
            if (runtimeErrorLogged)
                return;
            runtimeErrorLogged = true;
            Log.Error(LogPrefix +
                "RSMF.InventoryGoodsFilter.Error.RuntimeFallback".Translate(exception.Message.Named("error")));
        }

        internal static Building_SimContainer TryGetStorage(Dialog_GoodsManager dialog)
        {
            if (dialog == null || StorageField == null)
                return null;
            return StorageField.GetValue(dialog) as Building_SimContainer;
        }

        /// <summary>
        /// UI/存储规范化：允许字面量 0（始终补满）；-1 视作始终补满意图并规范为 0。
        /// 有效阈值 H' 仍由 R1 GetEffectiveRestockThreshold 负责。
        /// </summary>
        internal static int NormalizeForUiStorage(int configuredThreshold, int targetCount)
        {
            int target = targetCount < 0 ? 0 : targetCount;
            if (target <= 0)
                return 0;

            // 0 / 负值（含旧存档 -1）→ 始终补满意图字面量 0
            if (configuredThreshold <= 0)
                return 0;

            if (configuredThreshold > target)
                return target;
            return configuredThreshold;
        }

        internal static bool IsAlwaysFillIntent(int threshold, int target)
        {
            return threshold <= 0 || (target > 0 && threshold == target);
        }

        internal static ThresholdSessionState GetSessionState(GoodsItemData data)
        {
            return SessionStates.GetValue(data, _ => new ThresholdSessionState());
        }

        internal static GoodsItemData TryGetDraftItem(Dialog_GoodsManager dialog, ThingDef td)
        {
            if (dialog == null || td == null || DraftItemDataField == null)
                return null;

            Dictionary<string, GoodsItemData> draft =
                DraftItemDataField.GetValue(dialog) as Dictionary<string, GoodsItemData>;
            if (draft == null)
                return null;

            draft.TryGetValue(td.defName, out GoodsItemData data);
            return data;
        }

        /// <summary>
        /// 目标变化时的阈值跟随 + 会话备份恢复。
        /// </summary>
        internal static void ApplyTargetFollow(GoodsItemData data, int oldTarget, int oldThreshold)
        {
            if (data == null)
                return;

            int newTarget = data.count < 0 ? 0 : data.count;
            int currentThreshold = data.restockThreshold;
            ThresholdSessionState session = GetSessionState(data);

            // 首次观察：只记录，不改写。
            if (!session.Initialized)
            {
                session.Initialized = true;
                session.LastTarget = newTarget;
                session.LastThreshold = currentThreshold;
                session.AlwaysFillIntent = IsAlwaysFillIntent(currentThreshold, newTarget);
                return;
            }

            if (newTarget == oldTarget)
            {
                // 仅阈值被玩家改写：刷新意图快照。
                session.LastTarget = newTarget;
                session.LastThreshold = currentThreshold;
                session.AlwaysFillIntent = IsAlwaysFillIntent(currentThreshold, newTarget);
                if (newTarget > 0)
                    session.ZeroedInSession = false;
                return;
            }

            // 目标归零：备份意图，避免随后恢复时永久 residual 0。
            if (newTarget == 0)
            {
                if (!session.ZeroedInSession)
                {
                    session.BackupThreshold = oldThreshold;
                    session.BackupAlwaysFillIntent = IsAlwaysFillIntent(oldThreshold, oldTarget);
                    session.ZeroedInSession = true;
                }

                data.restockThreshold = 0;
                data.restockThresholdBuffer = "0";
                session.LastTarget = 0;
                session.LastThreshold = 0;
                session.AlwaysFillIntent = session.BackupAlwaysFillIntent;
                return;
            }

            // 从 0 恢复：优先还原会话备份。
            if (oldTarget == 0 && session.ZeroedInSession)
            {
                int restored;
                if (session.BackupAlwaysFillIntent)
                    restored = 0;
                else
                    restored = NormalizeForUiStorage(session.BackupThreshold, newTarget);

                data.restockThreshold = restored;
                data.restockThresholdBuffer = restored.ToString();
                session.ZeroedInSession = false;
                session.LastTarget = newTarget;
                session.LastThreshold = restored;
                session.AlwaysFillIntent = session.BackupAlwaysFillIntent || restored <= 0;
                return;
            }

            // 常规目标变化跟随。
            bool alwaysFill = IsAlwaysFillIntent(oldThreshold, oldTarget) || session.AlwaysFillIntent;
            int newThreshold;
            if (alwaysFill)
            {
                // 推荐：字面量 0 = 始终补满；Effective 由 R1 → newT
                newThreshold = 0;
            }
            else
            {
                newThreshold = NormalizeForUiStorage(oldThreshold, newTarget);
            }

            data.restockThreshold = newThreshold;
            data.restockThresholdBuffer = newThreshold.ToString();
            session.LastTarget = newTarget;
            session.LastThreshold = newThreshold;
            session.AlwaysFillIntent = alwaysFill || newThreshold <= 0;

            if (RestockFixGate.VerboseLog)
            {
                Log.Message(LogPrefix + "threshold follow: T " + oldTarget + "→" + newTarget +
                    " H " + oldThreshold + "→" + newThreshold +
                    " alwaysFill=" + alwaysFill);
            }
        }

        internal static void DrawThresholdTooltip(Rect row, bool enabled)
        {
            if (!enabled)
                return;

            // DrawItemRow 收到的 row 已是缩窄后的框架行；价格/阈值/目标都相对 row.xMax 布局。
            // 不要再加回仓库列宽度，否则阈值 tip 会右移到单价框。
            // 从右往左：价格 FieldW → gap → 阈值 ThresholdFieldW → gap → 目标 FieldW
            float rightX = row.xMax - RowPad - FieldW - ColGap - ThresholdFieldW;
            float ctrlY = row.y + (RowH - 24f) / 2f;
            Rect tipRect = new Rect(rightX, ctrlY, ThresholdFieldW, 24f);
            TooltipHandler.TipRegion(tipRect, "RSMF.InventoryGoodsFilter.Restock.ThresholdTip".Translate());
        }

        /// <summary>
        /// 行状态：Tooltip 叠在名称区（不画行尾字，避免与仓库列/控件抢位）。
        /// </summary>
        internal static void DrawRowRestockStatus(Dialog_GoodsManager dialog, Rect row, ThingDef td, bool enabled)
        {
            if (!enabled || td == null)
                return;

            Building_SimContainer storage = TryGetStorage(dialog);
            if (storage == null || storage.Destroyed)
                return;

            RestockStatusPresenter.RowStatusSnapshot snap =
                RestockStatusPresenter.GetOrBuildSnapshot(storage, td);
            if (snap == null || string.IsNullOrEmpty(snap.Tooltip))
                return;

            // 名称区（左侧）Tooltip：S/T/H'/P + 状态
            float nameW = Mathf.Max(80f, row.width * 0.28f);
            Rect nameTipRect = new Rect(row.x + RowPad, row.y, nameW, row.height);
            TooltipHandler.TipRegion(nameTipRect, snap.Tooltip);

            // 库存数字区也叠同一 Tooltip，方便对照 S
            float stockTipX = row.x + nameW + 8f;
            float stockTipW = Mathf.Min(90f, Mathf.Max(0f, row.width - nameW - 16f));
            if (stockTipW > 24f)
            {
                Rect stockTipRect = new Rect(stockTipX, row.y, stockTipW, row.height);
                TooltipHandler.TipRegion(stockTipRect, snap.Tooltip);
            }
        }

        internal static void DrawRestockActionButtons(Dialog_GoodsManager dialog, Rect rect)
        {
            // 日常玩家不显示开发者工具；仅 DevMode + 总开关。
            if (!RestockFixGate.DevToolsEnabled)
                return;

            Building_SimContainer storage = TryGetStorage(dialog);
            if (storage == null || storage.Destroyed)
                return;

            float btnY = rect.yMax - 36f;
            // 放在「全选/清空」右侧、容量与复制配置之间，避免覆盖框架主按钮。
            float x = rect.x + 230f;
            Rect copyRect = new Rect(x, btnY, RestockButtonW, RestockButtonH);
            Rect repairRect = new Rect(copyRect.xMax + RestockButtonGap, btnY, RestockButtonW, RestockButtonH);

            // 若与右侧复制配置按钮重叠则左移
            float rightReserved = rect.xMax - 450f;
            if (repairRect.xMax > rightReserved)
            {
                float shift = repairRect.xMax - rightReserved;
                copyRect.x -= shift;
                repairRect.x -= shift;
            }

            if (Widgets.ButtonText(copyRect, "RSMF.InventoryGoodsFilter.Restock.CopyDiagnostics".Translate()))
                CopyStorageDiagnostics(storage);
            TooltipHandler.TipRegion(copyRect, "RSMF.InventoryGoodsFilter.Restock.CopyDiagnosticsTip".Translate());

            if (Widgets.ButtonText(repairRect, "RSMF.InventoryGoodsFilter.Restock.RepairToSettings".Translate()))
                RepairStorageFromUi(storage);
            TooltipHandler.TipRegion(repairRect, "RSMF.InventoryGoodsFilter.Restock.RepairToSettingsTip".Translate());
        }

        private static void CopyStorageDiagnostics(Building_SimContainer storage)
        {
            try
            {
                string text = RestockStatusPresenter.BuildDiagnostic(storage);
                if (string.IsNullOrEmpty(text))
                {
                    Messages.Message(
                        "RSMF.InventoryGoodsFilter.Restock.CopyDiagnosticsEmpty".Translate(),
                        MessageTypeDefOf.NeutralEvent,
                        false);
                    return;
                }

                GUIUtility.systemCopyBuffer = text;
                Messages.Message(
                    "RSMF.InventoryGoodsFilter.Restock.CopyDiagnosticsDone".Translate(),
                    MessageTypeDefOf.TaskCompletion,
                    false);

                if (RestockFixGate.VerboseLog)
                    Log.Message(LogPrefix + "diagnostics copied, length=" + text.Length);
            }
            catch (Exception exception)
            {
                LogRuntimeErrorOnce(exception);
            }
        }

        private static void RepairStorageFromUi(Building_SimContainer storage)
        {
            try
            {
                string before = null;
                if (RestockFixGate.VerboseLog)
                    before = RestockStatusPresenter.BuildDiagnostic(storage);

                bool ok = RestockRepairService.RepairStorage(storage, soft: true, PlayerRepairReason);
                if (ok)
                {
                    Messages.Message(
                        "RSMF.InventoryGoodsFilter.Restock.RepairDone".Translate(),
                        MessageTypeDefOf.TaskCompletion,
                        false);
                }
                else
                {
                    Messages.Message(
                        "RSMF.InventoryGoodsFilter.Restock.RepairFailed".Translate(),
                        MessageTypeDefOf.RejectInput,
                        false);
                }

                if (RestockFixGate.VerboseLog)
                {
                    string after = RestockStatusPresenter.BuildDiagnostic(storage);
                    Log.Message(LogPrefix + "player repair ok=" + ok
                        + " beforeLen=" + (before?.Length ?? 0)
                        + " afterLen=" + (after?.Length ?? 0));
                }
            }
            catch (Exception exception)
            {
                LogRuntimeErrorOnce(exception);
                Messages.Message(
                    "RSMF.InventoryGoodsFilter.Restock.RepairFailed".Translate(),
                    MessageTypeDefOf.RejectInput,
                    false);
            }
        }

        internal sealed class ThresholdSessionState
        {
            internal bool Initialized;
            internal int LastTarget;
            internal int LastThreshold;
            internal bool AlwaysFillIntent;
            internal bool ZeroedInSession;
            internal int BackupThreshold;
            internal bool BackupAlwaysFillIntent;
        }

        internal struct DrawItemRowState
        {
            internal bool Valid;
            internal int OldTarget;
            internal int OldThreshold;
            internal bool Enabled;
        }
    }

    /// <summary>
    /// 显示/存储规范化与 R1 Effective 解耦：UI 可保留字面量 0。
    /// </summary>
    [HarmonyPatch]
    internal static class NormalizeRestockThresholdUiPatch
    {
        private static bool Prepare()
        {
            bool ok = RestockUiPatches.CanPatchNormalize();
            if (ok)
                RestockUiPatches.LogAppliedOnce();
            return ok;
        }

        private static MethodBase TargetMethod() => RestockPatchTargets.NormalizeRestockThreshold;

        private static bool Prefix(int configuredThreshold, int targetCount, ref int __result)
        {
            if (!RestockUiPatches.IsEnabled())
                return true;

            try
            {
                __result = RestockUiPatches.NormalizeForUiStorage(configuredThreshold, targetCount);
                return false;
            }
            catch (Exception exception)
            {
                RestockUiPatches.LogRuntimeErrorOnce(exception);
                return true;
            }
        }
    }

    /// <summary>
    /// 目标/阈值差分跟随 + 阈值 Tooltip。不复制 DrawItemRow 主体。
    /// </summary>
    [HarmonyPatch]
    internal static class DrawItemRowThresholdAssistPatch
    {
        private static bool Prepare()
        {
            bool ok = RestockUiPatches.CanPatchDrawItemRow();
            if (ok)
                RestockUiPatches.LogAppliedOnce();
            return ok;
        }

        private static MethodBase TargetMethod() => RestockPatchTargets.DrawItemRow;

        private static void Prefix(Dialog_GoodsManager __instance, ThingDef td, out RestockUiPatches.DrawItemRowState __state)
        {
            __state = default;
            if (!RestockUiPatches.IsEnabled())
                return;

            try
            {
                GoodsItemData data = RestockUiPatches.TryGetDraftItem(__instance, td);
                if (data == null)
                    return;

                __state.Valid = true;
                __state.OldTarget = data.count;
                __state.OldThreshold = data.restockThreshold;
                __state.Enabled = data.enabled;

                // 预热会话状态，避免首次仅改阈值时丢失意图。
                RestockUiPatches.ThresholdSessionState session = RestockUiPatches.GetSessionState(data);
                if (!session.Initialized)
                {
                    session.Initialized = true;
                    session.LastTarget = data.count;
                    session.LastThreshold = data.restockThreshold;
                    session.AlwaysFillIntent =
                        RestockUiPatches.IsAlwaysFillIntent(data.restockThreshold, data.count);
                }
            }
            catch (Exception exception)
            {
                RestockUiPatches.LogRuntimeErrorOnce(exception);
            }
        }

        private static void Postfix(
            Dialog_GoodsManager __instance,
            Rect row,
            ThingDef td,
            RestockUiPatches.DrawItemRowState __state)
        {
            if (!RestockUiPatches.IsEnabled() || !__state.Valid)
                return;

            try
            {
                GoodsItemData data = RestockUiPatches.TryGetDraftItem(__instance, td);
                if (data == null)
                    return;

                // 目标变化时覆盖框架仅 clamp 的 Normalize 结果。
                if (data.count != __state.OldTarget)
                    RestockUiPatches.ApplyTargetFollow(data, __state.OldTarget, __state.OldThreshold);
                else
                {
                    RestockUiPatches.ThresholdSessionState session = RestockUiPatches.GetSessionState(data);
                    session.LastTarget = data.count;
                    session.LastThreshold = data.restockThreshold;
                    session.AlwaysFillIntent =
                        RestockUiPatches.IsAlwaysFillIntent(data.restockThreshold, data.count);
                    if (data.count > 0)
                        session.ZeroedInSession = false;
                }

                // 确保 buffer 与字面量一致（含 0）。
                if (data.restockThresholdBuffer != data.restockThreshold.ToString())
                    data.restockThresholdBuffer = data.restockThreshold.ToString();

                RestockUiPatches.DrawThresholdTooltip(row, data.enabled);
                // R4：行状态（与仓库列共存；row 已是缩窄后的框架行）
                RestockUiPatches.DrawRowRestockStatus(__instance, row, td, data.enabled);
            }
            catch (Exception exception)
            {
                RestockUiPatches.LogRuntimeErrorOnce(exception);
            }
        }
    }

    /// <summary>
    /// 底栏开发者工具：「复制补货诊断」「重整补货」。运行时再按 DevMode 绘制。
    /// </summary>
    [HarmonyPatch]
    internal static class DrawBottomBarRestockAssistPatch
    {
        private static bool Prepare()
        {
            bool ok = RestockUiPatches.CanPatchBottomBar();
            if (ok)
                RestockUiPatches.LogAppliedOnce();
            return ok;
        }

        private static MethodBase TargetMethod() => RestockUiPatches.DrawBottomBarTarget;

        private static void Postfix(Dialog_GoodsManager __instance, Rect rect)
        {
            if (!RestockUiPatches.IsEnabled())
                return;

            try
            {
                RestockUiPatches.DrawRestockActionButtons(__instance, rect);
            }
            catch (Exception exception)
            {
                RestockUiPatches.LogRuntimeErrorOnce(exception);
            }
        }
    }
}
