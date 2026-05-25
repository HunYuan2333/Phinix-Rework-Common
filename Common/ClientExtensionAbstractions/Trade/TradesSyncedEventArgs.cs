using System;
using System.Collections.Generic;
using System.Linq;

namespace PhinixClient.Trade
{
    public class TradesSyncedEventArgs : EventArgs
    {
        public ClientTradeSnapshot[] Trades { get; }

        public string[] TradeIds => Trades.Select(trade => trade.TradeId).ToArray();

        public TradesSyncedEventArgs(IEnumerable<ClientTradeSnapshot> trades)
        {
            Trades = (trades ?? Array.Empty<ClientTradeSnapshot>()).ToArray();
        }
    }
}
