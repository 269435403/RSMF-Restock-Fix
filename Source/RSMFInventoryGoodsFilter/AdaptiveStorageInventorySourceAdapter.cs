using HarmonyLib;
using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace RSMFInventoryGoodsFilter
{
    internal sealed class AdaptiveStorageInventorySourceAdapter : IInventorySourceAdapter
    {
        private const string PackageId = "adaptive.storage.framework";
        private const string TypeName = "AdaptiveStorage.ThingClass";
        private const string LogPrefix = "[RSMF Inventory Goods Filter] ";

        internal static readonly AdaptiveStorageInventorySourceAdapter Instance =
            new AdaptiveStorageInventorySourceAdapter();

        private bool initialized;
        private bool enabled;
        private bool warningLogged;
        private Type thingClassType;
        private PropertyInfo storedThingsProperty;
        private MethodInfo containsAndAllowsMethod;

        public void Collect(Map map, bool looseMode, HashSet<int> processedThingIds,
            Dictionary<ThingDef, int> counts, ref int scanned)
        {
            if (!EnsureInitialized())
                return;

            try
            {
                List<Thing> things = map.listerThings?.AllThings;
                if (things == null)
                    return;

                Dictionary<int, Thing> adaptiveStorageThings = new Dictionary<int, Thing>();
                HashSet<int> disallowedThingIds = new HashSet<int>();
                for (int i = 0; i < things.Count; i++)
                {
                    Thing building = things[i];
                    if (building == null || !(building is Building) || !building.Spawned || building.Destroyed ||
                        building.Map != map || !thingClassType.IsInstanceOfType(building))
                        continue;

                    IEnumerable storedThings = storedThingsProperty.GetValue(building, null) as IEnumerable;
                    if (storedThings == null)
                        throw new InvalidOperationException("StoredThings is not enumerable.");

                    foreach (object entry in storedThings)
                    {
                        Thing thing = entry as Thing;
                        if (thing == null || !thing.Spawned || thing.Destroyed || thing.Map != map ||
                            thing.def?.category != ThingCategory.Item || thing.stackCount <= 0)
                            continue;

                        if (thing.IsForbidden(Faction.OfPlayer) ||
                            !(bool)containsAndAllowsMethod.Invoke(building, new object[] { thing }))
                        {
                            disallowedThingIds.Add(thing.thingIDNumber);
                            continue;
                        }

                        adaptiveStorageThings[thing.thingIDNumber] = thing;
                    }
                }

                foreach (KeyValuePair<int, Thing> pair in adaptiveStorageThings)
                {
                    if (disallowedThingIds.Contains(pair.Key))
                        continue;

                    Thing thing = pair.Value;
                    processedThingIds.Add(pair.Key);
                    counts.TryGetValue(thing.def, out int count);
                    counts[thing.def] = count + thing.stackCount;
                }
                processedThingIds.UnionWith(disallowedThingIds);
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
        }

        private bool EnsureInitialized()
        {
            if (initialized)
                return enabled;

            initialized = true;
            if (!ModsConfig.IsActive(PackageId))
                return false;

            try
            {
                thingClassType = AccessTools.TypeByName(TypeName);
                if (thingClassType == null)
                    throw new TypeLoadException(TypeName);

                storedThingsProperty = AccessTools.Property(thingClassType, "StoredThings");
                containsAndAllowsMethod = AccessTools.Method(thingClassType, "ContainsAndAllows",
                    new[] { typeof(Thing) });
                if (storedThingsProperty == null || containsAndAllowsMethod == null ||
                    containsAndAllowsMethod.ReturnType != typeof(bool))
                    throw new MissingMemberException(TypeName, "StoredThings or ContainsAndAllows(Thing)");

                enabled = true;
            }
            catch (Exception exception)
            {
                Disable(exception);
            }

            return enabled;
        }

        private void Disable(Exception exception)
        {
            enabled = false;
            if (warningLogged)
                return;

            warningLogged = true;
            Exception actual = exception is TargetInvocationException && exception.InnerException != null
                ? exception.InnerException
                : exception;
            Log.Warning(LogPrefix + "Adaptive Storage Framework compatibility was disabled: " + actual.Message);
        }
    }
}
