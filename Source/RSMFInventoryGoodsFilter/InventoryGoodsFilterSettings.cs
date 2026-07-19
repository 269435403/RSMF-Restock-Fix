using UnityEngine;
using Verse;

namespace RSMFInventoryGoodsFilter
{
    public class InventoryGoodsFilterSettings : ModSettings
    {
        public bool LooseInventoryRecognition;
        public bool EnableMechRestock = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref LooseInventoryRecognition, "looseInventoryRecognition", false);
            Scribe_Values.Look(ref EnableMechRestock, "enableMechRestock", true);
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
            listing.Label("RSMF.InventoryGoodsFilter.Settings.MechSection".Translate());

            listing.CheckboxLabeled(
                "RSMF.InventoryGoodsFilter.Settings.EnableMechRestock".Translate(),
                ref Settings.EnableMechRestock,
                "RSMF.InventoryGoodsFilter.Settings.EnableMechRestockTip".Translate());

            listing.End();
            base.DoSettingsWindowContents(inRect);
        }
    }
}
