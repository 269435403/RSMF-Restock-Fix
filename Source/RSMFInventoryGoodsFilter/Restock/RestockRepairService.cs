using SimManagementLib.SimThingClass;
using Verse;

namespace RSMFInventoryGoodsFilter.Restock
{
    /// <summary>
    /// soft：Reconcile + 清本模组失效 token + MarkDirty。
    /// hard（R4 默认不做）：整图 ResetAndRebuildAll，需二次确认。
    /// </summary>
    internal static class RestockRepairService
    {
        private const string SoftReason = "RSMF.InventoryGoodsFilter: soft repair";

        internal static bool RepairStorage(Building_SimContainer storage, bool soft, string reason)
        {
            if (!RestockFixGate.IsMasterEnabled)
                return false;
            if (storage == null || storage.Destroyed)
                return false;

            string resolvedReason = string.IsNullOrEmpty(reason) ? SoftReason : reason;

            try
            {
                if (!soft)
                {
                    // R4-Min 不提供 hard 重建；避免误触 ResetAndRebuildAll。
                    soft = true;
                }

                // 1) 框架 pending 与活动 Job 对齐
                storage.ReconcilePendingReservations();

                // 2) 仅清本模组失效 token（无活动 Deposit Job）
                int cleared = RestockReservationTracker.ClearStaleForStorage(storage);

                // 3) 标脏唤醒队列（null def = 整柜）
                storage.MarkRestockQueueDirty(null, resolvedReason);

                if (RestockFixGate.VerboseLog)
                {
                    Log.Message("[RSMF Inventory Goods Filter][Restock] Soft repair: " +
                        storage.Label + " reason=" + resolvedReason +
                        " staleTokensCleared=" + cleared);
                }

                return true;
            }
            catch (System.Exception exception)
            {
                if (RestockFixGate.VerboseLog)
                    Log.Warning("[RSMF Inventory Goods Filter][Restock] RepairStorage failed: " + exception.Message);
                return false;
            }
        }
    }
}
