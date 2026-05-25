using System;
using System.Collections.Generic;
using System.Linq;
using UserManagement;

namespace PhinixClient.Trade
{
    public sealed class ClientTradeSnapshot
    {
        public string TradeId { get; }

        public ImmutableUser OtherParty { get; }

        public string OtherPartyUuid => OtherParty.Uuid;

        public string OtherPartyDisplayName => OtherParty.DisplayName;

        public TradeItemSnapshot[] ItemsOnOffer { get; }

        public TradeItemSnapshot[] OtherPartyItemsOnOffer { get; }

        public bool Accepted { get; }

        public bool OtherPartyAccepted { get; }

        public ClientTradeSnapshot(string tradeId, ImmutableUser otherParty)
            : this(tradeId, otherParty, Array.Empty<TradeItemSnapshot>(), Array.Empty<TradeItemSnapshot>(), false, false)
        {
        }

        public ClientTradeSnapshot(
            string tradeId,
            ImmutableUser otherParty,
            IEnumerable<TradeItemSnapshot> ourItemsOnOffer,
            IEnumerable<TradeItemSnapshot> otherPartyItemsOnOffer,
            bool accepted,
            bool otherPartyAccepted)
        {
            TradeId = tradeId ?? string.Empty;
            OtherParty = otherParty;
            ItemsOnOffer = (ourItemsOnOffer ?? Array.Empty<TradeItemSnapshot>()).ToArray();
            OtherPartyItemsOnOffer = (otherPartyItemsOnOffer ?? Array.Empty<TradeItemSnapshot>()).ToArray();
            Accepted = accepted;
            OtherPartyAccepted = otherPartyAccepted;
        }
    }
}
