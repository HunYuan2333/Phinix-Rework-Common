using System;

namespace PhinixClient.Trade
{
    public class TradeUpdateEventArgs : EventArgs
    {
        public string TradeId { get; }

        public bool Success { get; }

        public string Token { get; }

        public ClientTradeSnapshot Trade { get; }

        public TradeFailureReason FailureReason { get; }

        public string FailureMessage { get; }

        public TradeUpdateEventArgs(ClientTradeSnapshot trade, string token = "")
        {
            Trade = trade;
            TradeId = trade?.TradeId ?? string.Empty;
            Success = true;
            Token = token ?? string.Empty;
            FailureReason = TradeFailureReason.None;
            FailureMessage = string.Empty;
        }

        public TradeUpdateEventArgs(ClientTradeSnapshot trade, TradeFailureReason failureReason, string failureMessage, string token = "")
        {
            Trade = trade;
            TradeId = trade?.TradeId ?? string.Empty;
            Success = false;
            Token = token ?? string.Empty;
            FailureReason = failureReason;
            FailureMessage = failureMessage ?? string.Empty;
        }
    }
}
