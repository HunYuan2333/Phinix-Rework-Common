using System;
using System.IO;
using System.Security.Cryptography;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Connections
{

    // Layout and offsets are checked against both reviewed backend binaries before startup.
    public sealed class PinnedUdpReceiveBudget
    {
        internal const int MaxFragments = 4096;
        private const int MaxQueuedEvents = 512;
        private const long QueueBytes = 32L * 1024 * 1024;
        private const long PeerFragmentBytes = 48L * 1024 * 1024;
        private const long GlobalFragmentBytes = 96L * 1024 * 1024;
        private const int Merged = 0x13;
        private const int FragmentBit = 0x80;

        private readonly NetManager manager;
        private readonly ICollection queue;
        private readonly FieldInfo reader, fragments, slots, ordered, buffered, rawData, packetSize;
        private readonly int mtu;
        private readonly List<NetPeer> peers = new List<NetPeer>();
        private readonly Dictionary<ushort, int> incomingGroups = new Dictionary<ushort, int>();
        private readonly Dictionary<ushort, int> peerGroups = new Dictionary<ushort, int>();

        public PinnedUdpReceiveBudget(NetManager manager)
        {
            ValidateBackend();
            this.manager = manager;
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            queue = (ICollection)RequireField(typeof(NetManager), "_netEventsQueue", fields).GetValue(manager);
            reader = RequireField(queue.GetType().GetGenericArguments()[0], "DataReader", fields);
            fragments = RequireField(typeof(NetPeer), "_holdedFragments", fields);
            slots = RequireField(fragments.FieldType.GetGenericArguments()[1], "Fragments", fields);
            ordered = RequireField(typeof(NetPeer), "_reliableOrderedChannel", fields);
            buffered = RequireField(ordered.FieldType, "_receivedPackets", fields);
            var packetType = buffered.FieldType.GetElementType();
            rawData = RequireField(packetType, "RawData", fields);
            packetSize = RequireField(packetType, "Size", fields);
            var constants = typeof(NetManager).Assembly.GetType("LiteNetLib.NetConstants", throwOnError: true);
            mtu = (int)RequireField(constants, "MaxPacketSize", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            if (mtu < 512 || mtu > 65535) throw new NotSupportedException("Unexpected UDP datagram size.");
        }

        private static FieldInfo RequireField(Type type, string name, BindingFlags flags) =>
            type.GetField(name, flags) ?? throw new NotSupportedException("UDP receive budget layout changed.");

        // The backend queues reliable packets and acknowledges them. Drop before it sees the
        // datagram so a dropped application payload is never acknowledged by the transport.
        public bool CanReceive(byte[] datagram, int count, NetEndPoint endpoint)
        {
            if (datagram == null || count < 1 || count > datagram.Length) return false;
            incomingGroups.Clear();
            if (count > mtu || !TryInspect(datagram, count, incomingGroups)) return false;
            lock (queue)
            {
                if (queue.Count >= MaxQueuedEvents) return false;
                long bytes = 0;
                foreach (object evt in queue)
                {
                    if (reader.GetValue(evt) is NetDataReader payload) bytes += payload.AvailableBytes;
                    if (bytes >= QueueBytes) return false;
                }
            }

            if (incomingGroups.Count == 0) return true;
            long globalBytes = 0;
            bool knownPeer = false;
            manager.GetPeersNonAlloc(peers);
            try
            {
                foreach (NetPeer peer in peers)
                {
                    peerGroups.Clear();
                    foreach (DictionaryEntry group in (IDictionary)fragments.GetValue(peer))
                        Reserve(peerGroups, (ushort)group.Key, ((Array)slots.GetValue(group.Value)).Length);
                    // Ordered packets can become fragments when a gap closes. Count their
                    // reservations while still buffered, once per fragment ID.
                    foreach (object packet in (Array)buffered.GetValue(ordered.GetValue(peer)))
                    {
                        if (packet == null) continue;
                        var data = (byte[])rawData.GetValue(packet);
                        int length = (int)packetSize.GetValue(packet);
                        if (!InspectLeaf(data, 0, length, peerGroups)) return false;
                    }

                    if (Equals(peer.EndPoint, endpoint))
                    {
                        knownPeer = true;
                        foreach (var group in incomingGroups) Reserve(peerGroups, group.Key, group.Value);
                    }

                    long peerBytes = 0;
                    foreach (int fragmentsCount in peerGroups.Values) peerBytes += fragmentsCount * (mtu + 32L);
                    globalBytes += peerBytes;
                    if (peerBytes > PeerFragmentBytes || globalBytes > GlobalFragmentBytes) return false;
                }
            }
            finally { peers.Clear(); }
            return knownPeer;
        }

        private static bool TryInspect(byte[] datagram, int count, Dictionary<ushort, int> reservations)
        {
            if (count < 1) return false;
            if ((datagram[0] & 0x7f) != Merged) return InspectLeaf(datagram, 0, count, reservations);
            if (datagram[0] != Merged || count < 4) return false;
            int offset = 1;
            while (offset < count)
            {
                if (count - offset < 2) return false;
                int length = ReadUInt16(datagram, offset);
                offset += 2;
                if (length == 0 || length > count - offset || !InspectLeaf(datagram, offset, length, reservations)) return false;
                offset += length;
            }
            return true;
        }

        private static bool InspectLeaf(byte[] packet, int offset, int count, Dictionary<ushort, int> reservations)
        {
            if (count < 1 || offset < 0 || offset > packet.Length - count) return false;
            int kind = packet[offset] & 0x7f;
            if (kind >= Merged) return false;
            if ((packet[offset] & FragmentBit) == 0) return true;
            if ((kind != 1 && kind != 3) || count <= 9) return false;
            int part = ReadUInt16(packet, offset + 5);
            int total = ReadUInt16(packet, offset + 7);
            if (total < 2 || total > MaxFragments || part >= total) return false;
            Reserve(reservations, (ushort)ReadUInt16(packet, offset + 3), total);
            return true;
        }

        private static int ReadUInt16(byte[] data, int offset) => data[offset] | data[offset + 1] << 8;

        public static void ValidateBackend()
        {
            string location = typeof(NetManager).Assembly.Location;
            string hash;
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(location))).Replace("-", "").ToLowerInvariant();
            if (hash != "320526331c04be0218341e68412b51df5752aa5381842c7c9d26e414c217eca0" &&
                hash != "c4ad697e3032a221892d894f1f225738a819b4c061b3de7f8ac4437b01b9e0c8")
                throw new NotSupportedException("UDP backend differs from the reviewed receive-budget pins.");
        }

        private static void Reserve(Dictionary<ushort, int> groups, ushort id, int count)
        {
            if (!groups.TryGetValue(id, out int reserved) || count > reserved) groups[id] = count;
        }

    }
}
