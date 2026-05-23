using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Utils.Framework
{
    public static class FrameworkSerialization
    {
        public static byte[] SerializePacket(FrameworkPacket packet)
        {
            return Serialize(packet);
        }

        public static FrameworkPacket DeserializePacket(byte[] data)
        {
            return Deserialize<FrameworkPacket>(data);
        }

        public static string SerializePayload<T>(T payload)
        {
            return Encoding.UTF8.GetString(Serialize(payload));
        }

        public static T DeserializePayload<T>(string json)
        {
            return Deserialize<T>(Encoding.UTF8.GetBytes(json ?? ""));
        }

        public static byte[] SerializeItemData(global::Phinix.Framework.FrameworkVanillaItemData itemData)
        {
            return itemData?.ToByteArray() ?? System.Array.Empty<byte>();
        }

        public static global::Phinix.Framework.FrameworkVanillaItemData DeserializeItemData(byte[] payloadBytes)
        {
            return payloadBytes == null || payloadBytes.Length == 0
                ? null
                : global::Phinix.Framework.FrameworkVanillaItemData.Parser.ParseFrom(payloadBytes);
        }

        public static global::Phinix.Framework.FrameworkItemPacket ToItemPacket(FrameworkItemPayload payload, string packetId = null)
        {
            if (payload == null) return null;

            global::Phinix.Framework.FrameworkItemPacket itemPacket = new global::Phinix.Framework.FrameworkItemPacket
            {
                CodecId = payload.CodecId ?? string.Empty,
                PayloadBytes = ByteString.CopyFrom(payload.PayloadBytes ?? System.Array.Empty<byte>())
            };

            itemPacket.Header = new global::Phinix.Framework.FrameworkHeader
            {
                FrameworkVersion = (uint) FrameworkProtocol.Version,
                Flow = global::Phinix.Framework.FrameworkFlow.Item,
                PacketId = packetId ?? string.Empty,
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow)
            };

            foreach (FrameworkMetadataEntry entry in payload.Metadata ?? new System.Collections.Generic.List<FrameworkMetadataEntry>())
            {
                itemPacket.Header.Metadata.Add(new global::Phinix.Framework.FrameworkMetadataEntry
                {
                    Key = entry?.Key ?? string.Empty,
                    Value = entry?.Value ?? string.Empty
                });
            }

            return itemPacket;
        }

        public static FrameworkItemPayload FromItemPacket(global::Phinix.Framework.FrameworkItemPacket itemPacket)
        {
            if (itemPacket == null) return null;

            return new FrameworkItemPayload
            {
                CodecId = itemPacket.CodecId,
                PayloadBytes = itemPacket.PayloadBytes?.ToByteArray() ?? System.Array.Empty<byte>(),
                Metadata = itemPacket.Header?.Metadata?
                    .Select(entry => new FrameworkMetadataEntry
                    {
                        Key = entry.Key,
                        Value = entry.Value
                    })
                    .ToList() ?? new System.Collections.Generic.List<FrameworkMetadataEntry>()
            };
        }

        private static byte[] Serialize<T>(T value)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
            using (MemoryStream ms = new MemoryStream())
            {
                serializer.WriteObject(ms, value);
                return ms.ToArray();
            }
        }

        private static T Deserialize<T>(byte[] data)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
            using (MemoryStream ms = new MemoryStream(data))
            {
                return (T) serializer.ReadObject(ms);
            }
        }
    }
}
