using System;

namespace PhinixClient.Trade
{
    public class TradeCreationEventArgs : EventArgs
    {
        public bool Success { get; }

        public string TradeId { get; }

        public string OtherPartyUuid { get; }

        public ClientTradeSnapshot Trade { get; }

        public TradeFailureReason FailureReason { get; }

        public string FailureMessage { get; }

        public TradeCreationEventArgs(ClientTradeSnapshot trade)
        {
            Success = true;
            Trade = trade;
            TradeId = trade?.TradeId ?? string.Empty;
            OtherPartyUuid = trade?.OtherPartyUuid ?? string.Empty;
            FailureReason = TradeFailureReason.None;
            FailureMessage = string.Empty;
        }

        public TradeCreationEventArgs(string otherPartyUuid, TradeFailureReason failureReason, string failureMessage)
        {
            Success = false;
            Trade = null;
            TradeId = string.Empty;
            OtherPartyUuid = otherPartyUuid ?? string.Empty;
            FailureReason = failureReason;
            FailureMessage = failureMessage ?? string.Empty;
        }
    }
}
