using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

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
