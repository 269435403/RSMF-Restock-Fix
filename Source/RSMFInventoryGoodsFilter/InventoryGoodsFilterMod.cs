using HarmonyLib;
using RSMFInventoryGoodsFilter.Patches;
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
                MechRestockPatchTargets.ValidateAndLog();
                new Harmony("yyyyy.rsmf.restockfix").PatchAll();
            }
            catch (System.Exception exception)
            {
                Log.Error("[RSMF Inventory Goods Filter] " +
                    "RSMF.InventoryGoodsFilter.Error.PatchAllFailed".Translate(exception.Message.Named("error")));
            }
        }
    }
}
