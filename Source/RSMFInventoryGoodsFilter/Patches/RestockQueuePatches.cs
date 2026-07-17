using HarmonyLib;
using RSMFInventoryGoodsFilter.Restock;
using RimWorld;
using SimManagementLib.SimMapComp;
using SimManagementLib.SimThingClass;
using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;
using Verse.AI;

namespace RSMFInventoryGoodsFilter.Patches
{
    /// <summary>
    /// R2：孤儿清理后标脏 + 主动派工 4B 事务化。
    /// </summary>
    internal static class RestockQueuePatches
    {
        private const string LogPrefix = "[RSMF Inventory Goods Filter][Restock] ";
        internal const string OrphanDirtyReason = "RSMF.InventoryGoodsFilter: orphan pending reconciled";
        internal const string DispatchFailReason = "RSMF.InventoryGoodsFilter: idle dispatch rejected";

        private static bool runtimeErrorLogged;
        private static bool appliedLogged;

        private static readonly FieldInfo PendingInField =
            AccessTools.Field(typeof(Building_SimContainer), "pendingIn");

        // ClearOrphaned 调用链上暂存快照（同线程、可能嵌套极少，用实例字典足够）。
        private static readonly Dictionary<int, Dictionary<ThingDef, int>> PendingSnapshots =
            new Dictionary<int, Dictionary<ThingDef, int>>();

        internal static bool IsEnabled()
        {
            return RestockFixGate.QueueSelfHealEnabled;
        }

        internal static bool IsScaffoldReady()
        {
            return IsEnabled() && RestockPatchTargets.HasQueueApis;
        }

        internal static bool CanPatchOrphan()
        {
            return IsScaffoldReady() && RestockPatchTargets.ClearOrphanedPendingIn != null && PendingInField != null;
        }

        internal static bool CanPatchDispatch()
        {
            return IsScaffoldReady() &&
                RestockPatchTargets.TryDispatchIdleRestockPawns != null &&
                RestockPatchTargets.TryMakeJobForPawn != null;
        }

        internal static void LogAppliedOnce()
        {
            if (appliedLogged || !RestockFixGate.VerboseLog)
                return;
            appliedLogged = true;
            Log.Message(LogPrefix + "RSMF.InventoryGoodsFilter.Restock.R2QueueApplied".Translate());
        }

        internal static void LogRuntimeErrorOnce(Exception exception)
        {
            if (runtimeErrorLogged)
                return;
            runtimeErrorLogged = true;
            Log.Error(LogPrefix +
                "RSMF.InventoryGoodsFilter.Error.RuntimeFallback".Translate(exception.Message.Named("error")));
        }

        internal static Dictionary<ThingDef, int> SnapshotPendingIn(Building_SimContainer storage)
        {
            Dictionary<ThingDef, int> snapshot = new Dictionary<ThingDef, int>();
            if (storage == null || PendingInField == null)
                return snapshot;

            object raw = PendingInField.GetValue(storage);
            if (!(raw is Dictionary<ThingDef, int> pending) || pending.Count == 0)
                return snapshot;

            foreach (KeyValuePair<ThingDef, int> entry in pending)
            {
                if (entry.Key == null || entry.Value <= 0)
                    continue;
                snapshot[entry.Key] = entry.Value;
            }

            return snapshot;
        }

        internal static void MarkChangedDefsDirty(
            Building_SimContainer storage,
            Dictionary<ThingDef, int> before)
        {
            if (storage == null || storage.Destroyed)
                return;

            Dictionary<ThingDef, int> after = SnapshotPendingIn(storage);
            HashSet<ThingDef> changed = new HashSet<ThingDef>();

            if (before != null)
            {
                foreach (KeyValuePair<ThingDef, int> entry in before)
                {
                    int afterCount = 0;
                    if (after != null)
                        after.TryGetValue(entry.Key, out afterCount);
                    if (afterCount != entry.Value)
                        changed.Add(entry.Key);
                }
            }

            if (after != null)
            {
                foreach (KeyValuePair<ThingDef, int> entry in after)
                {
                    int beforeCount = 0;
                    if (before != null)
                        before.TryGetValue(entry.Key, out beforeCount);
                    if (beforeCount != entry.Value)
                        changed.Add(entry.Key);
                }
            }

            if (changed.Count == 0)
                return;

            foreach (ThingDef def in changed)
            {
                if (def == null)
                    continue;
                storage.MarkRestockQueueDirty(def, OrphanDirtyReason);
            }

            if (RestockFixGate.VerboseLog)
            {
                Log.Message(LogPrefix + "Orphan reconcile dirty storage=" + storage.thingIDNumber +
                    " defs=" + changed.Count);
            }
        }

        internal static void StoreSnapshot(Building_SimContainer storage, Dictionary<ThingDef, int> snapshot)
        {
            if (storage == null)
                return;
            PendingSnapshots[storage.thingIDNumber] = snapshot ?? new Dictionary<ThingDef, int>();
        }

        internal static bool TryTakeSnapshot(Building_SimContainer storage, out Dictionary<ThingDef, int> snapshot)
        {
            snapshot = null;
            if (storage == null)
                return false;
            if (!PendingSnapshots.TryGetValue(storage.thingIDNumber, out snapshot))
                return false;
            PendingSnapshots.Remove(storage.thingIDNumber);
            return true;
        }
    }

    /// <summary>
    /// 孤儿/裁剪 pending 后强制标脏，切断「20-&gt;0 后永不补」静默停摆。
    /// </summary>
    [HarmonyPatch]
    internal static class ClearOrphanedPendingInSelfHealPatch
    {
        private static bool Prepare()
        {
            bool ok = RestockQueuePatches.CanPatchOrphan();
            if (ok)
                RestockQueuePatches.LogAppliedOnce();
            return ok;
        }

        private static MethodBase TargetMethod() => RestockPatchTargets.ClearOrphanedPendingIn;

        private static void Prefix(Building_SimContainer __instance)
        {
            if (!RestockQueuePatches.IsEnabled() || __instance == null)
                return;

            try
            {
                RestockQueuePatches.StoreSnapshot(__instance, RestockQueuePatches.SnapshotPendingIn(__instance));
            }
            catch (Exception exception)
            {
                RestockQueuePatches.LogRuntimeErrorOnce(exception);
            }
        }

        private static void Postfix(Building_SimContainer __instance)
        {
            if (!RestockQueuePatches.IsEnabled() || __instance == null)
                return;

            try
            {
                if (!RestockQueuePatches.TryTakeSnapshot(__instance, out Dictionary<ThingDef, int> before))
                    return;
                RestockQueuePatches.MarkChangedDefsDirty(__instance, before);
            }
            catch (Exception exception)
            {
                RestockQueuePatches.LogRuntimeErrorOnce(exception);
            }
        }
    }

    /// <summary>
    /// 4B：包装主动派工。TryTakeOrderedJob 失败时回滚 token 并 MarkDirty，避免任务静默丢失。
    /// 幂等做在 JobDriver 层，机械体同样走 Deposit Job 时自动覆盖。
    /// </summary>
    [HarmonyPatch]
    internal static class TryDispatchIdleRestockPawnsPatch
    {
        private static bool Prepare()
        {
            bool ok = RestockQueuePatches.CanPatchDispatch();
            if (ok)
                RestockQueuePatches.LogAppliedOnce();
            return ok;
        }

        private static MethodBase TargetMethod() => RestockPatchTargets.TryDispatchIdleRestockPawns;

        private static bool Prefix(MapComponent_RestockTaskQueue __instance, int now)
        {
            if (!RestockQueuePatches.IsEnabled())
                return true;

            try
            {
                // 完整接管 private 派工循环，使用 public TryMakeJobForPawn + 失败回滚。
                if (__instance == null)
                    return false;

                Map map = __instance.map;
                if (map?.mapPawns == null)
                    return false;

                // readyTasks 不可见：直接尝试为候选小人派工；无任务时 TryMakeJobForPawn 返回 null。
                List<Pawn> pawns = GetRestockCandidatePawns(__instance);
                if (pawns == null || pawns.Count == 0)
                    return false;

                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn pawn = pawns[i];
                    if (!ShouldTryIdleDispatch(pawn, now))
                        continue;

                    Job job = __instance.TryMakeJobForPawn(pawn);
                    if (job == null)
                        continue;

                    int jobKey = RestockReservationTracker.GetJobKey(job);
                    Building_SimContainer storage = job.GetTarget(TargetIndex.B).Thing as Building_SimContainer;
                    ThingDef thingDef = job.plantDefToSow ?? job.GetTarget(TargetIndex.A).Thing?.def;

                    bool accepted = false;
                    try
                    {
                        accepted = pawn.jobs != null &&
                            pawn.jobs.TryTakeOrderedJob(job, JobTag.MiscWork);
                    }
                    catch (Exception takeException)
                    {
                        RestockQueuePatches.LogRuntimeErrorOnce(takeException);
                        accepted = false;
                    }

                    if (accepted)
                        continue;

                    // 接单失败：回滚可能已写入的 pending token，并标脏重入队。
                    RollbackRejectedDispatch(jobKey, storage, thingDef, job.count);
                }

                return false;
            }
            catch (Exception exception)
            {
                RestockQueuePatches.LogRuntimeErrorOnce(exception);
                // 异常时回退框架原方法，避免彻底停派工。
                return true;
            }
        }

        private static void RollbackRejectedDispatch(
            int jobKey,
            Building_SimContainer storage,
            ThingDef thingDef,
            int jobCount)
        {
            try
            {
                if (jobKey != 0 &&
                    RestockReservationTracker.TryTake(jobKey, out RestockReservationRecord record) &&
                    record != null)
                {
                    Building_SimContainer trackedStorage = storage;
                    if (trackedStorage == null || trackedStorage.thingIDNumber != record.StorageThingId)
                        trackedStorage = FindStorageById(record.StorageThingId);

                    ThingDef def = record.Def ?? thingDef;
                    if (trackedStorage != null && !trackedStorage.Destroyed && def != null && record.Count > 0)
                    {
                        trackedStorage.CancelPending(def, record.Count);
                        trackedStorage.MarkRestockQueueDirty(def, RestockQueuePatches.DispatchFailReason);
                    }

                    return;
                }

                if (storage != null && !storage.Destroyed && thingDef != null)
                {
                    if (jobCount > 0)
                        storage.CancelPending(thingDef, jobCount);
                    storage.MarkRestockQueueDirty(thingDef, RestockQueuePatches.DispatchFailReason);
                }
            }
            catch (Exception exception)
            {
                RestockQueuePatches.LogRuntimeErrorOnce(exception);
            }
        }

        private static Building_SimContainer FindStorageById(int thingId)
        {
            if (thingId < 0 || Current.Game?.Maps == null)
                return null;

            List<Map> maps = Current.Game.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                Map map = maps[i];
                Thing thing = map?.listerThings?.AllThings?.Find(t => t != null && t.thingIDNumber == thingId);
                if (thing is Building_SimContainer storage)
                    return storage;
            }

            return null;
        }

        // --- 下列逻辑对齐框架 MapComponent_RestockTaskQueue 私有辅助，避免反射字段 ---

        // 对齐框架 MapComponent_RestockTaskQueue.IdleJobDefNames / IdleDispatchIntervalTicks
        private static readonly HashSet<string> IdleJobDefNames = new HashSet<string>
        {
            "Wait",
            "Wait_Wander",
            "Wait_MaintainPosture",
            "GotoWander",
            "HaulToCell",
            "Refuel"
        };

        private const int IdleDispatchIntervalTicks = 37;

        private static List<Pawn> GetRestockCandidatePawns(MapComponent_RestockTaskQueue queue)
        {
            List<Pawn> result = new List<Pawn>();
            Map map = queue?.map;
            if (map?.mapPawns == null)
                return result;

            // 对齐框架：FreeColonists + SpawnedColonyMechs，并过滤 CanUseRestockWorkGiver。
            AddRestockCandidatePawns(result, map.mapPawns.FreeColonists);
            AddRestockCandidatePawns(result, map.mapPawns.SpawnedColonyMechs);
            return result;
        }

        private static void AddRestockCandidatePawns(List<Pawn> result, List<Pawn> source)
        {
            if (source == null)
                return;

            for (int i = 0; i < source.Count; i++)
            {
                Pawn pawn = source[i];
                if (pawn == null || result.Contains(pawn))
                    continue;
                if (CanUseRestockWorkGiver(pawn))
                    result.Add(pawn);
            }
        }

        private static bool ShouldTryIdleDispatch(Pawn pawn, int now)
        {
            if (!CanUseRestockWorkGiver(pawn))
                return false;
            if (pawn.jobs == null || !IsIdleForRestockDispatch(pawn))
                return false;

            int offset = pawn.thingIDNumber >= 0 ? pawn.thingIDNumber % IdleDispatchIntervalTicks : 0;
            return (now + offset) % IdleDispatchIntervalTicks == 0;
        }

        private static bool IsIdleForRestockDispatch(Pawn pawn)
        {
            if (pawn?.CurJob == null)
                return true;

            string defName = pawn.CurJobDef?.defName;
            return !string.IsNullOrEmpty(defName) && IdleJobDefNames.Contains(defName);
        }

        private static bool CanUseRestockWorkGiver(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead || !pawn.Spawned || pawn.Downed || pawn.InMentalState)
                return false;

            WorkGiverDef workGiverDef = DefDatabase<WorkGiverDef>.GetNamedSilentFail("RestockMegaStorage");
            if (workGiverDef == null)
                return false;
            if (workGiverDef.workType != null && pawn.WorkTypeIsDisabled(workGiverDef.workType))
                return false;
            if (pawn.workSettings != null && workGiverDef.workType != null &&
                !pawn.workSettings.WorkIsActive(workGiverDef.workType))
                return false;
            if (workGiverDef.requiredCapacities != null)
            {
                for (int i = 0; i < workGiverDef.requiredCapacities.Count; i++)
                {
                    PawnCapacityDef capacity = workGiverDef.requiredCapacities[i];
                    if (capacity != null && pawn.health?.capacities?.CapableOf(capacity) == false)
                        return false;
                }
            }

            return true;
        }
    }
}
