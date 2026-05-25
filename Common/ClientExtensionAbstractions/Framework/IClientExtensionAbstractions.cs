using System;
using System.Collections.Generic;
using UserManagement;
using Utils.Framework;

namespace PhinixClient.Framework
{
    public interface IFrameworkClientTransport
    {
        bool HasRemoteCapability(string capability);

        void SendFrameworkPacket(FrameworkPacket packet);
    }

    public interface IClientUserDirectory
    {
        string Uuid { get; }

        bool TryGetUser(string uuid, out ImmutableUser user);
    }

    public interface ITradeItemPayloadEncoder
    {
        FrameworkItemPayload[] EncodeTradeItems(IEnumerable<Trade.TradeItemSnapshot> tradeItems);
    }

    public interface IFrameworkChatClientApi
    {
        event EventHandler HistorySynced;

        FrameworkPacket CreateOutgoingMessage(string rawMessage, ClientFrameworkContext context);

        FrameworkDisplayMessage RenderMessage(FrameworkPacket message);

        FrameworkPacket CreateHistoryRequestPacket(string sessionId, string senderUuid);

        void RequestHistory(IFrameworkClientTransport frameworkClient, bool authenticated, bool loggedIn, string sessionId, string senderUuid);

        UIChatMessage[] BuildUiMessages(IEnumerable<FrameworkDisplayMessage> messages, IClientUserDirectory userDirectory);

        bool TryGetUiMessage(IEnumerable<FrameworkDisplayMessage> messages, string messageId, IClientUserDirectory userDirectory, out UIChatMessage message);

        int CountUnreadExcluding(IEnumerable<FrameworkDisplayMessage> messages, IEnumerable<string> excludedUuids);

        bool ShouldDisplayChatMessage(UIChatMessage message, IEnumerable<string> blockedUserUuids, bool includeBlockedMessages);

        bool ShouldPlayNotification(UIChatMessage message, string localUuid, bool playNoiseOnMessageReceived, bool isInGame, IEnumerable<string> blockedUserUuids);

        UIChatMessage ToUiMessage(FrameworkDisplayMessage message, IClientUserDirectory userDirectory);

        void NotifyHistorySynced();
    }

    public interface IFrameworkTradeClientApi
    {
        event EventHandler RepositoryChanged;
        event EventHandler<Trade.TradeCreationEventArgs> OnTradeCreationSuccess;
        event EventHandler<Trade.TradeCreationEventArgs> OnTradeCreationFailure;
        event EventHandler<Trade.TradeUpdateEventArgs> OnTradeUpdateSuccess;
        event EventHandler<Trade.TradeUpdateEventArgs> OnTradeUpdateFailure;
        event EventHandler<Trade.TradesSyncedEventArgs> OnTradesSynced;
        event EventHandler<Trade.TradeCompletionEventArgs> OnTradeCompleted;
        event EventHandler<Trade.TradeCompletionEventArgs> OnTradeCancelled;

        Phinix.TradeExtension.FrameworkTradeStateSnapshot[] GetRepositoryTrades();
        string[] GetTradeIds();
        Trade.ClientTradeSnapshot[] GetTrades();
        bool TryGetTrade(string tradeId, out Trade.ClientTradeSnapshot trade);
        bool TryGetOtherPartyUuid(string tradeId, string localUuid, out string otherPartyUuid);
        bool TryGetOtherPartyAccepted(string tradeId, string localUuid, out bool otherPartyAccepted);
        bool TryGetPartyAccepted(string tradeId, string partyUuid, out bool accepted);
        bool TryGetItemsOnOffer(string tradeId, string partyUuid, out IEnumerable<Trade.TradeItemSnapshot> items);
        void RequestSnapshot(IFrameworkClientTransport frameworkClient, bool authenticated, bool loggedIn, string sessionId, string senderUuid);
        FrameworkPacket CreateTradeRequest(string otherPartyUuid, ClientFrameworkContext context);
        FrameworkPacket CreateOfferUpdateRequest(string tradeId, IEnumerable<Trade.TradeItemSnapshot> tradeItems, ClientFrameworkContext context);
        FrameworkPacket CreateStatusUpdateRequest(string tradeId, bool? accepted, bool? cancelled, ClientFrameworkContext context);
        void TrackPendingTradeUpdate(string tradeId, string token);
        void HandleSnapshot(FrameworkPacket packet);
        void HandleCreateResponse(FrameworkPacket packet);
        void HandleOfferUpdateResponse(FrameworkPacket packet);
        void HandleStatusUpdateResponse(FrameworkPacket packet);
        void HandleCompletedEvent(FrameworkPacket packet);
        void HandleCancelledEvent(FrameworkPacket packet);
    }
}
