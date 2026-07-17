using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using SimManagementLib.SimMapComp;
using SimManagementLib.SimThingClass;
using SimManagementLib.SimThingComp;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RSMFInventoryGoodsFilter.Restock
{
    /// <summary>
    /// R4：补货行状态文案与只读诊断摘要。
    /// 诊断路径禁止 Reconcile / MarkDirty / ClearOrphaned。
    /// </summary>
    internal static class RestockStatusPresenter
    {
        private const string DepositJobDefName = "DepositToMegaStorage";
        private const string RestockWorkGiverDefName = "RestockMegaStorage";

        private static int cacheTick = -1;
        private static int cacheStorageId = -1;
        private static readonly Dictionary<string, RowStatusSnapshot> RowCache =
            new Dictionary<string, RowStatusSnapshot>();

        private static int staffCacheTick = -1;
        private static int staffCacheMapId = -1;
        private static int staffEligibleCached;

        private static bool diagnosticErrorLogged;

        internal enum RestockRowState
        {
            Disabled,
            TargetZero,
            Satisfied,
            NotTriggered,
            InProgress,
            Queued,
            BlockedNoSupply,
            BlockedNoStaff,
            BlockedOther,
            Interrupted,
            Unknown
        }

        internal sealed class RowStatusSnapshot
        {
            internal bool Enabled;
            internal int Stored;
            internal int Target;
            internal int RawThreshold;
            internal int EffectiveThreshold;
            internal int Pending;
            internal int Needed;
            internal RestockRowState State;
            internal string BlockedReason;
            internal string QueueState;
            internal int TrackerTokens;
            internal int SupplyCandidates;
            internal int StaffEligible;
            internal string StatusLabel;
            internal string ShortLine;
            internal string Tooltip;
        }

        internal static string BuildRowStatus(Building_SimContainer storage, ThingDef def)
        {
            RowStatusSnapshot snap = GetOrBuildSnapshot(storage, def);
            return snap?.ShortLine ?? string.Empty;
        }

        internal static string BuildRowTooltip(Building_SimContainer storage, ThingDef def)
        {
            RowStatusSnapshot snap = GetOrBuildSnapshot(storage, def);
            return snap?.Tooltip ?? string.Empty;
        }

        internal static RowStatusSnapshot GetOrBuildSnapshot(Building_SimContainer storage, ThingDef def)
        {
            if (storage == null || storage.Destroyed || def == null)
                return null;

            int tick = Find.TickManager?.TicksGame ?? 0;
            if (cacheTick != tick || cacheStorageId != storage.thingIDNumber)
            {
                RowCache.Clear();
                cacheTick = tick;
                cacheStorageId = storage.thingIDNumber;
            }

            if (RowCache.TryGetValue(def.defName, out RowStatusSnapshot cached) && cached != null)
                return cached;

            RowStatusSnapshot snap = BuildSnapshot(storage, def);
            RowCache[def.defName] = snap;
            return snap;
        }

        /// <summary>
        /// 只读本柜诊断。禁止任何写路径。
        /// </summary>
        internal static string BuildDiagnostic(Building_SimContainer storage)
        {
            if (storage == null || storage.Destroyed)
                return string.Empty;

            try
            {
                StringBuilder sb = new StringBuilder();
                Map map = storage.Map;
                ThingComp_GoodsData goodsComp = storage.GetComp<ThingComp_GoodsData>();
                int staff = CountEligibleStaff(map);
                RestockQueueDebugSnapshot queueSnap = TryGetQueueSnapshot(map);

                sb.AppendLine("[RSMF Inventory Goods Filter] Storage Restock Diagnostics");
                sb.AppendLine("generated=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("tick=" + (Find.TickManager?.TicksGame ?? 0));
                sb.AppendLine("storage=" + storage.LabelCap
                    + " thingID=" + storage.thingIDNumber
                    + " def=" + (storage.def?.defName ?? "null"));
                sb.AppendLine("map=" + (map != null ? map.uniqueID.ToString() : "null")
                    + " category=" + (goodsComp?.ActiveGoodsDefName ?? ""));
                sb.AppendLine("capacity=" + storage.CountTotalStored() + "/" + storage.MaxTotalCapacity
                    + " pendingIn=" + storage.CountTotalPendingIn());
                sb.AppendLine("staffEligibleCount=" + staff);
                sb.AppendLine(RestockReservationTracker.BuildSummaryForStorage(storage.thingIDNumber));

                if (queueSnap != null)
                {
                    sb.AppendLine("queue dirty=" + queueSnap.DirtyCount
                        + " ready=" + queueSnap.ReadyCount
                        + " blocked=" + queueSnap.BlockedCount
                        + " lastProcessTick=" + queueSnap.LastProcessTick
                        + " lastRebuildTick=" + queueSnap.LastRebuildTick
                        + " lastReason=" + (queueSnap.LastReason ?? ""));
                }
                else
                {
                    sb.AppendLine("queue=N/A");
                }

                sb.AppendLine();
                sb.AppendLine("defName,enabled,T,rawH,H',S,P,needed,queueState,blockedReason,activeJobs,trackerTokens,supplyCandidateCount,staffEligibleCount,status");

                foreach (ThingDef def in storage.ActiveDefs)
                {
                    if (def == null)
                        continue;

                    RowStatusSnapshot snap = BuildSnapshot(storage, def, staff, queueSnap);
                    GoodsItemData item = goodsComp?.FindItemData(def);
                    bool enabled = item != null && item.enabled;
                    if (!enabled && snap.Stored <= 0 && snap.Target <= 0 && snap.Needed <= 0)
                        continue;

                    sb.Append(def.defName).Append(',')
                        .Append(enabled).Append(',')
                        .Append(snap.Target).Append(',')
                        .Append(snap.RawThreshold).Append(',')
                        .Append(snap.EffectiveThreshold).Append(',')
                        .Append(snap.Stored).Append(',')
                        .Append(snap.Pending).Append(',')
                        .Append(snap.Needed).Append(',')
                        .Append(snap.QueueState ?? "none").Append(',')
                        .Append(EscapeCsv(snap.BlockedReason)).Append(',')
                        .Append(CountActiveDepositJobs(map, storage.thingIDNumber, def)).Append(',')
                        .Append(snap.TrackerTokens).Append(',')
                        .Append(snap.SupplyCandidates).Append(',')
                        .Append(staff).Append(',')
                        .Append(snap.StatusLabel)
                        .AppendLine();
                }

                return sb.ToString().TrimEnd();
            }
            catch (Exception exception)
            {
                if (!diagnosticErrorLogged)
                {
                    diagnosticErrorLogged = true;
                    Log.Warning("[RSMF Inventory Goods Filter][Restock] BuildDiagnostic failed: " + exception.Message);
                }
                return "BuildDiagnostic failed: " + exception.Message;
            }
        }

        private static RowStatusSnapshot BuildSnapshot(Building_SimContainer storage, ThingDef def)
        {
            int staff = CountEligibleStaff(storage.Map);
            RestockQueueDebugSnapshot queueSnap = TryGetQueueSnapshot(storage.Map);
            return BuildSnapshot(storage, def, staff, queueSnap);
        }

        private static RowStatusSnapshot BuildSnapshot(
            Building_SimContainer storage,
            ThingDef def,
            int staffEligible,
            RestockQueueDebugSnapshot queueSnap)
        {
            RowStatusSnapshot snap = new RowStatusSnapshot();
            try
            {
                ThingComp_GoodsData goodsComp = storage.GetComp<ThingComp_GoodsData>();
                GoodsItemData item = goodsComp?.FindItemData(def);
                snap.Enabled = item != null && item.enabled;
                snap.Target = storage.GetTargetCount(def);
                snap.RawThreshold = item != null ? item.restockThreshold : 0;
                // GetRestockThreshold 在 R1 下返回 H'
                snap.EffectiveThreshold = storage.GetRestockThreshold(def);
                // 只读：不 forceReconcile
                snap.Stored = storage.CountStored(def);
                snap.Pending = storage.CountPending(def, forceReconcile: false);
                snap.Needed = RestockInvariant.CountNeededForStorage(storage, def, avoidReconcile: true);
                snap.TrackerTokens = RestockReservationTracker.CountForDef(storage.thingIDNumber, def);
                snap.StaffEligible = staffEligible;

                string blockedReason;
                snap.QueueState = ResolveQueueState(queueSnap, storage.thingIDNumber, def, out blockedReason);
                snap.BlockedReason = blockedReason;

                // 货源粗算：仅在 need>0 且尚未判定排队/进行中时做轻量扫描
                bool hasActive = snap.TrackerTokens > 0
                    || CountActiveDepositJobs(storage.Map, storage.thingIDNumber, def) > 0;
                if (snap.Needed > 0 && !hasActive && snap.QueueState == "none")
                    snap.SupplyCandidates = CountSupplyCandidates(storage, def);
                else if (snap.Needed > 0)
                    snap.SupplyCandidates = -1; // 未扫描
                else
                    snap.SupplyCandidates = -1;

                ResolveState(snap, hasActive);
                FormatLabels(snap);
            }
            catch (Exception exception)
            {
                if (RestockFixGate.VerboseLog)
                    Log.Warning("[RSMF Inventory Goods Filter][Restock] BuildSnapshot failed: " + exception.Message);
                snap.State = RestockRowState.Unknown;
                snap.StatusLabel = "RSMF.InventoryGoodsFilter.Restock.State.Unknown".Translate();
                snap.ShortLine = snap.StatusLabel;
                snap.Tooltip = snap.StatusLabel;
            }

            return snap;
        }

        private static void ResolveState(RowStatusSnapshot snap, bool hasActive)
        {
            if (!snap.Enabled)
            {
                snap.State = RestockRowState.Disabled;
                return;
            }

            if (snap.Target <= 0)
            {
                snap.State = RestockRowState.TargetZero;
                return;
            }

            if (snap.Needed <= 0 && snap.Stored >= snap.Target)
            {
                snap.State = RestockRowState.Satisfied;
                return;
            }

            if (snap.Needed <= 0)
            {
                snap.State = RestockRowState.NotTriggered;
                return;
            }

            if (hasActive)
            {
                snap.State = RestockRowState.InProgress;
                return;
            }

            if (snap.QueueState == "ready" || snap.QueueState == "dirty")
            {
                snap.State = RestockRowState.Queued;
                return;
            }

            if (snap.QueueState == "blocked")
            {
                string reason = snap.BlockedReason ?? string.Empty;
                if (LooksLikeNoSupply(reason))
                    snap.State = RestockRowState.BlockedNoSupply;
                else if (LooksLikeNoStaff(reason))
                    snap.State = RestockRowState.BlockedNoStaff;
                else
                    snap.State = RestockRowState.BlockedOther;
                return;
            }

            if (snap.SupplyCandidates == 0)
            {
                snap.State = RestockRowState.BlockedNoSupply;
                return;
            }

            if (snap.StaffEligible <= 0)
            {
                snap.State = RestockRowState.BlockedNoStaff;
                return;
            }

            // need>0 且无任务/无 Job：中断
            snap.State = RestockRowState.Interrupted;
        }

        private static void FormatLabels(RowStatusSnapshot snap)
        {
            string stateKey;
            switch (snap.State)
            {
                case RestockRowState.Disabled:
                    stateKey = "RSMF.InventoryGoodsFilter.Restock.State.Disabled";
                    break;
                case RestockRowState.TargetZero:
                    stateKey = "RSMF.InventoryGoodsFilter.Restock.State.TargetZero";
                    break;
                case RestockRowState.Satisfied:
                    stateKey = "RSMF.InventoryGoodsFilter.Restock.State.Satisfied";
                    break;
                case RestockRowState.NotTriggered:
                    stateKey = "RSMF.InventoryGoodsFilter.Restock.State.NotTriggered";
                    break;
                case RestockRowState.InProgress:
                    stateKey = "RSMF.InventoryGoodsFilter.Restock.State.InProgress";
                    break;
                case RestockRowState.Queued:
                    stateKey = "RSMF.InventoryGoodsFilter.Restock.State.Queued";
                    break;
                case RestockRowState.BlockedNoSupply:
                    stateKey = "RSMF.InventoryGoodsFilter.Restock.State.BlockedNoSupply";
                    break;
                case RestockRowState.BlockedNoStaff:
                    stateKey = "RSMF.InventoryGoodsFilter.Restock.State.BlockedNoStaff";
                    break;
                case RestockRowState.BlockedOther:
                    stateKey = "RSMF.InventoryGoodsFilter.Restock.State.BlockedOther";
                    break;
                case RestockRowState.Interrupted:
                    stateKey = "RSMF.InventoryGoodsFilter.Restock.State.Interrupted";
                    break;
                default:
                    stateKey = "RSMF.InventoryGoodsFilter.Restock.State.Unknown";
                    break;
            }

            string stateLabel = stateKey.Translate();
            if (snap.State == RestockRowState.BlockedOther && !string.IsNullOrEmpty(snap.BlockedReason))
                stateLabel = "RSMF.InventoryGoodsFilter.Restock.State.BlockedWithReason".Translate(snap.BlockedReason.Named("reason"));

            snap.StatusLabel = stateLabel;
            snap.ShortLine = "RSMF.InventoryGoodsFilter.Restock.RowStatusShort".Translate(
                snap.Stored.Named("S"),
                snap.Target.Named("T"),
                snap.EffectiveThreshold.Named("H"),
                snap.Pending.Named("P"),
                snap.Needed.Named("need"),
                stateLabel.Named("status"));

            string triggerNote = snap.RawThreshold <= 0 || (snap.Target > 0 && snap.RawThreshold == snap.Target)
                ? "RSMF.InventoryGoodsFilter.Restock.IntentAlwaysFill".Translate()
                : "RSMF.InventoryGoodsFilter.Restock.IntentCustom".Translate();

            snap.Tooltip = "RSMF.InventoryGoodsFilter.Restock.RowStatusTip".Translate(
                snap.Stored.Named("S"),
                snap.Target.Named("T"),
                snap.RawThreshold.Named("rawH"),
                snap.EffectiveThreshold.Named("H"),
                snap.Pending.Named("P"),
                snap.Needed.Named("need"),
                stateLabel.Named("status"),
                (snap.QueueState ?? "none").Named("queue"),
                (snap.BlockedReason ?? "-").Named("blocked"),
                snap.TrackerTokens.Named("tokens"),
                triggerNote.Named("intent"));
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

        private static int CountEligibleStaff(Map map)
        {
            if (map?.mapPawns == null)
                return 0;

            int tick = Find.TickManager?.TicksGame ?? 0;
            if (staffCacheTick == tick && staffCacheMapId == map.uniqueID)
                return staffEligibleCached;

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

            staffCacheTick = tick;
            staffCacheMapId = map.uniqueID;
            staffEligibleCached = count;
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

        private static int CountActiveDepositJobs(Map map, int storageId, ThingDef def)
        {
            if (map?.mapPawns == null)
                return 0;

            int count = 0;
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            if (pawns == null)
                return 0;

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                Job job = pawn?.jobs?.curJob;
                if (job == null || job.def == null || job.def.defName != DepositJobDefName)
                    continue;

                // Deposit Job：A=货源，B=货柜，plantDefToSow=预约 def
                Building_SimContainer storage = job.targetB.Thing as Building_SimContainer;
                if (storage == null || storage.thingIDNumber != storageId)
                    continue;

                if (def != null)
                {
                    ThingDef reserved = job.plantDefToSow ?? job.targetA.Thing?.def;
                    if (reserved != null && reserved != def)
                        continue;
                }

                count++;
            }

            return count;
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
                // 货柜内虚拟库存：父级是货柜则跳过
                if (thing.ParentHolder is Building_SimContainer)
                    continue;
                if (thing.IsForbidden(Faction.OfPlayer))
                    continue;
                count++;
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
            return r.Contains("staff") || r.Contains("pawn") || r.Contains("员工") || r.Contains("worker") || r.Contains("岗位");
        }

        private static string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
                return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        internal static Color GetStateColor(RestockRowState state)
        {
            switch (state)
            {
                case RestockRowState.InProgress:
                case RestockRowState.Queued:
                    return new Color(0.45f, 0.75f, 1f);
                case RestockRowState.BlockedNoSupply:
                case RestockRowState.BlockedNoStaff:
                case RestockRowState.BlockedOther:
                    return new Color(1f, 0.7f, 0.3f);
                case RestockRowState.Interrupted:
                    return new Color(1f, 0.35f, 0.35f);
                case RestockRowState.Satisfied:
                case RestockRowState.NotTriggered:
                    return new Color(0.75f, 0.75f, 0.75f);
                default:
                    return Color.white;
            }
        }
    }
}
