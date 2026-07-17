using RimWorld;
using SimManagementLib.SimThingClass;
using System.Collections.Generic;
using Verse;

namespace RSMFInventoryGoodsFilter
{
    internal interface IInventorySourceAdapter
    {
        void Collect(Map map, bool looseMode, HashSet<int> processedThingIds,
            Dictionary<ThingDef, int> counts, ref int scanned);
    }

    internal sealed class VanillaInventorySourceAdapter : IInventorySourceAdapter
    {
        internal static readonly VanillaInventorySourceAdapter Instance = new VanillaInventorySourceAdapter();

        public void Collect(Map map, bool looseMode, HashSet<int> processedThingIds,
            Dictionary<ThingDef, int> counts, ref int scanned)
        {
            List<Thing> things = map.listerThings?.AllThings;
            if (things == null)
                return;

            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (thing == null)
                    continue;
                scanned++;

                if (!thing.Spawned || thing.Destroyed || thing.Map != map)
                    continue;
                if (thing.def?.category != ThingCategory.Item || thing.stackCount <= 0)
                    continue;
                if (thing.IsForbidden(Faction.OfPlayer))
                    continue;

                if (looseMode)
                {
                    if (IsFrameworkContainerVirtualStock(thing, map))
                        continue;
                }
                else if (!IsStrictPlayerStorage(thing, map))
                {
                    continue;
                }

                AddUnique(thing, processedThingIds, counts);
            }
        }

        private static void AddUnique(Thing thing, HashSet<int> collectedThingIds,
            Dictionary<ThingDef, int> counts)
        {
            if (!collectedThingIds.Add(thing.thingIDNumber))
                return;

            counts.TryGetValue(thing.def, out int count);
            counts[thing.def] = count + thing.stackCount;
        }

        private static bool IsStrictPlayerStorage(Thing thing, Map map)
        {
            SlotGroup slotGroup = map.haulDestinationManager?.SlotGroupAt(thing.Position);
            ISlotGroupParent parent = slotGroup?.parent;
            if (parent == null || parent.Map != map || !IsPlayerStorage(parent))
                return false;
            if (parent is Building_SimContainer)
                return false;
            return thing.IsInValidStorage();
        }

        private static bool IsFrameworkContainerVirtualStock(Thing thing, Map map)
        {
            SlotGroup slotGroup = map.haulDestinationManager?.SlotGroupAt(thing.Position);
            return slotGroup?.parent is Building_SimContainer;
        }

        private static bool IsPlayerStorage(ISlotGroupParent parent)
        {
            if (parent is Zone_Stockpile)
                return true;

            Thing parentThing = parent as Thing;
            return parentThing != null && parentThing.Faction == Faction.OfPlayer;
        }
    }
}
