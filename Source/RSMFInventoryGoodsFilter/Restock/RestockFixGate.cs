namespace RSMFInventoryGoodsFilter.Restock
{
    /// <summary>
    /// 补货增强功能门控。收官后：总开关开则全部增强层强制启用；关则全关。
    /// 旧存档子开关不再分拆暴露给玩家。
    /// </summary>
    internal static class RestockFixGate
    {
        internal static InventoryGoodsFilterSettings Settings => InventoryGoodsFilterMod.Settings;

        internal static bool IsMasterEnabled =>
            Settings == null || Settings.EnableRestockInvariantFix;

        // 收官策略：总开关开 → 全部增强层强制启用（忽略旧子开关 false）
        internal static bool ThresholdSemanticEnabled => IsMasterEnabled;

        internal static bool ReservationIdempotencyEnabled => IsMasterEnabled;

        internal static bool QueueSelfHealEnabled => IsMasterEnabled;

        /// <summary>玩家有用 UX：阈值 tip、拖动跟随、行状态 tip。</summary>
        internal static bool PlayerUxEnabled => IsMasterEnabled;

        /// <summary>兼容旧调用名：等同 PlayerUxEnabled。</summary>
        internal static bool UiAssistEnabled => PlayerUxEnabled;

        internal static bool WatchdogEnabled => IsMasterEnabled;

        /// <summary>开发者底栏：复制诊断 / 重整补货。</summary>
        internal static bool DevToolsEnabled =>
            IsMasterEnabled && Verse.Prefs.DevMode;

        internal static bool VerboseLog =>
            Settings != null && Settings.VerboseRestockLog;
    }
}
