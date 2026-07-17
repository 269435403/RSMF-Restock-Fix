using RimWorld;
using System.Collections.Generic;
using System.Diagnostics;
using Verse;

namespace RSMFInventoryGoodsFilter
{
    /// <summary>
    /// 按 Map 与识别模式缓存仓库库存快照，避免商品窗口每帧全图扫描。
    /// </summary>
    internal static class MapInventorySnapshotCache
    {
        internal const int CacheTicks = 120;
        private const string LogPrefix = "[RSMF Inventory Goods Filter] ";
        private static readonly IInventorySourceAdapter[] SourceAdapters =
        {
            AdaptiveStorageInventorySourceAdapter.Instance,
            VanillaInventorySourceAdapter.Instance
        };

        private sealed class CacheEntry
        {
            internal Dictionary<ThingDef, int> Counts = new Dictionary<ThingDef, int>();
            internal int BuiltTick = int.MinValue;
            internal bool LooseMode;
        }

        private static readonly Dictionary<int, CacheEntry> Entries = new Dictionary<int, CacheEntry>();
        private static int generation;

        /// <summary>设置或强制失效后递增，供窗口检测是否需要重建过滤缓存。</summary>
        internal static int Generation => generation;

        internal static Dictionary<ThingDef, int> GetCounts(Map map, bool forceRebuild = false)
        {
            if (map == null)
                return new Dictionary<ThingDef, int>();

            bool looseMode = InventoryGoodsFilterMod.Settings != null &&
                InventoryGoodsFilterMod.Settings.LooseInventoryRecognition;
            int mapId = map.uniqueID;
            int now = Find.TickManager?.TicksGame ?? 0;

            if (!forceRebuild &&
                Entries.TryGetValue(mapId, out CacheEntry existing) &&
                existing != null &&
                existing.LooseMode == looseMode &&
                now - existing.BuiltTick <= CacheTicks)
            {
                return existing.Counts;
            }

            CacheEntry entry = BuildEntry(map, looseMode, now);
            Entries[mapId] = entry;
            return entry.Counts;
        }

        internal static void Invalidate(Map map)
        {
            if (map == null)
                return;
            Entries.Remove(map.uniqueID);
            generation++;
        }

        internal static void InvalidateAll()
        {
            Entries.Clear();
            generation++;
        }

        private static CacheEntry BuildEntry(Map map, bool looseMode, int now)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            Dictionary<ThingDef, int> counts = new Dictionary<ThingDef, int>();
            HashSet<int> collectedThingIds = new HashSet<int>();
            int scanned = 0;
            for (int i = 0; i < SourceAdapters.Length; i++)
                SourceAdapters[i].Collect(map, looseMode, collectedThingIds, counts, ref scanned);

            stopwatch.Stop();
            Log.Message(LogPrefix + "RSMF.InventoryGoodsFilter.Perf.SnapshotRebuilt".Translate(
                map.uniqueID.Named("mapId"),
                (looseMode ? "loose" : "strict").Named("mode"),
                scanned.Named("scanned"),
                counts.Count.Named("kinds"),
                stopwatch.ElapsedMilliseconds.Named("ms"),
                CacheTicks.Named("cacheTicks")));

            return new CacheEntry
            {
                Counts = counts,
                BuiltTick = now,
                LooseMode = looseMode
            };
        }
    }
}
