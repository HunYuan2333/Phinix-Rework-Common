using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Utils.Framework
{
    [DataContract]
    public sealed class FrameworkMetadataEntry
    {
        [DataMember(Order = 0)]
        public string Key { get; set; }

        [DataMember(Order = 1)]
        public string Value { get; set; }
    }

    [DataContract]
    public sealed class FrameworkEnvelope
    {
        [DataMember(Order = 0)]
        public int Version { get; set; } = FrameworkProtocol.Version;

        [DataMember(Order = 1)]
        public string Kind { get; set; }

        [DataMember(Order = 2)]
        public string MessageType { get; set; }

        [DataMember(Order = 3)]
        public string MessageId { get; set; } = Guid.NewGuid().ToString();

        [DataMember(Order = 4)]
        public string SessionId { get; set; }

        [DataMember(Order = 5)]
        public string SenderUuid { get; set; }

        [DataMember(Order = 6)]
        public long TimestampUtcTicks { get; set; } = DateTime.UtcNow.Ticks;

        [DataMember(Order = 7)]
        public string PayloadJson { get; set; }

        [DataMember(Order = 8)]
        public List<FrameworkMetadataEntry> Metadata { get; set; } = new List<FrameworkMetadataEntry>();
    }

    [DataContract]
    public sealed class FrameworkCapabilitiesPayload
    {
        [DataMember(Order = 0)]
        public List<string> Capabilities { get; set; } = new List<string>();
    }

    [DataContract]
    public sealed class FrameworkDisplayMessage
    {
        [DataMember(Order = 0)]
        public string MessageId { get; set; } = Guid.NewGuid().ToString();

        [DataMember(Order = 1)]
        public string SenderUuid { get; set; }

        [DataMember(Order = 2)]
        public string Text { get; set; }

        [DataMember(Order = 3)]
        public long TimestampUtcTicks { get; set; } = DateTime.UtcNow.Ticks;

        [DataMember(Order = 4)]
        public string Source { get; set; } = "framework";

        [DataMember(Order = 5)]
        public bool SuppressDefaultDisplay { get; set; }

        [DataMember(Order = 6)]
        public string TranslationKey { get; set; }

        [DataMember(Order = 7)]
        public List<string> TranslationArgs { get; set; } = new List<string>();
    }

    [DataContract]
    public sealed class RedPacketPayload
    {
        [DataMember(Order = 0)]
        public string Body { get; set; }
    }

    [DataContract]
    public sealed class FrameworkItemPayload
    {
        [DataMember(Order = 0)]
        public string CodecId { get; set; }

        [DataMember(Order = 1)]
        public string PayloadJson { get; set; }

        [DataMember(Order = 2)]
        public List<FrameworkMetadataEntry> Metadata { get; set; } = new List<FrameworkMetadataEntry>();
    }
}
