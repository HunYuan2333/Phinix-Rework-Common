using System;
using System.Collections.Generic;
using PhinixClient.Trade;
using Utils.Framework;

namespace Phinix.TradeExtension.Client
{
    internal static class FrameworkTradeItemPayloadCodec
    {
        public static bool TryDecodeToTradeItemSnapshot(FrameworkItemPayload payload, out TradeItemSnapshot item)
        {
            item = null;
            if (payload == null ||
                !string.Equals(payload.CodecId, "core.item.vanilla", StringComparison.OrdinalIgnoreCase) ||
                ((payload.PayloadBytes == null || payload.PayloadBytes.Length == 0) && string.IsNullOrEmpty(payload.PayloadJson)))
            {
                return false;
            }

            item = DecodeToTradeItemSnapshot(payload);
            return item != null;
        }

        public static TradeItemSnapshot CreateUnknownTradeItemSnapshot(string label)
        {
            return new TradeItemSnapshot(label ?? "UnknownItem", 1, 1, TradeItemQuality.None);
        }

        private static TradeItemSnapshot DecodeToTradeItemSnapshot(FrameworkItemPayload payload)
        {
            if (payload.PayloadBytes != null && payload.PayloadBytes.Length > 0)
            {
                global::Phinix.Framework.FrameworkVanillaItemData deserializedPayload = FrameworkSerialization.DeserializeItemData(payload.PayloadBytes);
                return FromPayload(deserializedPayload);
            }

            LegacyProtoThingPayload legacyPayload = FrameworkSerialization.DeserializePayload<LegacyProtoThingPayload>(payload.PayloadJson);
            return FromLegacyPayload(legacyPayload);
        }

        private static TradeItemSnapshot FromPayload(global::Phinix.Framework.FrameworkVanillaItemData payload)
        {
            if (payload == null) return null;

            return new TradeItemSnapshot(
                payload.DefName,
                payload.StackCount,
                payload.HitPoints,
                FromFrameworkQuality(payload.Quality),
                payload.StuffDefName,
                FromPayload(payload.InnerItem));
        }

        private static TradeItemSnapshot FromLegacyPayload(LegacyProtoThingPayload payload)
        {
            if (payload == null) return null;

            return new TradeItemSnapshot(
                payload.DefName,
                payload.StackCount,
                payload.HitPoints,
                payload.Quality,
                payload.StuffDefName,
                FromLegacyPayload(payload.InnerProtoThing));
        }

        private static TradeItemQuality FromFrameworkQuality(global::Phinix.Framework.FrameworkItemQuality quality)
        {
            switch (quality)
            {
                case global::Phinix.Framework.FrameworkItemQuality.Awful: return TradeItemQuality.Awful;
                case global::Phinix.Framework.FrameworkItemQuality.Poor: return TradeItemQuality.Poor;
                case global::Phinix.Framework.FrameworkItemQuality.Normal: return TradeItemQuality.Normal;
                case global::Phinix.Framework.FrameworkItemQuality.Good: return TradeItemQuality.Good;
                case global::Phinix.Framework.FrameworkItemQuality.Excellent: return TradeItemQuality.Excellent;
                case global::Phinix.Framework.FrameworkItemQuality.Masterwork: return TradeItemQuality.Masterwork;
                case global::Phinix.Framework.FrameworkItemQuality.Legendary: return TradeItemQuality.Legendary;
                case global::Phinix.Framework.FrameworkItemQuality.None: return TradeItemQuality.None;
                default: return TradeItemQuality.None;
            }
        }

        [System.Runtime.Serialization.DataContract]
        private sealed class LegacyProtoThingPayload
        {
            [System.Runtime.Serialization.DataMember(Order = 0)]
            public string DefName { get; set; }

            [System.Runtime.Serialization.DataMember(Order = 1)]
            public int StackCount { get; set; }

            [System.Runtime.Serialization.DataMember(Order = 2)]
            public int HitPoints { get; set; }

            [System.Runtime.Serialization.DataMember(Order = 3)]
            public TradeItemQuality Quality { get; set; }

            [System.Runtime.Serialization.DataMember(Order = 4)]
            public string StuffDefName { get; set; }

            [System.Runtime.Serialization.DataMember(Order = 5)]
            public LegacyProtoThingPayload InnerProtoThing { get; set; }
        }
    }
}
