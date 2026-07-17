using HarmonyLib;
using SimManagementLib.SimAI;
using SimManagementLib.SimDialog;
using SimManagementLib.SimMapComp;
using SimManagementLib.SimThingClass;
using SimManagementLib.SimThingComp;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Verse;

namespace RSMFInventoryGoodsFilter.Restock
{
    /// <summary>
    /// R0锛氬惎鍔ㄦ椂鎺㈡祴琛ヨ揣淇鎵€闇€鐨勬鏋剁鍙枫€?    /// 浠呰褰曞彲鐢ㄦ€э紝涓嶆敼鍙樹换浣曡ˉ璐ц涓恒€?    /// </summary>
    internal static class RestockPatchTargets
    {
        private const string LogPrefix = "[RSMF Inventory Goods Filter][Restock] ";

        // Building_SimContainer
        internal static MethodInfo CountNeeded;
        internal static MethodInfo CountNeededForWorkScan;
        internal static MethodInfo CountNeededRaw;
        internal static MethodInfo GetRestockThreshold;
        internal static MethodInfo ReservePending;
        internal static MethodInfo CancelPending;
        internal static MethodInfo ClearOrphanedPendingIn;
        internal static MethodInfo ReconcilePendingReservations;
        internal static MethodInfo MarkRestockQueueDirty;
        internal static MethodInfo Deposit;

        // GoodsItemData
        internal static MethodInfo GetEffectiveRestockThreshold;
        internal static MethodInfo NormalizeRestockThreshold;

        // MapComponent_RestockTaskQueue
        internal static MethodInfo MarkStorageDirty;
        internal static MethodInfo MarkDirty;
        internal static MethodInfo ResetAndRebuildAll;
        internal static MethodInfo TryMakeJobForPawn;
        internal static MethodInfo TryDispatchIdleRestockPawns;
        internal static MethodInfo TryMakeJobFromTask;

        // JobDriver / UI / settings
        internal static MethodInfo TryMakePreToilReservations;
        internal static MethodInfo ApplySettings;
        internal static MethodInfo DrawItemRow;
        internal static MethodInfo DrawBottomBar;
        internal static MethodInfo CreateDebugSnapshot;

        // Types
        internal static Type BuildingSimContainerType;
        internal static Type GoodsItemDataType;
        internal static Type RestockQueueType;
        internal static Type DepositJobDriverType;
        internal static Type GoodsDataCompType;
        internal static Type GoodsManagerDialogType;
        internal static Type MechDispatcherType;

        private static bool validated;

        internal static bool HasPublicNeedApis =>
            CountNeeded != null && CountNeededForWorkScan != null && GetRestockThreshold != null;

        internal static bool HasPrivateNeedRaw => CountNeededRaw != null;

        internal static bool HasThresholdApi => GetEffectiveRestockThreshold != null;

        internal static bool HasNormalizeApi => NormalizeRestockThreshold != null;

        internal static bool HasReservationApis =>
            ReservePending != null && CancelPending != null && ClearOrphanedPendingIn != null &&
            ReconcilePendingReservations != null;

        internal static bool HasQueueApis =>
            MarkRestockQueueDirty != null && MarkStorageDirty != null && MarkDirty != null &&
            ResetAndRebuildAll != null && TryMakeJobForPawn != null;

        internal static bool HasPrivateDispatchApis =>
            TryDispatchIdleRestockPawns != null && TryMakeJobFromTask != null;

        internal static bool HasJobDriverApi => TryMakePreToilReservations != null;

        internal static bool HasApplySettingsApi => ApplySettings != null;

        internal static bool HasUiApi => DrawItemRow != null;

        internal static bool HasBottomBarApi => DrawBottomBar != null;

        internal static bool HasQueueDebugSnapshot => CreateDebugSnapshot != null;

        internal static bool HasMechDispatcherType => MechDispatcherType != null;

        internal static void ValidateAndLog()
        {
            if (validated)
                return;
            validated = true;

            Probe();
            LogProbeReport();
        }

        private static void Probe()
        {
            BuildingSimContainerType = typeof(Building_SimContainer);
            GoodsItemDataType = typeof(GoodsItemData);
            RestockQueueType = typeof(MapComponent_RestockTaskQueue);
            DepositJobDriverType = typeof(JobDriver_DepositToMegaStorage);
            GoodsDataCompType = typeof(ThingComp_GoodsData);
            GoodsManagerDialogType = typeof(Dialog_GoodsManager);
            MechDispatcherType = AccessTools.TypeByName("SimManagementLib.SimMapComp.MapComponent_MechShopStaffDispatcher")
                ?? typeof(MapComponent_MechShopStaffDispatcher);

            CountNeeded = AccessTools.Method(BuildingSimContainerType, "CountNeeded", new[] { typeof(ThingDef) });
            CountNeededForWorkScan = AccessTools.Method(BuildingSimContainerType, "CountNeededForWorkScan", new[] { typeof(ThingDef) });
            CountNeededRaw = AccessTools.Method(BuildingSimContainerType, "CountNeededRaw", new[] { typeof(ThingDef) });
            GetRestockThreshold = AccessTools.Method(BuildingSimContainerType, "GetRestockThreshold", new[] { typeof(ThingDef) });
            ReservePending = AccessTools.Method(BuildingSimContainerType, "ReservePending", new[] { typeof(ThingDef), typeof(int) });
            CancelPending = AccessTools.Method(BuildingSimContainerType, "CancelPending", new[] { typeof(ThingDef), typeof(int) });
            ClearOrphanedPendingIn = AccessTools.Method(BuildingSimContainerType, "ClearOrphanedPendingIn", Type.EmptyTypes);
            ReconcilePendingReservations = AccessTools.Method(BuildingSimContainerType, "ReconcilePendingReservations", Type.EmptyTypes);
            MarkRestockQueueDirty = AccessTools.Method(BuildingSimContainerType, "MarkRestockQueueDirty", new[] { typeof(ThingDef), typeof(string) });
            Deposit = AccessTools.Method(BuildingSimContainerType, "Deposit", new[] { typeof(Pawn), typeof(ThingDef), typeof(int) });

            GetEffectiveRestockThreshold = AccessTools.Method(
                GoodsItemDataType,
                "GetEffectiveRestockThreshold",
                new[] { typeof(int), typeof(int) });
            NormalizeRestockThreshold = AccessTools.Method(
                GoodsItemDataType,
                "NormalizeRestockThreshold",
                new[] { typeof(int), typeof(int) });

            MarkStorageDirty = AccessTools.Method(RestockQueueType, "MarkStorageDirty", new[] { typeof(Building_SimContainer), typeof(string) });
            MarkDirty = AccessTools.Method(RestockQueueType, "MarkDirty", new[] { typeof(Building_SimContainer), typeof(ThingDef), typeof(string) });
            ResetAndRebuildAll = AccessTools.Method(RestockQueueType, "ResetAndRebuildAll", new[] { typeof(string) });
            TryMakeJobForPawn = AccessTools.Method(RestockQueueType, "TryMakeJobForPawn", new[] { typeof(Pawn) });
            TryDispatchIdleRestockPawns = AccessTools.Method(RestockQueueType, "TryDispatchIdleRestockPawns", new[] { typeof(int) });
            TryMakeJobFromTask = AccessTools.Method(
                RestockQueueType,
                "TryMakeJobFromTask",
                new[] { typeof(Pawn), typeof(RestockTask), typeof(int) });

            // 绛惧悕鎺㈡祴澶辫触鏃舵寜鍚嶇О鍥為€€锛屽吋瀹规鏋跺唴閮ㄧ被鍨嬪彉鍔ㄣ€?            if (TryMakeJobFromTask == null)
                TryMakeJobFromTask = AccessTools.Method(RestockQueueType, "TryMakeJobFromTask");

            TryMakePreToilReservations = AccessTools.Method(
                DepositJobDriverType,
                "TryMakePreToilReservations",
                new[] { typeof(bool) });

            ApplySettings = AccessTools.Method(
                GoodsDataCompType,
                "ApplySettings",
                new[] { typeof(string), typeof(Dictionary<string, GoodsItemData>) });

            DrawItemRow = AccessTools.Method(
                GoodsManagerDialogType,
                "DrawItemRow",
                new[] { typeof(UnityEngine.Rect), typeof(ThingDef), typeof(bool), typeof(SimManagementLib.Pojo.RuntimeGoodsCategory) });
            DrawBottomBar = AccessTools.Method(
                GoodsManagerDialogType,
                "DrawBottomBar",
                new[] { typeof(UnityEngine.Rect) });
            CreateDebugSnapshot = AccessTools.Method(RestockQueueType, "CreateDebugSnapshot", Type.EmptyTypes);
        }

        private static void LogProbeReport()
        {
            List<string> publicOk = new List<string>();
            List<string> nonPublicOk = new List<string>();
            List<string> missing = new List<string>();

            Classify(publicOk, nonPublicOk, missing, CountNeeded, "Building_SimContainer.CountNeeded(ThingDef)", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, CountNeededForWorkScan, "Building_SimContainer.CountNeededForWorkScan(ThingDef)", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, CountNeededRaw, "Building_SimContainer.CountNeededRaw(ThingDef)", expectedNonPublic: true);
            Classify(publicOk, nonPublicOk, missing, GetRestockThreshold, "Building_SimContainer.GetRestockThreshold(ThingDef)", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, ReservePending, "Building_SimContainer.ReservePending(ThingDef,int)", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, CancelPending, "Building_SimContainer.CancelPending(ThingDef,int)", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, ClearOrphanedPendingIn, "Building_SimContainer.ClearOrphanedPendingIn()", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, ReconcilePendingReservations, "Building_SimContainer.ReconcilePendingReservations()", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, MarkRestockQueueDirty, "Building_SimContainer.MarkRestockQueueDirty(ThingDef,string)", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, Deposit, "Building_SimContainer.Deposit(Pawn,ThingDef,int)", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, GetEffectiveRestockThreshold, "GoodsItemData.GetEffectiveRestockThreshold(int,int)", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, NormalizeRestockThreshold, "GoodsItemData.NormalizeRestockThreshold(int,int)", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, MarkStorageDirty, "MapComponent_RestockTaskQueue.MarkStorageDirty(...)", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, MarkDirty, "MapComponent_RestockTaskQueue.MarkDirty(...)", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, ResetAndRebuildAll, "MapComponent_RestockTaskQueue.ResetAndRebuildAll(string)", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, TryMakeJobForPawn, "MapComponent_RestockTaskQueue.TryMakeJobForPawn(Pawn)", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, TryDispatchIdleRestockPawns, "MapComponent_RestockTaskQueue.TryDispatchIdleRestockPawns(int)", expectedNonPublic: true);
            Classify(publicOk, nonPublicOk, missing, TryMakeJobFromTask, "MapComponent_RestockTaskQueue.TryMakeJobFromTask(...)", expectedNonPublic: true);
            Classify(publicOk, nonPublicOk, missing, TryMakePreToilReservations, "JobDriver_DepositToMegaStorage.TryMakePreToilReservations(bool)", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, ApplySettings, "ThingComp_GoodsData.ApplySettings(...)", expectedNonPublic: false);
            Classify(publicOk, nonPublicOk, missing, DrawItemRow, "Dialog_GoodsManager.DrawItemRow(...)", expectedNonPublic: true);
            Classify(publicOk, nonPublicOk, missing, DrawBottomBar, "Dialog_GoodsManager.DrawBottomBar(Rect)", expectedNonPublic: true);
            Classify(publicOk, nonPublicOk, missing, CreateDebugSnapshot, "MapComponent_RestockTaskQueue.CreateDebugSnapshot()", expectedNonPublic: false);

            if (MechDispatcherType == null)
                missing.Add("MapComponent_MechShopStaffDispatcher (type)");
            else
                publicOk.Add("MapComponent_MechShopStaffDispatcher (type)");

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("R0 restock patch target probe:");
            sb.Append("  public OK (").Append(publicOk.Count).Append("): ").AppendLine(string.Join(", ", publicOk));
            sb.Append("  non-public OK (").Append(nonPublicOk.Count).Append("): ").AppendLine(string.Join(", ", nonPublicOk));
            if (missing.Count == 0)
                sb.Append("  missing: (none)");
            else
                sb.Append("  missing (").Append(missing.Count).Append("): ").Append(string.Join(", ", missing));

            Log.Message(LogPrefix + sb);

            if (missing.Count > 0)
            {
                Log.Warning(LogPrefix +
                    "RSMF.InventoryGoodsFilter.Restock.ProbeMissing".Translate(string.Join(", ", missing).Named("targets")));
            }
            else
            {
                Log.Message(LogPrefix + "RSMF.InventoryGoodsFilter.Restock.ProbeOk".Translate());
            }
        }

        private static void Classify(
            List<string> publicOk,
            List<string> nonPublicOk,
            List<string> missing,
            MethodInfo method,
            string name,
            bool expectedNonPublic)
        {
            if (method == null)
            {
                missing.Add(name);
                return;
            }

            if (expectedNonPublic || !method.IsPublic)
                nonPublicOk.Add(name + DescribeAccess(method));
            else
                publicOk.Add(name + DescribeAccess(method));
        }

        private static string DescribeAccess(MethodInfo method)
        {
            if (method.IsPublic)
                return " [public]";
            if (method.IsFamily)
                return " [protected]";
            if (method.IsAssembly)
                return " [internal]";
            if (method.IsFamilyOrAssembly)
                return " [protected internal]";
            return " [private]";
        }
    }
}

