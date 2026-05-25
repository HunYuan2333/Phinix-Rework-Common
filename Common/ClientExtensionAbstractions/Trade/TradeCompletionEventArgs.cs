using System;
using System.Collections.Generic;
using System.Linq;

namespace PhinixClient.Trade
{
    public class TradeCompletionEventArgs : EventArgs
    {
        public string TradeId { get; }

        public bool Success { get; }

        public string OtherPartyUuid { get; }

        public ClientTradeSnapshot Trade { get; }

        public TradeItemSnapshot[] Items { get; }

        public TradeCompletionEventArgs(string tradeId, bool success, string otherPartyUuid, IEnumerable<TradeItemSnapshot> items, ClientTradeSnapshot trade = null)
        {
            TradeId = tradeId ?? string.Empty;
            Success = success;
            OtherPartyUuid = otherPartyUuid ?? string.Empty;
            Trade = trade;
            Items = (items ?? Array.Empty<TradeItemSnapshot>()).ToArray();
        }
    }
}
