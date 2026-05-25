namespace PhinixClient.Trade
{
    public sealed class TradeItemSnapshot
    {
        public string DefName { get; }

        public int StackCount { get; }

        public string StuffDefName { get; }

        public TradeItemQuality Quality { get; }

        public int HitPoints { get; }

        public TradeItemSnapshot InnerItem { get; }

        public TradeItemSnapshot(
            string defName,
            int stackCount,
            int hitPoints,
            TradeItemQuality quality = TradeItemQuality.None,
            string stuffDefName = "",
            TradeItemSnapshot innerItem = null)
        {
            DefName = defName ?? string.Empty;
            StackCount = stackCount;
            HitPoints = hitPoints;
            Quality = quality;
            StuffDefName = stuffDefName ?? string.Empty;
            InnerItem = innerItem;
        }
    }
}
