using System.Collections.Generic;
using System.Runtime.Serialization;
using Utils.Framework;

namespace Phinix.TradeExtension
{
    public static class FrameworkTradeProtocol
    {
        public const string Capability = "builtin.trade";
        public const string CreateRequestType = "trade.create.request";
        public const string CreateResponseType = "trade.create.response";
        public const string SnapshotType = "trade.state.snapshot";
        public const string OfferUpdateRequestType = "trade.offer.update.request";
        public const string OfferUpdateResponseType = "trade.offer.update.response";
        public const string StatusUpdateRequestType = "trade.status.update.request";
        public const string StatusUpdateResponseType = "trade.status.update.response";
        public const string CompletedEventType = "trade.completed.event";
        public const string CancelledEventType = "trade.cancelled.event";
        public const string StateKindTradeSnapshot = "trade.snapshot";
    }

    [DataContract]
    public enum FrameworkTradeFailureReason
    {
        [EnumMember]
        None = 0,
        [EnumMember]
        SessionInvalid = 1,
        [EnumMember]
        LoginInvalid = 2,
        [EnumMember]
        OtherPartyDoesNotExist = 3,
        [EnumMember]
        OtherPartyOffline = 4,
        [EnumMember]
        NotAcceptingTrades = 5,
        [EnumMember]
        AlreadyTrading = 6,
        [EnumMember]
        TradeDoesNotExist = 7,
        [EnumMember]
        NotTradeParticipant = 8,
        [EnumMember]
        InternalServerError = 9
    }

    [DataContract]
    public sealed class FrameworkTradeCreateRequest
    {
        [DataMember(Order = 0)]
        public string OtherPartyUuid { get; set; }
    }

    [DataContract]
    public sealed class FrameworkTradeCreateResponse
    {
        [DataMember(Order = 0)]
        public bool Success { get; set; }

        [DataMember(Order = 1)]
        public string TradeId { get; set; }

        [DataMember(Order = 2)]
        public string OtherPartyUuid { get; set; }

        [DataMember(Order = 3)]
        public FrameworkTradeFailureReason FailureReason { get; set; } = FrameworkTradeFailureReason.None;

        [DataMember(Order = 4)]
        public string FailureMessage { get; set; }
    }

    [DataContract]
    public sealed class FrameworkTradeOfferUpdateRequest
    {
        [DataMember(Order = 0)]
        public string TradeId { get; set; }

        [DataMember(Order = 1)]
        public List<FrameworkItemPayload> Items { get; set; } = new List<FrameworkItemPayload>();
    }

    [DataContract]
    public sealed class FrameworkTradeOfferUpdateResponse
    {
        [DataMember(Order = 0)]
        public bool Success { get; set; }

        [DataMember(Order = 1)]
        public string TradeId { get; set; }

        [DataMember(Order = 2)]
        public List<FrameworkItemPayload> Items { get; set; } = new List<FrameworkItemPayload>();

        [DataMember(Order = 3)]
        public FrameworkTradeFailureReason FailureReason { get; set; } = FrameworkTradeFailureReason.None;

        [DataMember(Order = 4)]
        public string FailureMessage { get; set; }
    }

    [DataContract]
    public sealed class FrameworkTradeStatusUpdateRequest
    {
        [DataMember(Order = 0)]
        public string TradeId { get; set; }

        [DataMember(Order = 1, EmitDefaultValue = false)]
        public bool? Accepted { get; set; }

        [DataMember(Order = 2, EmitDefaultValue = false)]
        public bool? Cancelled { get; set; }
    }

    [DataContract]
    public sealed class FrameworkTradeStatusUpdateResponse
    {
        [DataMember(Order = 0)]
        public bool Success { get; set; }

        [DataMember(Order = 1)]
        public string TradeId { get; set; }

        [DataMember(Order = 2)]
        public FrameworkTradeFailureReason FailureReason { get; set; } = FrameworkTradeFailureReason.None;

        [DataMember(Order = 3)]
        public string FailureMessage { get; set; }
    }

    [DataContract]
    public sealed class FrameworkTradeParticipantSnapshot
    {
        [DataMember(Order = 0)]
        public string Uuid { get; set; }

        [DataMember(Order = 1)]
        public bool Accepted { get; set; }

        [DataMember(Order = 2)]
        public List<FrameworkItemPayload> ItemsOnOffer { get; set; } = new List<FrameworkItemPayload>();
    }

    [DataContract]
    public sealed class FrameworkTradeStateSnapshot
    {
        [DataMember(Order = 0)]
        public string TradeId { get; set; }

        [DataMember(Order = 1)]
        public List<FrameworkTradeParticipantSnapshot> Participants { get; set; } = new List<FrameworkTradeParticipantSnapshot>();

        [DataMember(Order = 2)]
        public long SnapshotVersion { get; set; }
    }

    [DataContract]
    public sealed class FrameworkTradeStateCollectionSnapshot
    {
        [DataMember(Order = 0)]
        public List<FrameworkTradeStateSnapshot> Trades { get; set; } = new List<FrameworkTradeStateSnapshot>();
    }

    [DataContract]
    public sealed class FrameworkTradeCompletionEvent
    {
        [DataMember(Order = 0)]
        public string TradeId { get; set; }

        [DataMember(Order = 1)]
        public string OtherPartyUuid { get; set; }

        [DataMember(Order = 2)]
        public List<FrameworkItemPayload> Items { get; set; } = new List<FrameworkItemPayload>();

        [DataMember(Order = 3)]
        public bool Cancelled { get; set; }
    }
}
