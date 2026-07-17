using HarmonyLib;
using RSMFInventoryGoodsFilter.Restock;
using Verse;

namespace RSMFInventoryGoodsFilter
{
    [StaticConstructorOnStartup]
    internal static class InventoryGoodsFilterStartup
    {
        static InventoryGoodsFilterStartup()
        {
            try
            {
                GoodsManagerPatches.ValidateTargets();
                RestockPatchTargets.ValidateAndLog();
                new Harmony("chezhou.rsmf.inventorygoodsfilter").PatchAll();
            }
            catch (System.Exception exception)
            {
                Log.Error("[RSMF Inventory Goods Filter] " +
                    "RSMF.InventoryGoodsFilter.Error.PatchAllFailed".Translate(exception.Message.Named("error")));
            }
        }
    }
}
