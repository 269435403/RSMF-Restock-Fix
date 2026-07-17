using System;
using System.Reflection;
using HarmonyLib;
using SimManagementLib.SimThingClass;
using Verse;

namespace RSMFInventoryGoodsFilter.Restock
{
    /// <summary>
    /// 玩家补货不变式公式（H' 与 need）。
    /// R1：H&lt;=0 → 始终补满；仅当 (S+P) &lt; H' 时产生缺口。
    /// </summary>
    internal static class RestockInvariant
    {
        private static readonly MethodInfo CountPendingRawMethod =
            AccessTools.Method(typeof(Building_SimContainer), "CountPendingRaw", new[] { typeof(ThingDef) });
        private static readonly MethodInfo CountTotalPendingInRawMethod =
            AccessTools.Method(typeof(Building_SimContainer), "CountTotalPendingInRaw", Type.EmptyTypes);

        /// <summary>
        /// 计算有效触发阈值 H'。
        /// H&lt;=0 → 始终补满到 T；H&gt;0 → clamp 到 [1,T]。
        /// </summary>
        internal static int GetEffectiveThreshold(int target, int configured)
        {
            int t = target < 0 ? 0 : target;
            if (t <= 0)
                return 0;
            if (configured <= 0)
                return t;
            if (configured > t)
                return t;
            return configured < 1 ? 1 : configured;
        }

        /// <summary>
        /// 纯数值缺口：仅当 (S+P) &lt; H' 时返回 need，否则 0。
        /// </summary>
        internal static int CountNeeded(int stored, int pending, int target, int configuredThreshold, int capacityRemain)
        {
            int t = target < 0 ? 0 : target;
            if (t <= 0)
                return 0;

            int h = GetEffectiveThreshold(t, configuredThreshold);
            return CountNeededWithEffectiveThreshold(stored, pending, t, h, capacityRemain);
        }

        /// <summary>
        /// 在已有 H'（通常来自 GetRestockThreshold）时计算缺口。
        /// 使用严格小于：sp &gt;= h 不补。
        /// </summary>
        internal static int CountNeededWithEffectiveThreshold(
            int stored,
            int pending,
            int target,
            int effectiveThreshold,
            int capacityRemain)
        {
            int t = target < 0 ? 0 : target;
            if (t <= 0)
                return 0;

            int h = effectiveThreshold < 0 ? 0 : effectiveThreshold;
            int sp = stored + pending;
            if (sp >= h)
                return 0;

            int need = t - sp;
            if (need <= 0)
                return 0;
            if (capacityRemain <= 0)
                return 0;
            return need > capacityRemain ? capacityRemain : need;
        }

        /// <summary>
        /// 用货柜 API 计算缺口。
        /// avoidReconcile=true 时走 Raw pending/容量路径，对齐 CountNeededRaw / ForWorkScan。
        /// </summary>
        internal static int CountNeededForStorage(Building_SimContainer storage, ThingDef thingDef, bool avoidReconcile)
        {
            if (storage == null || thingDef == null)
                return 0;

            try
            {
                int target = storage.GetTargetCount(thingDef);
                if (target <= 0)
                    return 0;

                // 方案 A 已让 GetRestockThreshold → EffectiveRestockThreshold 返回 H'
                int threshold = storage.GetRestockThreshold(thingDef);
                int stored = storage.CountStored(thingDef);
                int pending = avoidReconcile
                    ? InvokeCountPendingRaw(storage, thingDef)
                    : storage.CountPending(thingDef);
                int capacityRemain = avoidReconcile
                    ? storage.MaxTotalCapacity - storage.CountTotalStored() - InvokeCountTotalPendingInRaw(storage)
                    : storage.GetRemainingCapacityForPending();

                return CountNeededWithEffectiveThreshold(stored, pending, target, threshold, capacityRemain);
            }
            catch (Exception exception)
            {
                if (RestockFixGate.VerboseLog)
                    Log.Warning("[RSMF Inventory Goods Filter][Restock] CountNeededForStorage failed: " + exception.Message);
                return 0;
            }
        }

        private static int InvokeCountPendingRaw(Building_SimContainer storage, ThingDef thingDef)
        {
            if (CountPendingRawMethod != null)
                return (int)CountPendingRawMethod.Invoke(storage, new object[] { thingDef });
            return storage.CountPending(thingDef);
        }

        private static int InvokeCountTotalPendingInRaw(Building_SimContainer storage)
        {
            if (CountTotalPendingInRawMethod != null)
                return (int)CountTotalPendingInRawMethod.Invoke(storage, null);
            return storage.CountTotalPendingIn();
        }
    }
}
