using HarmonyLib;
using RSMFInventoryGoodsFilter.Restock;
using SimManagementLib.SimAI;
using SimManagementLib.SimThingClass;
using System;
using System.Reflection;
using Verse;
using Verse.AI;

namespace RSMFInventoryGoodsFilter.Patches
{
    /// <summary>
    /// R2：Reserve/Cancel/JobDriver 幂等与预约 token。
    /// 核心：完整 Prefix 接管 TryMakePreToilReservations，阻止同一 Job 双写 pending。
    /// 框架参考：JobDriver_DepositToMegaStorage（最小复制 + 幂等）。
    /// </summary>
    internal static class RestockReservationPatches
    {
        private const string LogPrefix = "[RSMF Inventory Goods Filter][Restock] ";
        private static bool runtimeErrorLogged;
        private static bool appliedLogged;

        internal static bool IsEnabled()
        {
            return RestockFixGate.ReservationIdempotencyEnabled;
        }

        internal static bool IsScaffoldReady()
        {
            return IsEnabled() &&
                RestockPatchTargets.HasReservationApis &&
                RestockPatchTargets.HasJobDriverApi;
        }

        internal static void LogAppliedOnce()
        {
            if (appliedLogged || !RestockFixGate.VerboseLog)
                return;
            appliedLogged = true;
            Log.Message(LogPrefix + "RSMF.InventoryGoodsFilter.Restock.R2ReservationApplied".Translate());
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
    /// 完整接管预预约：同一 Job 第二次进入只做物品 Reserve，不再 ReservePending。
    /// </summary>
    [HarmonyPatch]
    internal static class DepositTryMakePreToilReservationsPatch
    {
        private static bool Prepare()
        {
            bool ok = RestockReservationPatches.IsScaffoldReady();
            if (ok)
                RestockReservationPatches.LogAppliedOnce();
            return ok;
        }

        private static MethodBase TargetMethod() => RestockPatchTargets.TryMakePreToilReservations;

        // 框架版本路径：JobDriver_DepositToMegaStorage.TryMakePreToilReservations
        // 复制最小必要逻辑并插入 Job 级幂等 token。
        private static bool Prefix(
            JobDriver_DepositToMegaStorage __instance,
            bool errorOnFailed,
            ref bool __result)
        {
            if (!RestockReservationPatches.IsEnabled())
                return true;

            try
            {
                Job job = __instance.job;
                Pawn pawn = __instance.pawn;
                if (job == null || pawn == null)
                {
                    __result = false;
                    return false;
                }

                Building_SimContainer storage = job.GetTarget(TargetIndex.B).Thing as Building_SimContainer;
                Thing toHaul = job.GetTarget(TargetIndex.A).Thing;
                ThingDef thingDef = job.plantDefToSow ?? toHaul?.def;
                if (storage == null || storage.Destroyed || thingDef == null || toHaul == null)
                {
                    __result = false;
                    return false;
                }

                int jobKey = RestockReservationTracker.GetJobKey(job);
                if (jobKey == 0)
                {
                    // key 不可用：降级走原方法，避免错误幂等。
                    return true;
                }

                // 第二次 PreToil（StartJob 新 Driver）：禁止再 ReservePending。
                if (RestockReservationTracker.HasValidRecord(jobKey, storage, thingDef))
                {
                    RestockReservationTracker.TryGet(jobKey, out RestockReservationRecord existing);
                    int reservedCount = existing != null && existing.Count > 0
                        ? existing.Count
                        : (job.count > 0 ? job.count : toHaul.stackCount);
                    job.count = reservedCount;

                    if (pawn.Reserve(toHaul, job, 1, reservedCount, null, errorOnFailed))
                    {
                        __result = true;
                        return false;
                    }

                    // 物品预约失败：释放 pending + token。
                    storage.CancelPending(thingDef, reservedCount);
                    RestockReservationTracker.Untrack(jobKey);
                    SetReservationReleased(__instance, true);
                    __result = false;
                    return false;
                }

                // 首次 PreToil：Reconcile → 一次 ReservePending → Track → Reserve 物品。
                storage.ReconcilePendingReservations();
                int wanted = job.count > 0 ? job.count : toHaul.stackCount;
                int reserved = storage.ReservePending(thingDef, wanted);
                if (reserved <= 0)
                {
                    __result = false;
                    return false;
                }

                job.count = reserved;
                RestockReservationTracker.Track(jobKey, storage, thingDef, reserved, pawn.thingIDNumber);

                if (pawn.Reserve(toHaul, job, 1, reserved, null, errorOnFailed))
                {
                    __result = true;
                    return false;
                }

                storage.CancelPending(thingDef, reserved);
                RestockReservationTracker.Untrack(jobKey);
                SetReservationReleased(__instance, true);
                __result = false;
                return false;
            }
            catch (Exception exception)
            {
                RestockReservationPatches.LogRuntimeErrorOnce(exception);
                return true;
            }
        }

        private static readonly FieldInfo ReservationReleasedField =
            AccessTools.Field(typeof(JobDriver_DepositToMegaStorage), "reservationReleased");

        private static void SetReservationReleased(JobDriver_DepositToMegaStorage driver, bool value)
        {
            if (ReservationReleasedField == null || driver == null)
                return;
            try
            {
                ReservationReleasedField.SetValue(driver, value);
            }
            catch
            {
                // 字段缺失时忽略；CancelPending 仍会释放 pending。
            }
        }
    }

    /// <summary>
    /// CancelPending 后同步移除 token（覆盖 CleanupReservation / Deposit 失败释放）。
    /// </summary>
    [HarmonyPatch]
    internal static class CancelPendingTrackerPatch
    {
        private static bool Prepare() => RestockReservationPatches.IsScaffoldReady();

        private static MethodBase TargetMethod() => RestockPatchTargets.CancelPending;

        private static void Postfix(Building_SimContainer __instance, ThingDef thingDef, int reservedCount)
        {
            if (!RestockReservationPatches.IsEnabled())
                return;
            if (__instance == null || thingDef == null || reservedCount <= 0)
                return;

            try
            {
                RestockReservationTracker.UntrackMatching(__instance.thingIDNumber, thingDef, reservedCount);
            }
            catch (Exception exception)
            {
                RestockReservationPatches.LogRuntimeErrorOnce(exception);
            }
        }
    }

    // Deposit 内部会调用 CancelPending，由 CancelPendingTrackerPatch 统一摘 token，避免双重 UntrackMatching 误伤并行 Job。
}
