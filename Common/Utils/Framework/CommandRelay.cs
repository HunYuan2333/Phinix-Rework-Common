using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Utils.Framework
{
    public enum CommandProcessingMode { Server = 0, Relay = 1 }

    [DataContract]
    public sealed class CommandRoute
    {
        [DataMember(Order = 0)] public CommandProcessingMode Mode { get; set; }
        [DataMember(Order = 1)] public string PluginId { get; set; }
        [DataMember(Order = 2)] public string CommandType { get; set; }
        [DataMember(Order = 3)] public int Version { get; set; }
        // Null is capability broadcast. A player target includes every eligible connection.
        [DataMember(Order = 4)] public string TargetPlayerUuid { get; set; }
    }

    [DataContract]
    public sealed class CommandCapability
    {
        [DataMember(Order = 0)] public string PluginId { get; set; }
        [DataMember(Order = 1)] public string CommandType { get; set; }
        [DataMember(Order = 2)] public int Version { get; set; }
        public string Key => PluginId + "/" + CommandType + "/" + Version;
    }

    [DataContract]
    public sealed class CommandCapabilitySnapshot
    {
        [DataMember(Order = 0)] public string Epoch { get; set; }
        [DataMember(Order = 1)] public long Revision { get; set; }
        [DataMember(Order = 2)] public List<CommandCapability> Capabilities { get; set; }
    }

    [DataContract]
    public sealed class CommandCapabilityAck
    {
        [DataMember(Order = 0)] public string Epoch { get; set; }
        [DataMember(Order = 1)] public long Revision { get; set; }
        [DataMember(Order = 2)] public string Error { get; set; }
    }

    [DataContract]
    public sealed class RelayDelivery
    {
        [DataMember(Order = 0)] public string ConnectionId { get; set; }
        [DataMember(Order = 1)] public string Outcome { get; set; }
    }

    [DataContract]
    public sealed class RelayResult
    {
        [DataMember(Order = 0)] public string RequestMessageId { get; set; }
        [DataMember(Order = 1)] public string Error { get; set; }
        [DataMember(Order = 3)] public string Status { get; set; }
        [DataMember(Order = 2)] public List<RelayDelivery> Deliveries { get; set; } = new List<RelayDelivery>();
        public static RelayResult Failure(string id, string error) => new RelayResult { RequestMessageId = id, Error = error, Status = error == "Unknown" ? "Unknown" : "Rejected" };
    }

    public static class CommandRelayProtocol
    {
        public const string Capability = "core.command-relay.v1";
        public const string SnapshotKind = "command-capabilities";
        public const string AckKind = "command-capabilities-ack";
        public const string ResultKind = "command-relay-result";
        public const string EpochMetadata = "relay_epoch";
        public const int MaxCapabilities = 256;
        public const int MaxPacketBytes = 64 * 1024;
        private static readonly Regex Identifier = new Regex(@"^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
        private static readonly Regex PlayerIdentifier = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,255}$", RegexOptions.CultureInvariant);
        public static bool IsIdentifier(string value) => value != null && Identifier.IsMatch(value);
        public static bool IsCapability(CommandCapability value) => value != null && IsIdentifier(value.PluginId) &&
            IsIdentifier(value.CommandType) && value.Version > 0 && !IsReserved(value.PluginId);
        public static bool IsReserved(string value) => value == "core" || value == "builtin" || value == "framework" ||
            value.StartsWith("core.", StringComparison.Ordinal) || value.StartsWith("builtin.", StringComparison.Ordinal) ||
            value.StartsWith("framework.", StringComparison.Ordinal);
        public static string Validate(FrameworkPacket packet)
        {
            var route = packet?.CommandRoute;
            if (route == null || route.Mode != CommandProcessingMode.Relay || packet.Kind != FrameworkProtocol.KindCommand ||
                packet.Flow != global::Phinix.Framework.FrameworkFlow.Command ||
                !IsCapability(new CommandCapability { PluginId = route.PluginId, CommandType = route.CommandType, Version = route.Version }) ||
                packet.MessageType != route.CommandType || string.IsNullOrEmpty(packet.MessageId) || packet.MessageId.Length > 128 ||
                (route.TargetPlayerUuid != null && !PlayerIdentifier.IsMatch(route.TargetPlayerUuid)))
                return "InvalidCommand";
            try { return FrameworkSerialization.SerializePacket(packet).Length > MaxPacketBytes ? "PayloadTooLarge" : null; }
            catch { return "InvalidCommand"; }
        }
    }

    public interface IClientCommandCapabilities
    {
        IDisposable Declare(string ownerId, string pluginId, string commandType, int version = 1);
        void RevokeOwner(string ownerId);
        IReadOnlyList<CommandCapability> GetSnapshot();
    }

    public enum CapabilitySyncState { Disconnected, Pending, Synced, Failed }

    public interface IClientCommandRelay
    {
        CapabilitySyncState SyncState { get; }
        string SyncError { get; }
        Task<RelayResult> SendRelay(string pluginId, string commandType, string payloadJson,
            string targetPlayerUuid = null, int version = 1);
    }

    public sealed class ClientCommandCapabilities : IClientCommandCapabilities
    {
        private sealed class Declaration : IDisposable
        {
            private ClientCommandCapabilities registry;
            internal string Owner;
            internal CommandCapability Capability;
            public Declaration(ClientCommandCapabilities registry) { this.registry = registry; }
            public void Dispose() { Interlocked.Exchange(ref registry, null)?.Remove(this); }
        }
        private readonly object gate = new object();
        private readonly List<Declaration> declarations = new List<Declaration>();
        private long revision = 1;
        public long Revision { get { lock (gate) return revision; } }
        public IDisposable Declare(string ownerId, string pluginId, string commandType, int version = 1)
        {
            var capability = new CommandCapability { PluginId = pluginId, CommandType = commandType, Version = version };
            if (string.IsNullOrWhiteSpace(ownerId) || !CommandRelayProtocol.IsCapability(capability))
                throw new ArgumentException("Invalid capability declaration.");
            lock (gate)
            {
                if (declarations.Count >= 4096 || (!declarations.Any(d => d.Capability.Key == capability.Key) &&
                    declarations.Select(d => d.Capability.Key).Distinct().Count() >= CommandRelayProtocol.MaxCapabilities))
                    throw new InvalidOperationException("Capability declaration limit exceeded.");
                var declaration = new Declaration(this) { Owner = ownerId, Capability = capability };
                declarations.Add(declaration);
                if (FrameworkSerialization.SerializePayload(Capture(new string('e', 128))).Length > 60 * 1024)
                {
                    declarations.Remove(declaration);
                    throw new InvalidOperationException("Capability snapshot size limit exceeded.");
                }
                revision++;
                return declaration;
            }
        }
        private void Remove(Declaration declaration) { lock (gate) if (declarations.Remove(declaration)) revision++; }
        public void RevokeOwner(string ownerId)
        {
            lock (gate) if (declarations.RemoveAll(d => d.Owner == ownerId) > 0) revision++;
        }
        public IReadOnlyList<CommandCapability> GetSnapshot()
        {
            lock (gate) return declarations.Select(d => d.Capability).GroupBy(c => c.Key).Select(g =>
                new CommandCapability { PluginId = g.First().PluginId, CommandType = g.First().CommandType, Version = g.First().Version })
                .OrderBy(c => c.Key, StringComparer.Ordinal).ToArray();
        }
        public CommandCapabilitySnapshot Capture(string epoch)
        {
            lock (gate) return new CommandCapabilitySnapshot { Epoch = epoch, Revision = revision, Capabilities = GetSnapshot().ToList() };
        }
    }
}
