namespace PhinixClient.Trade
{
    public enum TradeFailureReason
    {
        None = 0,
        SessionInvalid = 1,
        LoginInvalid = 2,
        InternalServerError = 3,
        OtherPartyOffline = 4,
        OtherPartyDoesNotExist = 5,
        AlreadyTrading = 6,
        TradeDoesNotExist = 7,
        NotAcceptingTrades = 8,
        NotTradeParticipant = 9
    }
}
