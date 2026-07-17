using UnityEngine;
using Verse;

namespace RSMFInventoryGoodsFilter
{
    public class InventoryGoodsFilterSettings : ModSettings
    {
        public bool LooseInventoryRecognition;

        // 玩家可见：可靠补货总开关 + 详细日志。子层字段仅存档兼容，UI 不再绘制。
        public bool EnableRestockInvariantFix = true;
        public bool EnableThresholdSemanticFix = true;
        public bool EnableReservationIdempotencyFix = true;
        public bool EnableQueueSelfHealFix = true;
        public bool EnableRestockUiAssist = true;
        public bool EnableRestockWatchdog = true;
        public bool VerboseRestockLog;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref LooseInventoryRecognition, "looseInventoryRecognition", false);

            Scribe_Values.Look(ref EnableRestockInvariantFix, "enableRestockInvariantFix", true);
            Scribe_Values.Look(ref EnableThresholdSemanticFix, "enableThresholdSemanticFix", true);
            Scribe_Values.Look(ref EnableReservationIdempotencyFix, "enableReservationIdempotencyFix", true);
            Scribe_Values.Look(ref EnableQueueSelfHealFix, "enableQueueSelfHealFix", true);
            Scribe_Values.Look(ref EnableRestockUiAssist, "enableRestockUiAssist", true);
            Scribe_Values.Look(ref EnableRestockWatchdog, "enableRestockWatchdog", true);
            Scribe_Values.Look(ref VerboseRestockLog, "verboseRestockLog", false);

            // 收官：总开关开时一次性纠正旧存档里关掉的子层，避免半残。
            if (Scribe.mode == LoadSaveMode.PostLoadInit && EnableRestockInvariantFix)
            {
                EnableThresholdSemanticFix = true;
                EnableReservationIdempotencyFix = true;
                EnableQueueSelfHealFix = true;
                EnableRestockUiAssist = true;
                EnableRestockWatchdog = true;
            }
        }
    }

    public class InventoryGoodsFilterMod : Mod
    {
        public static InventoryGoodsFilterSettings Settings;

        public InventoryGoodsFilterMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<InventoryGoodsFilterSettings>();
        }

        public override string SettingsCategory()
        {
            return "RSMF.InventoryGoodsFilter.SettingsCategory".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            bool previousLoose = Settings.LooseInventoryRecognition;
            listing.CheckboxLabeled(
                "RSMF.InventoryGoodsFilter.Settings.LooseMode".Translate(),
                ref Settings.LooseInventoryRecognition,
                "RSMF.InventoryGoodsFilter.Settings.LooseModeTip".Translate());

            if (previousLoose != Settings.LooseInventoryRecognition)
                MapInventorySnapshotCache.InvalidateAll();

            listing.GapLine();
            listing.Label("RSMF.InventoryGoodsFilter.Settings.StrictModeNote".Translate());

            listing.GapLine();
            listing.Label("RSMF.InventoryGoodsFilter.Settings.RestockSection".Translate());

            listing.CheckboxLabeled(
                "RSMF.InventoryGoodsFilter.Settings.EnableRestockInvariantFix".Translate(),
                ref Settings.EnableRestockInvariantFix,
                "RSMF.InventoryGoodsFilter.Settings.EnableRestockInvariantFixTip".Translate());

            listing.CheckboxLabeled(
                "RSMF.InventoryGoodsFilter.Settings.VerboseRestockLog".Translate(),
                ref Settings.VerboseRestockLog,
                "RSMF.InventoryGoodsFilter.Settings.VerboseRestockLogTip".Translate());

            listing.End();
            base.DoSettingsWindowContents(inRect);
        }
    }
}
