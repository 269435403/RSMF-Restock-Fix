using System;
using System.Collections.Generic;
using RimWorld;
using RSMFInventoryGoodsFilter.Patches;
using SimManagementLib.SimMapComp;
using SimManagementLib.SimThingClass;
using SimManagementLib.SimThingComp;
using SimManagementLib.Tool;
using Verse;
using Verse.AI;

namespace RSMFInventoryGoodsFilter.Restock
{
    /// <summary>
    /// R5：后台静默 soft 自愈。
    /// 分片扫描货柜；仅在 need&gt;0 且无推进、Grace 到期后调用 soft RepairStorage。
    /// 默认不弹 Message；仅 Verbose 输出摘要。
    /// </summary>
    public class RestockWatchdogMapComponent : MapComponent
    {
        private const string LogPrefix = "[RSMF Inventory Goods Filter][Restock] ";
        private const string RepairReason = "RSMF.InventoryGoodsFilter: watchdog soft";
        private const string DepositJobDefName = "DepositToMegaStorage";
        private const string RestockWorkGiverDefName = "RestockMegaStorage";

        // 性能：约 1.5s 一拍（60tps），每拍最多 3 柜；Grace≈4s；每柜冷却≈10s
        private const int ScanIntervalTicks = 90;
        private const int MaxStoragesPerPulse = 3;
        private const int GraceTicks = 240;
        private const int RepairCooldownTicks = 600;
        private const int RealBlockCooldownTicks = 1800;
        private const int FirstScanDelayTicks = 90;

        private int nextScanTick = -1;
        private int scanCursor;
        private bool appliedLogged;
        private bool runtimeErrorLogged;

        // 冷却可不落盘；读档后靠 Grace 重新观察即可
        private readonly Dictionary<int, int> lastRepairTickByStorageId = new Dictionary<int, int>();
        private readonly Dictionary<int, int> needObservedSinceByStorageId = new Dictionary<int, int>();

        private static readonly List<Building_SimContainer> StorageBuffer = new List<Building_SimContainer>(32);

        public RestockWatchdogMapComponent(Map map) : base(map)
        {
        }

        public override void MapComponentTick()
        {
            if (!RestockFixGate.WatchdogEnabled)
                return;

            try
            {
                int now = Find.TickManager?.TicksGame ?? 0;
                if (nextScanTick < 0)
                    nextScanTick = now + FirstScanDelayTicks;

                if (now < nextScanTick)
                    return;

                nextScanTick = now + ScanIntervalTicks;
                LogAppliedOnce();
                Pulse(now);
            }
            catch (Exception exception)
            {
                if (!runtimeErrorLogged)
                {
                    runtimeErrorLogged = true;
                    Log.Warning(LogPrefix + "Watchdog tick failed: " + exception.Message);
                }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref nextScanTick, "rsmfWatchdogNextScanTick", -1);
            Scribe_Values.Look(ref scanCursor, "rsmfWatchdogScanCursor", 0);
            // 冷却/观察字典不落盘：避免读档后立刻误修（首扫仍有 FirstScanDelay）
        }

        private void Pulse(int now)
        {
            CollectStorages(map, StorageBuffer);
            int total = StorageBuffer.Count;
            if (total <= 0)
            {
                scanCursor = 0;
                return;
            }

            if (scanCursor < 0 || scanCursor >= total)
                scanCursor = 0;

            // 本拍缓存一次队列快照，避免每柜 CreateDebugSnapshot
            RestockQueueDebugSnapshot queueSnap = TryGetQueueSnapshot(map);
            int staffEligible = CountEligibleStaff(map);
            int examined = 0;
            int repaired = 0;
            int skippedCooldown = 0;
            int skippedNoInterrupt = 0;
            int skippedRealBlock = 0;

            while (examined < MaxStoragesPerPulse && examined < total)
            {
                Building_SimContainer storage = StorageBuffer[scanCursor];
                scanCursor++;
                if (scanCursor >= total)
                    scanCursor = 0;
                examined++;

                if (storage == null || storage.Destroyed || !storage.Spawned)
                    continue;

                int storageId = storage.thingIDNumber;
                if (IsOnCooldown(storageId, now, out int remainingCooldownKind))
                {
                    if (remainingCooldownKind > 0)
                        skippedCooldown++;
                    continue;
                }

                InterruptVerdict verdict = EvaluateStorage(storage, queueSnap, staffEligible);
                if (verdict == InterruptVerdict.None)
                {
                    needObservedSinceByStorageId.Remove(storageId);
                    skippedNoInterrupt++;
                    continue;
                }

                if (verdict == InterruptVerdict.RealBlock)
                {
                    // 真实阻塞：拉长冷却，不狂修
                    lastRepairTickByStorageId[storageId] = now - RepairCooldownTicks + RealBlockCooldownTicks;
                    needObservedSinceByStorageId.Remove(storageId);
                    skippedRealBlock++;
                    if (RestockFixGate.VerboseLog)
                    {
                        Log.Message(LogPrefix + "watchdog skip real-block storage=" +
                            storage.Label + " id=" + storageId);
                    }
                    continue;
                }

                // PossibleInterrupt：需持续 GraceTicks
                if (!needObservedSinceByStorageId.TryGetValue(storageId, out int sinceTick))
                {
                    needObservedSinceByStorageId[storageId] = now;
                    continue;
                }

                if (now - sinceTick < GraceTicks)
                    continue;

                bool ok = RestockRepairService.RepairStorage(storage, soft: true, RepairReason);
                lastRepairTickByStorageId[storageId] = now;
                needObservedSinceByStorageId.Remove(storageId);
                if (ok)
                    repaired++;

                if (RestockFixGate.VerboseLog)
                {
                    Log.Message(LogPrefix + "watchdog repaired storage=" +
                        storage.Label + " id=" + storageId +
                        " ok=" + ok +
                        " graceHeld=" + (now - sinceTick));
                }
            }

            if (RestockFixGate.VerboseLog && (repaired > 0 || skippedRealBlock > 0))
            {
                Log.Message(LogPrefix + "watchdog pulse map=" + map.uniqueID +
                    " storages=" + total +
                    " examined=" + examined +
                    " repaired=" + repaired +
                    " skipCooldown=" + skippedCooldown +
                    " skipNone=" + skippedNoInterrupt +
                    " skipBlock=" + skippedRealBlock);
            }
        }

        private enum InterruptVerdict
        {
            None,
            PossibleInterrupt,
            RealBlock
        }

        /// <summary>
        /// 轻量判定：存在启用商品 need&gt;0，且无 Job/token，且不在 ready/dirty。
        /// 若仅无货源/无员工 → RealBlock（不修）。
        /// </summary>
        private static InterruptVerdict EvaluateStorage(
            Building_SimContainer storage,
            RestockQueueDebugSnapshot queueSnap,
            int staffEligible)
        {
            ThingComp_GoodsData goodsComp = storage.GetComp<ThingComp_GoodsData>();
            if (goodsComp == null || string.IsNullOrEmpty(goodsComp.ActiveGoodsDefName))
                return InterruptVerdict.None;

            bool sawNeed = false;
            bool sawInterrupt = false;
            bool sawRealBlock = false;

            foreach (ThingDef def in storage.ActiveDefs)
            {
                if (def == null)
                    continue;

                GoodsItemData item = goodsComp.FindItemData(def);
                if (item == null || !item.enabled)
                    continue;

                int target = storage.GetTargetCount(def);
                if (target <= 0)
                    continue;

                int needed = RestockInvariant.CountNeededForStorage(storage, def, avoidReconcile: true);
                if (needed <= 0)
                    continue;

                sawNeed = true;

                // 已有推进：token 或活动 Deposit
                if (RestockReservationTracker.HasActiveToken(storage.thingIDNumber, def))
                    continue;
                if (HasActiveDepositJob(storage.Map, storage.thingIDNumber, def))
                    continue;

                string blockedReason;
                string queueState = ResolveQueueState(queueSnap, storage.thingIDNumber, def, out blockedReason);
                if (queueState == "ready" || queueState == "dirty")
                    continue; // 队列已在推进，不算中断

                if (queueState == "blocked")
                {
                    if (LooksLikeNoSupply(blockedReason) || LooksLikeNoStaff(blockedReason))
                    {
                        sawRealBlock = true;
                        continue;
                    }
                    // 其他 blocked 原因：给 soft 一次机会
                    sawInterrupt = true;
                    continue;
                }

                // queue none：粗判货源/员工
                if (staffEligible <= 0)
                {
                    sawRealBlock = true;
                    continue;
                }

                int supply = CountSupplyCandidates(storage, def);
                if (supply <= 0)
                {
                    sawRealBlock = true;
                    continue;
                }

                sawInterrupt = true;
            }

            if (sawInterrupt)
                return InterruptVerdict.PossibleInterrupt;
            if (sawRealBlock && sawNeed)
                return InterruptVerdict.RealBlock;
            return InterruptVerdict.None;
        }

        private bool IsOnCooldown(int storageId, int now, out int remainingKind)
        {
            remainingKind = 0;
            if (!lastRepairTickByStorageId.TryGetValue(storageId, out int last))
                return false;

            int elapsed = now - last;
            // last 可能被 RealBlock 写成「未来相对冷却」：elapsed < RepairCooldownTicks 即冷却中
            if (elapsed < RepairCooldownTicks)
            {
                remainingKind = 1;
                return true;
            }
            return false;
        }

        private static void CollectStorages(Map map, List<Building_SimContainer> buffer)
        {
            buffer.Clear();
            List<Building> buildings = map?.listerBuildings?.allBuildingsColonist;
            if (buildings == null)
                return;

            for (int i = 0; i < buildings.Count; i++)
            {
                Building_SimContainer storage = buildings[i] as Building_SimContainer;
                if (storage != null && !storage.Destroyed && storage.Spawned)
                    buffer.Add(storage);
            }
        }

        private static RestockQueueDebugSnapshot TryGetQueueSnapshot(Map map)
        {
            if (map == null)
                return null;
            try
            {
                return map.GetComponent<MapComponent_RestockTaskQueue>()?.CreateDebugSnapshot();
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveQueueState(
            RestockQueueDebugSnapshot snap,
            int storageId,
            ThingDef def,
            out string blockedReason)
        {
            blockedReason = null;
            if (snap == null || def == null)
                return "none";

            if (snap.DirtyTasks != null)
            {
                for (int i = 0; i < snap.DirtyTasks.Count; i++)
                {
                    RestockTaskKey key = snap.DirtyTasks[i];
                    if (key.StorageId == storageId && key.ThingDef == def)
                        return "dirty";
                }
            }

            if (snap.ReadyTasks != null)
            {
                for (int i = 0; i < snap.ReadyTasks.Count; i++)
                {
                    RestockTask task = snap.ReadyTasks[i];
                    if (task != null && task.StorageId == storageId && task.ThingDef == def)
                        return "ready";
                }
            }

            if (snap.BlockedTasks != null)
            {
                for (int i = 0; i < snap.BlockedTasks.Count; i++)
                {
                    RestockTask task = snap.BlockedTasks[i];
                    if (task != null && task.StorageId == storageId && task.ThingDef == def)
                    {
                        blockedReason = task.StateReason ?? string.Empty;
                        return "blocked";
                    }
                }
            }

            return "none";
        }

        private static bool HasActiveDepositJob(Map map, int storageId, ThingDef def)
        {
            if (map?.mapPawns == null)
                return false;

            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            if (pawns == null)
                return false;

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                Job job = pawn?.jobs?.curJob;
                if (job == null || job.def == null || job.def.defName != DepositJobDefName)
                    continue;

                Building_SimContainer targetStorage = job.targetB.Thing as Building_SimContainer;
                if (targetStorage == null || targetStorage.thingIDNumber != storageId)
                    continue;

                if (def != null)
                {
                    ThingDef reserved = job.plantDefToSow ?? job.targetA.Thing?.def;
                    if (reserved != null && reserved != def)
                        continue;
                }

                return true;
            }

            return false;
        }

        private static int CountEligibleStaff(Map map)
        {
            if (map?.mapPawns == null)
                return 0;

            int count = 0;
            WorkGiverDef restockGiver = DefDatabase<WorkGiverDef>.GetNamedSilentFail(RestockWorkGiverDefName);
            List<Pawn> colonists = map.mapPawns.FreeColonists;
            if (colonists != null)
            {
                for (int i = 0; i < colonists.Count; i++)
                {
                    if (CanDoRestock(colonists[i], restockGiver))
                        count++;
                }
            }

            List<Pawn> mechs = map.mapPawns.SpawnedColonyMechs;
            if (mechs != null)
            {
                for (int i = 0; i < mechs.Count; i++)
                {
                    if (CanDoRestock(mechs[i], restockGiver))
                        count++;
                }
            }

            return count;
        }

        private static bool CanDoRestock(Pawn pawn, WorkGiverDef restockGiver)
        {
            if (pawn == null || pawn.Dead || pawn.Destroyed || !pawn.Spawned)
                return false;
            if (pawn.Downed || pawn.InMentalState)
                return false;
            if (restockGiver == null)
                return true;

            // 玩家机械体：不走殖民者 workSettings；会 Hauling 的机可做框架 Restocking。
            if (ShopStaffUtility.IsAssignableMechanicalStaff(pawn))
            {
                WorkTypeDef workType = restockGiver.workType;
                if (workType == null)
                    return true;
                if (MechRestockWorkTypePatches.CanHaulerMechDoRestocking(pawn, workType))
                    return true;
                List<WorkTypeDef> enabled = pawn.RaceProps?.mechEnabledWorkTypes;
                return enabled != null && enabled.Contains(workType);
            }

            if (pawn.workSettings == null || !pawn.workSettings.EverWork)
                return false;
            try
            {
                if (!pawn.workSettings.WorkIsActive(restockGiver.workType))
                    return false;
            }
            catch
            {
                return false;
            }
            return true;
        }

        private static int CountSupplyCandidates(Building_SimContainer storage, ThingDef def)
        {
            Map map = storage?.Map;
            if (map?.listerThings == null || def == null)
                return 0;

            List<Thing> things = map.listerThings.ThingsOfDef(def);
            if (things == null || things.Count == 0)
                return 0;

            int count = 0;
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (thing == null || thing.Destroyed || !thing.Spawned || thing.stackCount <= 0)
                    continue;
                if (thing is Building_SimContainer)
                    continue;
                if (thing.ParentHolder is Building_SimContainer)
                    continue;
                if (thing.IsForbidden(Faction.OfPlayer))
                    continue;
                count++;
                if (count >= 1)
                    return count; // 只需知道有没有
            }
            return count;
        }

        private static bool LooksLikeNoSupply(string reason)
        {
            if (string.IsNullOrEmpty(reason))
                return false;
            string r = reason.ToLowerInvariant();
            return r.Contains("supply") || r.Contains("货源") || r.Contains("missing") || r.Contains("无货");
        }

        private static bool LooksLikeNoStaff(string reason)
        {
            if (string.IsNullOrEmpty(reason))
                return false;
            string r = reason.ToLowerInvariant();
            return r.Contains("staff") || r.Contains("pawn") || r.Contains("员工") ||
                   r.Contains("worker") || r.Contains("岗位");
        }

        private void LogAppliedOnce()
        {
            if (appliedLogged)
                return;
            appliedLogged = true;
            // 默认一条成品话术；分阶段细节仅 Verbose。
            if (RestockFixGate.VerboseLog)
                Log.Message(LogPrefix + "RSMF.InventoryGoodsFilter.Restock.R5WatchdogApplied".Translate());
            else
                Log.Message(LogPrefix + "RSMF.InventoryGoodsFilter.Restock.EnhancementsApplied".Translate());
        }
    }
}
