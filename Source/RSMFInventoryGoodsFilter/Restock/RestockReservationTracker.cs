using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using SimManagementLib.SimThingClass;
using Verse;
using Verse.AI;

namespace RSMFInventoryGoodsFilter.Restock
{
    /// <summary>
    /// Job 级补货预约 token 表（R2 幂等核心）。
    /// 不落盘；读档后靠框架 Reconcile + 活动 Job 重建，表可清空。
    /// key 绑定 Job 对象身份（优先 loadID），同一 Job 两次 PreToil 必须命中同一 key。
    /// R4：增加只读汇总与清失效 token（不改幂等语义）。
    /// </summary>
    internal static class RestockReservationTracker
    {
        private const string LogPrefix = "[RSMF Inventory Goods Filter][Restock] ";
        private const string DepositJobDefName = "DepositToMegaStorage";

        private static readonly Dictionary<int, RestockReservationRecord> Active =
            new Dictionary<int, RestockReservationRecord>();

        private static bool loadIdFallbackLogged;

        internal static int ActiveCount => Active.Count;

        internal static void ClearAll()
        {
            Active.Clear();
        }

        /// <summary>
        /// 同一 Job 对象在 TryTakeOrderedJob 缓存 Driver 与 StartJob 新 Driver 两次进入时必须返回同一 key。
        /// </summary>
        internal static int GetJobKey(Job job)
        {
            if (job == null)
                return 0;

            // loadID 在 Job 创建后通常可用；为 0 时回退到对象身份哈希。
            if (job.loadID != 0)
                return job.loadID;

            if (!loadIdFallbackLogged)
            {
                loadIdFallbackLogged = true;
                Log.Warning(LogPrefix +
                    "RSMF.InventoryGoodsFilter.Restock.JobKeyFallback".Translate());
            }

            // RuntimeHelpers.GetHashCode 对同一实例稳定，不依赖 GetHashCode 重写。
            return RuntimeHelpers.GetHashCode(job);
        }

        internal static bool TryGet(int jobKey, out RestockReservationRecord record)
        {
            if (jobKey == 0)
            {
                record = null;
                return false;
            }

            return Active.TryGetValue(jobKey, out record);
        }

        internal static bool HasValidRecord(int jobKey, Building_SimContainer storage, ThingDef def)
        {
            if (!TryGet(jobKey, out RestockReservationRecord record) || record == null)
                return false;
            if (storage == null || def == null)
                return false;
            if (record.StorageThingId != storage.thingIDNumber)
                return false;
            if (record.Def != def)
                return false;
            return record.Count > 0;
        }

        internal static void Track(int jobKey, RestockReservationRecord record)
        {
            if (jobKey == 0 || record == null || record.Count <= 0)
                return;
            record.JobKey = jobKey;
            Active[jobKey] = record;

            if (RestockFixGate.VerboseLog)
            {
                Log.Message(LogPrefix + "Track jobKey=" + jobKey +
                    " storage=" + record.StorageThingId +
                    " def=" + (record.Def?.defName ?? "null") +
                    " count=" + record.Count);
            }
        }

        internal static void Track(
            int jobKey,
            Building_SimContainer storage,
            ThingDef def,
            int count,
            int pawnId)
        {
            if (storage == null || def == null || count <= 0)
                return;

            Track(jobKey, new RestockReservationRecord
            {
                JobKey = jobKey,
                StorageThingId = storage.thingIDNumber,
                Def = def,
                Count = count,
                CreatedTick = Find.TickManager?.TicksGame ?? 0,
                PawnId = pawnId
            });
        }

        internal static void Untrack(int jobKey)
        {
            if (jobKey == 0)
                return;

            if (Active.Remove(jobKey) && RestockFixGate.VerboseLog)
                Log.Message(LogPrefix + "Untrack jobKey=" + jobKey);
        }

        /// <summary>
        /// CancelPending / Deposit 路径：按货柜+Def+数量摘掉一条 token（无 job 上下文时）。
        /// </summary>
        internal static bool UntrackMatching(int storageThingId, ThingDef def, int count)
        {
            if (storageThingId < 0 || def == null || count <= 0 || Active.Count == 0)
                return false;

            int exactKey = 0;
            int anyKey = 0;
            foreach (KeyValuePair<int, RestockReservationRecord> entry in Active)
            {
                RestockReservationRecord record = entry.Value;
                if (record == null || record.StorageThingId != storageThingId || record.Def != def)
                    continue;

                if (anyKey == 0)
                    anyKey = entry.Key;

                if (record.Count == count)
                {
                    exactKey = entry.Key;
                    break;
                }
            }

            int removeKey = exactKey != 0 ? exactKey : anyKey;
            if (removeKey == 0)
                return false;

            Untrack(removeKey);
            return true;
        }

        /// <summary>
        /// 主动派工失败回滚：若 token 仍在，返回记录供 CancelPending。
        /// </summary>
        internal static bool TryTake(int jobKey, out RestockReservationRecord record)
        {
            if (!TryGet(jobKey, out record) || record == null)
                return false;
            Active.Remove(jobKey);
            return true;
        }

        // ---------- R4 只读汇总 / 清失效 ----------

        internal static int CountForStorage(int storageThingId)
        {
            if (storageThingId < 0 || Active.Count == 0)
                return 0;

            int count = 0;
            foreach (KeyValuePair<int, RestockReservationRecord> entry in Active)
            {
                if (entry.Value != null && entry.Value.StorageThingId == storageThingId)
                    count++;
            }
            return count;
        }

        internal static int CountForDef(int storageThingId, ThingDef def)
        {
            if (storageThingId < 0 || def == null || Active.Count == 0)
                return 0;

            int count = 0;
            foreach (KeyValuePair<int, RestockReservationRecord> entry in Active)
            {
                RestockReservationRecord record = entry.Value;
                if (record != null && record.StorageThingId == storageThingId && record.Def == def)
                    count++;
            }
            return count;
        }

        internal static int SumCountForDef(int storageThingId, ThingDef def)
        {
            if (storageThingId < 0 || def == null || Active.Count == 0)
                return 0;

            int sum = 0;
            foreach (KeyValuePair<int, RestockReservationRecord> entry in Active)
            {
                RestockReservationRecord record = entry.Value;
                if (record != null && record.StorageThingId == storageThingId && record.Def == def)
                    sum += record.Count;
            }
            return sum;
        }

        internal static bool HasActiveToken(int storageThingId, ThingDef def)
        {
            return CountForDef(storageThingId, def) > 0;
        }

        internal static string BuildSummaryForStorage(int storageThingId)
        {
            if (storageThingId < 0 || Active.Count == 0)
                return "trackerTokens=0";

            StringBuilder sb = new StringBuilder();
            int total = 0;
            foreach (KeyValuePair<int, RestockReservationRecord> entry in Active)
            {
                RestockReservationRecord record = entry.Value;
                if (record == null || record.StorageThingId != storageThingId)
                    continue;

                total++;
                if (total <= 12)
                {
                    sb.Append("  token jobKey=").Append(entry.Key)
                        .Append(" def=").Append(record.Def?.defName ?? "null")
                        .Append(" count=").Append(record.Count)
                        .Append(" pawnId=").Append(record.PawnId)
                        .Append(" tick=").Append(record.CreatedTick)
                        .AppendLine();
                }
            }

            string head = "trackerTokens=" + total;
            if (total == 0)
                return head;
            if (total > 12)
                sb.Append("  ... omitted ").Append(total - 12).AppendLine();
            return head + "\n" + sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 清理指向本柜、但地图上已无对应活动 Deposit Job 的失效 token。
        /// 不碰仍在跑的合法 Job。
        /// </summary>
        internal static int ClearStaleForStorage(Building_SimContainer storage)
        {
            if (storage == null || storage.Destroyed || Active.Count == 0)
                return 0;

            int storageId = storage.thingIDNumber;
            HashSet<int> liveKeys = CollectLiveDepositJobKeys(storage.Map);
            List<int> remove = null;

            foreach (KeyValuePair<int, RestockReservationRecord> entry in Active)
            {
                RestockReservationRecord record = entry.Value;
                if (record == null || record.StorageThingId != storageId)
                    continue;
                if (liveKeys.Contains(entry.Key))
                    continue;

                if (remove == null)
                    remove = new List<int>();
                remove.Add(entry.Key);
            }

            if (remove == null || remove.Count == 0)
                return 0;

            for (int i = 0; i < remove.Count; i++)
                Untrack(remove[i]);

            if (RestockFixGate.VerboseLog)
            {
                Log.Message(LogPrefix + "ClearStaleForStorage storage=" + storageId +
                    " removed=" + remove.Count);
            }

            return remove.Count;
        }

        private static HashSet<int> CollectLiveDepositJobKeys(Map map)
        {
            HashSet<int> keys = new HashSet<int>();
            if (map?.mapPawns == null)
                return keys;

            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            if (pawns == null)
                return keys;

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                Job job = pawn?.jobs?.curJob;
                if (job == null || job.def == null)
                    continue;
                if (job.def.defName != DepositJobDefName)
                    continue;

                int key = GetJobKey(job);
                if (key != 0)
                    keys.Add(key);
            }

            return keys;
        }
    }

    internal sealed class RestockReservationRecord
    {
        internal int JobKey;
        internal int StorageThingId = -1;
        internal ThingDef Def;
        internal int Count;
        internal int CreatedTick;
        internal int PawnId = -1;
    }
}
