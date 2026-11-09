using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Utils.Framework
{
    // Game-independent connection state. The host supplies authenticated transport and calls Pulse.
    public sealed class ClientCommandRelay : IClientCommandRelay, IDisposable
    {
        private sealed class Pending
        {
            public DateTime Deadline;
            public TaskCompletionSource<RelayResult> Completion;
        }
        private readonly object gate = new object();
        private readonly ClientCommandCapabilities capabilities;
        private readonly Func<FrameworkPacket, bool> send;
        private readonly Func<DateTime> now;
        private readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        private string epoch;
        private long acknowledged, sentRevision;
        private DateTime nextSync;
        private int attempts;
        private bool disposed;
        private CapabilitySyncState syncState;
        private string syncError;
        public string SyncError { get { lock (gate) return syncError; } }
        public CapabilitySyncState SyncState
        {
            get { lock (gate) return syncState == CapabilitySyncState.Synced && acknowledged != capabilities.Revision
                ? CapabilitySyncState.Pending : syncState; }
        }

        public ClientCommandRelay(ClientCommandCapabilities capabilities, Func<FrameworkPacket, bool> send, Func<DateTime> now = null)
        {
            this.capabilities = capabilities;
            this.send = send;
            this.now = now ?? (() => DateTime.UtcNow);
        }
        public void Begin(string epoch)
        {
            lock (gate)
            {
                if (disposed || string.IsNullOrEmpty(epoch) || epoch.Length > 128) return;
                if (this.epoch == epoch) return;
                Disconnect();
                this.epoch = epoch;
                syncState = CapabilitySyncState.Pending;
                nextSync = DateTime.MinValue;
                Pulse();
            }
        }
        public void Pulse()
        {
            lock (gate)
            {
                if (disposed) return;
                foreach (var item in pending.Where(p => p.Value.Deadline <= now()).ToArray())
                {
                    pending.Remove(item.Key);
                    item.Value.Completion.TrySetResult(RelayResult.Failure(item.Key, "Unknown"));
                }
                if (epoch == null || acknowledged == capabilities.Revision || now() < nextSync) return;
                var snapshot = capabilities.Capture(epoch);
                if (sentRevision != snapshot.Revision) attempts = 0;
                sentRevision = snapshot.Revision;
                attempts++;
                syncState = attempts > 3 ? CapabilitySyncState.Failed : CapabilitySyncState.Pending;
                nextSync = now().AddSeconds(Math.Min(10, attempts));
                if (!TrySend(new FrameworkPacket { Kind = CommandRelayProtocol.SnapshotKind,
                    PayloadJson = FrameworkSerialization.SerializePayload(snapshot) })) syncError = "NotSent";
                else if (attempts > 3) syncError = "AckTimeout";
            }
        }
        private bool TrySend(FrameworkPacket packet)
        {
            try { return send(packet); } catch { return false; }
        }
        public void Acknowledge(CommandCapabilityAck ack)
        {
            lock (gate)
            {
                if (epoch == null || ack == null || ack.Epoch != epoch || ack.Revision != sentRevision ||
                    ack.Revision <= acknowledged) return;
                if (ack.Error != null) { syncState = CapabilitySyncState.Failed; syncError = ack.Error; return; }
                syncError = null;
                acknowledged = ack.Revision;
                syncState = acknowledged == capabilities.Revision ? CapabilitySyncState.Synced : CapabilitySyncState.Pending;
                attempts = 0;
            }
        }
        public Task<RelayResult> SendRelay(string pluginId, string commandType, string payloadJson, string targetPlayerUuid = null, int version = 1)
        {
            var packet = new FrameworkPacket { Kind = FrameworkProtocol.KindCommand,
                Flow = global::Phinix.Framework.FrameworkFlow.Command, MessageType = commandType, PayloadJson = payloadJson,
                CommandRoute = new CommandRoute { Mode = CommandProcessingMode.Relay, PluginId = pluginId,
                    CommandType = commandType, Version = version, TargetPlayerUuid = targetPlayerUuid } };
            lock (gate)
            {
                string failure = CommandRelayProtocol.Validate(packet);
                if (failure == null && (disposed || epoch == null)) failure = "UnsupportedRelay";
                if (failure == null && acknowledged == 0) failure = "CapabilitiesNotSynced";
                if (failure == null && pending.Count >= 64) failure = "RateLimited";
                if (failure != null) return Task.FromResult(RelayResult.Failure(packet.MessageId, failure));
                var completion = new TaskCompletionSource<RelayResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Add(packet.MessageId, new Pending { Deadline = now().AddSeconds(15), Completion = completion });
                string sendError = null;
                try { if (!send(packet)) sendError = "NotSent"; }
                catch { sendError = "Unknown"; }
                if (sendError != null)
                {
                    pending.Remove(packet.MessageId);
                    completion.TrySetResult(RelayResult.Failure(packet.MessageId, sendError));
                }
                return completion.Task;
            }
        }
        public void Complete(RelayResult result)
        {
            lock (gate)
            {
                if (result?.RequestMessageId == null || result.Deliveries == null || result.Deliveries.Count > 128) return;
                if (pending.TryGetValue(result.RequestMessageId, out var request))
                {
                    pending.Remove(result.RequestMessageId);
                    request.Completion.TrySetResult(result);
                }
            }
        }
        public void Disconnect()
        {
            lock (gate)
            {
                epoch = null;
                syncError = null;
                acknowledged = sentRevision = 0;
                attempts = 0;
                syncState = CapabilitySyncState.Disconnected;
                foreach (var item in pending) item.Value.Completion.TrySetResult(RelayResult.Failure(item.Key, "Unknown"));
                pending.Clear();
            }
        }
        public void Dispose() { lock (gate) { Disconnect(); disposed = true; } }
    }
}
