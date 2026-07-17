using HarmonyLib;
using RSMFInventoryGoodsFilter.Restock;
using SimManagementLib.SimThingClass;
using SimManagementLib.SimThingComp;
using System.Reflection;
using Verse;

namespace RSMFInventoryGoodsFilter.Patches
{
    /// <summary>
    /// R1：阈值语义对齐（方案 A）+ 三处 CountNeeded* 严格小于一致性。
    /// - 方案 A：GoodsItemData.GetEffectiveRestockThreshold → H&lt;=0 视为始终补满
    /// - 配合：CountNeeded / CountNeededForWorkScan / CountNeededRaw 使用 (S+P) &lt; H'
    /// </summary>
    internal static class RestockNeedPatches
    {
        private const string LogPrefix = "[RSMF Inventory Goods Filter][Restock] ";
        private static bool runtimeErrorLogged;
        private static bool appliedLogged;

        internal static bool IsEnabled()
        {
            return RestockFixGate.ThresholdSemanticEnabled;
        }

        internal static bool CanPatchThreshold()
        {
            return IsEnabled() && RestockPatchTargets.HasThresholdApi;
        }

        internal static bool CanPatchPublicNeed()
        {
            return IsEnabled() && RestockPatchTargets.HasPublicNeedApis;
        }

        internal static bool CanPatchNeedRaw()
        {
            return IsEnabled() && RestockPatchTargets.HasPrivateNeedRaw;
        }

        internal static void LogAppliedOnce()
        {
            if (appliedLogged || !RestockFixGate.VerboseLog)
                return;
            appliedLogged = true;
            Log.Message(LogPrefix + "RSMF.InventoryGoodsFilter.Restock.R1Applied".Translate());
        }

        internal static void LogRuntimeErrorOnce(System.Exception exception)
        {
            if (runtimeErrorLogged)
                return;
            runtimeErrorLogged = true;
            Log.Error(LogPrefix + "RSMF.InventoryGoodsFilter.Error.RuntimeFallback".Translate(exception.Message.Named("error")));
        }
    }

    /// <summary>
    /// 方案 A：改写有效阈值。configured &lt;= 0 → target（始终补满）。
    /// </summary>
    [HarmonyPatch]
    internal static class GetEffectiveRestockThresholdPatch
    {
        private static bool Prepare()
        {
            bool ok = RestockNeedPatches.CanPatchThreshold();
            if (ok)
                RestockNeedPatches.LogAppliedOnce();
            return ok;
        }

        private static MethodBase TargetMethod() => RestockPatchTargets.GetEffectiveRestockThreshold;

        private static bool Prefix(int targetCount, int configuredThreshold, ref int __result)
        {
            if (!RestockNeedPatches.IsEnabled())
                return true;

            try
            {
                __result = RestockInvariant.GetEffectiveThreshold(targetCount, configuredThreshold);
                return false;
            }
            catch (System.Exception exception)
            {
                RestockNeedPatches.LogRuntimeErrorOnce(exception);
                return true;
            }
        }
    }

    /// <summary>
    /// 公开缺口：与 Raw / WorkScan 同一不变式（严格小于 H'）。
    /// </summary>
    [HarmonyPatch]
    internal static class CountNeededPatch
    {
        private static bool Prepare() => RestockNeedPatches.CanPatchPublicNeed();

        private static MethodBase TargetMethod() => RestockPatchTargets.CountNeeded;

        private static bool Prefix(Building_SimContainer __instance, ThingDef thingDef, ref int __result)
        {
            if (!RestockNeedPatches.IsEnabled())
                return true;

            try
            {
                __result = RestockInvariant.CountNeededForStorage(__instance, thingDef, avoidReconcile: false);
                return false;
            }
            catch (System.Exception exception)
            {
                RestockNeedPatches.LogRuntimeErrorOnce(exception);
                return true;
            }
        }
    }

    /// <summary>
    /// 工作扫描缺口：触发条件与 CountNeeded 一致；pending/容量走 Raw 路径避免高频校正。
    /// </summary>
    [HarmonyPatch]
    internal static class CountNeededForWorkScanPatch
    {
        private static bool Prepare() => RestockNeedPatches.CanPatchPublicNeed();

        private static MethodBase TargetMethod() => RestockPatchTargets.CountNeededForWorkScan;

        private static bool Prefix(Building_SimContainer __instance, ThingDef thingDef, ref int __result)
        {
            if (!RestockNeedPatches.IsEnabled())
                return true;

            try
            {
                __result = RestockInvariant.CountNeededForStorage(__instance, thingDef, avoidReconcile: true);
                return false;
            }
            catch (System.Exception exception)
            {
                RestockNeedPatches.LogRuntimeErrorOnce(exception);
                return true;
            }
        }
    }

    /// <summary>
    /// 预约用原始缺口：触发条件与 CountNeeded 一致；pending/容量走 Raw 路径。
    /// </summary>
    [HarmonyPatch]
    internal static class CountNeededRawPatch
    {
        private static bool Prepare() => RestockNeedPatches.CanPatchNeedRaw();

        private static MethodBase TargetMethod() => RestockPatchTargets.CountNeededRaw;

        private static bool Prefix(Building_SimContainer __instance, ThingDef thingDef, ref int __result)
        {
            if (!RestockNeedPatches.IsEnabled())
                return true;

            try
            {
                __result = RestockInvariant.CountNeededForStorage(__instance, thingDef, avoidReconcile: true);
                return false;
            }
            catch (System.Exception exception)
            {
                RestockNeedPatches.LogRuntimeErrorOnce(exception);
                return true;
            }
        }
    }
}
