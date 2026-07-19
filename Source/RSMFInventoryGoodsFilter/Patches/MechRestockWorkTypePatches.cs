using HarmonyLib;
using RimWorld;
using SimManagementLib.SimMapComp;
using SimManagementLib.Tool;
using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;
using Verse.AI;

namespace RSMFInventoryGoodsFilter.Patches
{
    /// <summary>
    /// 机械体补货补丁的反射探针（全部是主模组私有方法，字符串反射）。
    /// 探针找不到 → 对应补丁 Prepare 返回 false → 静默降级，启动时 Warning 一次。
    /// 主模组改名这些方法不会编译报错，只会静默失效，见启动日志。
    /// </summary>
    internal static class MechRestockPatchTargets
    {
        private const string LogPrefix = "[RSMF Inventory Goods Filter][Restock] ";

        internal static readonly MethodInfo MechanicalStaffCanUseWorkType =
            AccessTools.Method(typeof(ShopStaffUtility), "MechanicalStaffCanUseWorkType");
        internal static readonly MethodInfo CanUseRestockWorkGiver =
            AccessTools.Method(typeof(MapComponent_RestockTaskQueue), "CanUseRestockWorkGiver");
        internal static readonly MethodInfo MechDispatcherIsIdleForShopDispatch =
            AccessTools.Method(typeof(MapComponent_MechShopStaffDispatcher), "IsIdleForShopDispatch");
        internal static readonly MethodInfo MechDispatcherShouldTryDispatch =
            AccessTools.Method(typeof(MapComponent_MechShopStaffDispatcher), "ShouldTryDispatch");

        internal static void ValidateAndLog()
        {
            List<string> missing = new List<string>();
            if (MechanicalStaffCanUseWorkType == null)
                missing.Add("ShopStaffUtility.MechanicalStaffCanUseWorkType");
            if (CanUseRestockWorkGiver == null)
                missing.Add("MapComponent_RestockTaskQueue.CanUseRestockWorkGiver");
            if (MechDispatcherIsIdleForShopDispatch == null)
                missing.Add("MapComponent_MechShopStaffDispatcher.IsIdleForShopDispatch");
            if (MechDispatcherShouldTryDispatch == null)
                missing.Add("MapComponent_MechShopStaffDispatcher.ShouldTryDispatch");

            if (missing.Count > 0)
            {
                Log.Warning(LogPrefix +
                    "Mech restock compat targets missing (affected patches disabled): " +
                    string.Join(", ", missing));
            }
        }
    }

    /// <summary>
    /// 让原版「只会 Hauling」的玩家机械体（如 Mech_Lifter）真正能执行框架补货。
    ///
    /// 源码事实链：
    /// 1) 框架补货 WorkGiver RestockMegaStorage 绑定自定义 WorkType Restocking（非原版工种）。
    /// 2) Mech_Lifter.mechEnabledWorkTypes 只有 Hauling。
    /// 3) ShopStaffUtility.MechanicalStaffCanUseWorkType 严格 Contains → 岗位资格挡。
    /// 4) Pawn.GetDisabledWorkTypes 对殖民地机械体：凡不在 mechEnabledWorkTypes 的工种一律禁用
    ///    → WorkTypeIsDisabled(Restocking)=true。
    /// 5) MapComponent_RestockTaskQueue.CanUseRestockWorkGiver 会查 WorkTypeIsDisabled
    ///    → 队列主动派工永远跳过搬运机。
    /// 6) MapComponent_MechShopStaffDispatcher 只把 Wait / Wait_MaintainPosture 当空闲，
    ///    机械体常见 GotoWander / Wait_Wander 时不会派工。
    ///
    /// 本文件用 Harmony 逐点打通，不改框架 DLL。
    /// </summary>
    internal static class MechRestockWorkTypePatches
    {
        private const string LogPrefix = "[RSMF Inventory Goods Filter][Restock] ";
        private static bool appliedLogged;
        private static bool runtimeErrorLogged;

        private static bool MechRestockEnabled =>
            InventoryGoodsFilterMod.Settings == null || InventoryGoodsFilterMod.Settings.EnableMechRestock;

        private static readonly HashSet<string> ExtraMechIdleJobDefNames = new HashSet<string>
        {
            "Wait",
            "Wait_Wander",
            "Wait_MaintainPosture",
            "GotoWander",
            "HaulToCell",
            "Refuel",
            "MechCharge" // 低电量充电中不抢；充电 Job 名若不同由下面空闲判定再收窄
        };

        // 真正允许打断的“空闲/低优先级”Job（不含充电）
        private static readonly HashSet<string> MechIdleDispatchJobDefNames = new HashSet<string>
        {
            "Wait",
            "Wait_Wander",
            "Wait_MaintainPosture",
            "GotoWander",
            "HaulToCell",
            "Refuel"
        };

        internal static bool CanHaulerMechDoRestocking(Pawn pawn, WorkTypeDef workType)
        {
            if (pawn == null || workType == null)
                return false;
            if (!IsRestockingWorkType(workType))
                return false;
            if (!ShopStaffUtility.IsAssignableMechanicalStaff(pawn))
                return false;
            return MechNativeSupportsHauling(pawn);
        }

        internal static bool IsHaulerColonyMechStaff(Pawn pawn)
        {
            return ShopStaffUtility.IsAssignableMechanicalStaff(pawn) && MechNativeSupportsHauling(pawn);
        }

        private static bool IsRestockingWorkType(WorkTypeDef workType)
        {
            return workType != null && workType.defName == "Restocking";
        }

        private static bool MechNativeSupportsHauling(Pawn pawn)
        {
            List<WorkTypeDef> enabled = pawn.RaceProps?.mechEnabledWorkTypes;
            if (enabled == null || enabled.Count == 0)
                return false;

            WorkTypeDef hauling = WorkTypeDefOf.Hauling;
            for (int i = 0; i < enabled.Count; i++)
            {
                WorkTypeDef wt = enabled[i];
                if (wt == null)
                    continue;
                if (hauling != null && wt == hauling)
                    return true;
                if (wt.defName == "Hauling")
                    return true;
            }

            return false;
        }

        private static void LogAppliedOnce()
        {
            if (appliedLogged)
                return;
            appliedLogged = true;
            Log.Message(LogPrefix + "Mech restock compat active (hauling mechs can restock).");
        }

        private static void LogRuntimeErrorOnce(Exception exception)
        {
            if (runtimeErrorLogged)
                return;
            runtimeErrorLogged = true;
            Log.Warning(LogPrefix + "Mech restock compat error: " + exception.Message);
        }

        /// <summary>
        /// 岗位/权限：Hauling 机可使用 Restocking 工种。
        /// </summary>
        [HarmonyPatch]
        private static class MechanicalStaffCanUseWorkType_Patch
        {
            private static MethodBase TargetMethod()
            {
                return MechRestockPatchTargets.MechanicalStaffCanUseWorkType;
            }

            private static bool Prepare()
            {
                if (MechRestockPatchTargets.MechanicalStaffCanUseWorkType == null)
                    return false;

                LogAppliedOnce();
                return true;
            }

            private static void Postfix(Pawn pawn, WorkTypeDef workType, ref bool __result)
            {
                if (__result || !MechRestockEnabled)
                    return;
                try
                {
                    if (CanHaulerMechDoRestocking(pawn, workType))
                        __result = true;
                }
                catch (Exception exception)
                {
                    LogRuntimeErrorOnce(exception);
                }
            }
        }

        /// <summary>
        /// 原版：殖民地机械体对未声明工种一律 Disabled。
        /// Restocking 对 Hauling 机应视为可用，否则队列派工永远跳过。
        /// </summary>
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.WorkTypeIsDisabled))]
        private static class Pawn_WorkTypeIsDisabled_Patch
        {
            private static void Postfix(Pawn __instance, WorkTypeDef w, ref bool __result)
            {
                if (!__result || !MechRestockEnabled)
                    return;
                try
                {
                    if (CanHaulerMechDoRestocking(__instance, w))
                        __result = false;
                }
                catch (Exception exception)
                {
                    LogRuntimeErrorOnce(exception);
                }
            }
        }

        /// <summary>
        /// 队列主动派工入口：在 WorkTypeIsDisabled 之外再兜一层（含 workSettings 为空的机械体）。
        /// </summary>
        [HarmonyPatch]
        private static class CanUseRestockWorkGiver_Patch
        {
            private static MethodBase TargetMethod()
            {
                return MechRestockPatchTargets.CanUseRestockWorkGiver;
            }

            private static bool Prepare()
            {
                return TargetMethod() != null;
            }

            private static void Postfix(Pawn pawn, ref bool __result)
            {
                if (__result || !MechRestockEnabled)
                    return;
                try
                {
                    if (!IsHaulerColonyMechStaff(pawn))
                        return;
                    if (pawn.Downed || pawn.InMentalState)
                        return;
                    // 工作模式：关机/护送时不抢
                    if (!IsMechWorkModeAllowingShopWork(pawn))
                        return;
                    __result = true;
                }
                catch (Exception exception)
                {
                    LogRuntimeErrorOnce(exception);
                }
            }
        }

        /// <summary>
        /// 机械体店员派工：放宽“空闲”判定，并尊重工作模式。
        /// </summary>
        [HarmonyPatch]
        private static class MechDispatcher_IsIdle_Patch
        {
            private static MethodBase TargetMethod()
            {
                return MechRestockPatchTargets.MechDispatcherIsIdleForShopDispatch;
            }

            private static bool Prepare()
            {
                return TargetMethod() != null;
            }

            private static void Postfix(Pawn pawn, ref bool __result)
            {
                if (__result || !MechRestockEnabled)
                    return;
                try
                {
                    if (!IsHaulerColonyMechStaff(pawn))
                        return;
                    if (!IsMechWorkModeAllowingShopWork(pawn))
                        return;
                    if (IsIdleForMechShopDispatch(pawn))
                        __result = true;
                }
                catch (Exception exception)
                {
                    LogRuntimeErrorOnce(exception);
                }
            }
        }

        /// <summary>
        /// 机械体店员派工 ShouldTryDispatch：总开关关时不额外放行；开时再确认工作模式。
        /// </summary>
        [HarmonyPatch]
        private static class MechDispatcher_ShouldTryDispatch_Patch
        {
            private static MethodBase TargetMethod()
            {
                return MechRestockPatchTargets.MechDispatcherShouldTryDispatch;
            }

            private static bool Prepare()
            {
                return TargetMethod() != null;
            }

            private static void Postfix(Pawn pawn, int now, ref bool __result)
            {
                if (!__result || !MechRestockEnabled)
                    return;
                try
                {
                    // 框架已判定可派工时，若工作模式不允许商店活，压回 false
                    if (IsHaulerColonyMechStaff(pawn) && !IsMechWorkModeAllowingShopWork(pawn))
                        __result = false;
                }
                catch (Exception exception)
                {
                    LogRuntimeErrorOnce(exception);
                }
            }
        }

        private static bool IsIdleForMechShopDispatch(Pawn pawn)
        {
            if (pawn?.jobs == null)
                return false;
            if (pawn.CurJob == null)
                return true;
            string defName = pawn.CurJobDef?.defName;
            return !string.IsNullOrEmpty(defName) && MechIdleDispatchJobDefNames.Contains(defName);
        }

        private static bool IsMechWorkModeAllowingShopWork(Pawn pawn)
        {
            if (pawn == null)
                return false;
            try
            {
                MechWorkModeDef mode = pawn.GetMechWorkMode();
                if (mode == null)
                    return true;
                // 仅 Work 模式参与店员补货；充电/关机/护送不抢
                if (mode == MechWorkModeDefOf.Work)
                    return true;
                // 部分版本/模组可能没有全部 DefOf 常量，用 defName 兜底
                string name = mode.defName;
                return name == "Work";
            }
            catch
            {
                return true;
            }
        }
    }
}
