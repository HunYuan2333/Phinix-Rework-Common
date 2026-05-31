using System;
using System.Collections.Generic;
using System.Linq;
using Phinix.TradeExtension;
using UserManagement;
using Utils;
using Utils.Framework;
#if NET472
using Verse;
using Thing = Verse.Thing;
#endif

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

    public enum TradeFailureReason
    {
        None = 0,
        SessionInvalid = 1,
        LoginInvalid = 2,
        OtherPartyDoesNotExist = 3,
        OtherPartyOffline = 4,
        NotAcceptingTrades = 5,
        AlreadyTrading = 6,
        TradeDoesNotExist = 7,
        NotTradeParticipant = 8,
        InternalServerError = 9
    }

    public enum TradeItemQuality
    {
        None = 0,
        Awful = 1,
        Poor = 2,
        Normal = 3,
        Good = 4,
        Excellent = 5,
        Masterwork = 6,
        Legendary = 7
    }

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

    public class TradeCreationEventArgs : EventArgs
    {
        public ClientTradeSnapshot Trade { get; }
        public string OtherPartyUuid { get; }
        public TradeFailureReason FailureReason { get; }
        public string FailureMessage { get; }

        public TradeCreationEventArgs(ClientTradeSnapshot trade)
        {
            Trade = trade;
            OtherPartyUuid = trade?.OtherPartyUuid ?? string.Empty;
            FailureReason = TradeFailureReason.None;
            FailureMessage = string.Empty;
        }

        public TradeCreationEventArgs(string otherPartyUuid, TradeFailureReason failureReason, string failureMessage)
        {
            OtherPartyUuid = otherPartyUuid ?? string.Empty;
            FailureReason = failureReason;
            FailureMessage = failureMessage ?? string.Empty;
        }
    }

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

    public class TradesSyncedEventArgs : EventArgs
    {
        public ClientTradeSnapshot[] Trades { get; }

        public TradesSyncedEventArgs(IEnumerable<ClientTradeSnapshot> trades)
        {
            Trades = (trades ?? Array.Empty<ClientTradeSnapshot>()).ToArray();
        }
    }

    public class TradeUpdateEventArgs : EventArgs
    {
        public ClientTradeSnapshot Trade { get; }
        public TradeFailureReason FailureReason { get; }
        public string FailureMessage { get; }
        public string Token { get; }

        public TradeUpdateEventArgs(ClientTradeSnapshot trade, string token = "")
        {
            Trade = trade;
            FailureReason = TradeFailureReason.None;
            FailureMessage = string.Empty;
            Token = token ?? string.Empty;
        }

        public TradeUpdateEventArgs(ClientTradeSnapshot trade, TradeFailureReason failureReason, string failureMessage, string token = "")
        {
            Trade = trade;
            FailureReason = failureReason;
            FailureMessage = failureMessage ?? string.Empty;
            Token = token ?? string.Empty;
        }
    }
}

namespace PhinixClient.Framework
{
    public interface ITradeItemPayloadEncoder
    {
        FrameworkItemPayload[] EncodeTradeItems(IEnumerable<PhinixClient.Trade.TradeItemSnapshot> tradeItems);
    }

    public interface IClientTradeService
    {
        event EventHandler<LogEventArgs> OnLogEntry;
        event EventHandler<PhinixClient.Trade.TradeCreationEventArgs> OnTradeCreationRequested;
        event EventHandler<PhinixClient.Trade.TradeCreationEventArgs> OnTradeCreationSuccess;
        event EventHandler<PhinixClient.Trade.TradeCreationEventArgs> OnTradeCreationFailure;
        event EventHandler<PhinixClient.Trade.TradeCompletionEventArgs> OnTradeCompleted;
        event EventHandler<PhinixClient.Trade.TradeCompletionEventArgs> OnTradeCancelled;
        event EventHandler<PhinixClient.Trade.TradeUpdateEventArgs> OnTradeUpdateSuccess;
        event EventHandler<PhinixClient.Trade.TradeUpdateEventArgs> OnTradeUpdateFailure;
        event EventHandler<PhinixClient.Trade.TradesSyncedEventArgs> OnTradesSynced;

        void CreateTrade(string uuid);

        void CancelTrade(string tradeId);

        string[] GetTradeIds();

        PhinixClient.Trade.ClientTradeSnapshot[] GetTrades();

        PhinixClient.Trade.ClientTradeSnapshot[] GetTradesExceptWith(IEnumerable<string> otherPartyUuids);

        bool TryGetTrade(string tradeId, out PhinixClient.Trade.ClientTradeSnapshot trade);

        bool TryGetOtherPartyUuid(string tradeId, out string otherPartyUuid);

        bool TryGetOtherPartyAccepted(string tradeId, out bool otherPartyAccepted);

        bool TryGetPartyAccepted(string tradeId, string partyUuid, out bool accepted);

        bool TryGetItemsOnOffer(string tradeId, string uuid, out IEnumerable<PhinixClient.Trade.TradeItemSnapshot> items);

        void UpdateTradeItems(string tradeId, IEnumerable<PhinixClient.Trade.TradeItemSnapshot> items, string token = "");

        void UpdateTradeStatus(string tradeId, bool? accepted = null, bool? cancelled = null);
    }

#if NET472
    public interface IFrameworkTradeClientApi
    {
        event EventHandler RepositoryChanged;
        event EventHandler<PhinixClient.Trade.TradeCreationEventArgs> OnTradeCreationSuccess;
        event EventHandler<PhinixClient.Trade.TradeCreationEventArgs> OnTradeCreationFailure;
        event EventHandler<PhinixClient.Trade.TradeUpdateEventArgs> OnTradeUpdateSuccess;
        event EventHandler<PhinixClient.Trade.TradeUpdateEventArgs> OnTradeUpdateFailure;
        event EventHandler<PhinixClient.Trade.TradesSyncedEventArgs> OnTradesSynced;
        event EventHandler<PhinixClient.Trade.TradeCompletionEventArgs> OnTradeCompleted;
        event EventHandler<PhinixClient.Trade.TradeCompletionEventArgs> OnTradeCancelled;

        Phinix.TradeExtension.FrameworkTradeStateSnapshot[] GetRepositoryTrades();
        string[] GetTradeIds();
        PhinixClient.Trade.ClientTradeSnapshot[] GetTrades();
        bool TryGetTrade(string tradeId, out PhinixClient.Trade.ClientTradeSnapshot trade);
        bool TryGetOtherPartyUuid(string tradeId, string localUuid, out string otherPartyUuid);
        bool TryGetOtherPartyAccepted(string tradeId, string localUuid, out bool otherPartyAccepted);
        bool TryGetPartyAccepted(string tradeId, string partyUuid, out bool accepted);
        bool TryGetItemsOnOffer(string tradeId, string partyUuid, out IEnumerable<PhinixClient.Trade.TradeItemSnapshot> items);
        void RequestSnapshot(IFrameworkClientTransport frameworkClient, bool authenticated, bool loggedIn, string sessionId, string senderUuid);
        FrameworkPacket CreateTradeRequest(string otherPartyUuid, ClientFrameworkContext context);
        FrameworkPacket CreateOfferUpdateRequest(string tradeId, IEnumerable<PhinixClient.Trade.TradeItemSnapshot> tradeItems, ClientFrameworkContext context);
        FrameworkPacket CreateStatusUpdateRequest(string tradeId, bool? accepted, bool? cancelled, ClientFrameworkContext context);
        void TrackPendingTradeUpdate(string tradeId, string token);
        void HandleSnapshot(FrameworkPacket packet);
        void HandleCreateResponse(FrameworkPacket packet);
        void HandleOfferUpdateResponse(FrameworkPacket packet);
        void HandleStatusUpdateResponse(FrameworkPacket packet);
        void HandleCompletedEvent(FrameworkPacket packet);
        void HandleCancelledEvent(FrameworkPacket packet);

        /// <summary>
        /// Legacy 适配器专用：将旧版服务器发来的 trade 快照注入 repository。
        /// FrameworkV2 模式不应调用此方法 —— repository 由 HandleSnapshot 维护。
        /// </summary>
        void UpsertTrade(FrameworkTradeStateSnapshot snapshot);

        /// <summary>
        /// Legacy 适配器专用：从 repository 移除已完成的 trade。
        /// FrameworkV2 模式不应调用此方法 —— repository 由 HandleCompletedEvent/HandleCancelledEvent 维护。
        /// </summary>
        void RemoveTrade(string tradeId);

    }

    public interface IFrameworkLegacyTradeCompletionApi
    {
        /// <summary>
        /// Legacy 适配器专用：注入旧版服务器发来的交易完成/取消事件。
        /// FrameworkV2 模式不应调用此方法 —— 完成事件由 HandleCompletedEvent/HandleCancelledEvent 维护。
        /// </summary>
        void CompleteTrade(string tradeId, bool success, string otherPartyUuid, IEnumerable<PhinixClient.Trade.TradeItemSnapshot> items);
    }

    public interface ITradeUiHostContext
    {
        IClientTradeService TradeService { get; }

        bool AllItemsTradable { get; }

        event EventHandler OnDisconnect;

        event EventHandler<UserDisplayNameChangedEventArgs> OnUserDisplayNameChanged;

        LookTargets DropPods(IEnumerable<Thing> verseThings);

        void OpenTradeWindow(PhinixClient.Trade.ClientTradeSnapshot trade);

        void Log(LogEventArgs args);
    }
#endif
}
