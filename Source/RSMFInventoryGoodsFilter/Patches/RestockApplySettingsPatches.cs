using HarmonyLib;
using RSMFInventoryGoodsFilter.Restock;
using SimManagementLib.SimThingClass;
using SimManagementLib.SimThingComp;
using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace RSMFInventoryGoodsFilter.Patches
{
    /// <summary>
    /// R3：ApplySettings 保存后强制 Reconcile + 标脏，保证改方案必收敛。
    /// 收敛绑总开关，不依赖 UI 辅助开关。
    /// </summary>
    internal static class RestockApplySettingsPatches
    {
        private const string LogPrefix = "[RSMF Inventory Goods Filter][Restock] ";
        internal const string SettingsAppliedReason = "RSMF.InventoryGoodsFilter: settings applied";

        private static bool runtimeErrorLogged;
        private static bool appliedLogged;

        internal static bool IsEnabled()
        {
            return RestockFixGate.IsMasterEnabled;
        }

        internal static bool CanPatch()
        {
            return IsEnabled() && RestockPatchTargets.HasApplySettingsApi;
        }

        internal static void LogAppliedOnce()
        {
            if (appliedLogged || !RestockFixGate.VerboseLog)
                return;
            appliedLogged = true;
            Log.Message(LogPrefix + "RSMF.InventoryGoodsFilter.Restock.R3SettingsApplied".Translate());
        }

        internal static void LogRuntimeErrorOnce(Exception exception)
        {
            if (runtimeErrorLogged)
                return;
            runtimeErrorLogged = true;
            Log.Error(LogPrefix +
                "RSMF.InventoryGoodsFilter.Error.RuntimeFallback".Translate(exception.Message.Named("error")));
        }
    }

    /// <summary>
    /// 保存商品配置后 soft 收敛：ReconcilePendingReservations + MarkRestockQueueDirty。
    /// </summary>
    [HarmonyPatch]
    internal static class ApplySettingsConvergePatch
    {
        private static bool Prepare()
        {
            bool ok = RestockApplySettingsPatches.CanPatch();
            if (ok)
                RestockApplySettingsPatches.LogAppliedOnce();
            return ok;
        }

        private static MethodBase TargetMethod() => RestockPatchTargets.ApplySettings;

        private static void Postfix(ThingComp_GoodsData __instance, string newDefName, Dictionary<string, GoodsItemData> newSettings)
        {
            if (!RestockApplySettingsPatches.IsEnabled())
                return;

            try
            {
                Building_SimContainer storage = __instance?.parent as Building_SimContainer;
                if (storage == null || storage.Destroyed)
                    return;

                RestockRepairService.RepairStorage(storage, soft: true, RestockApplySettingsPatches.SettingsAppliedReason);

                if (RestockFixGate.VerboseLog)
                {
                    Log.Message("[RSMF Inventory Goods Filter][Restock] ApplySettings converge: " +
                        storage.Label + " category=" + (newDefName ?? ""));
                }
            }
            catch (Exception exception)
            {
                RestockApplySettingsPatches.LogRuntimeErrorOnce(exception);
            }
        }
    }
}
