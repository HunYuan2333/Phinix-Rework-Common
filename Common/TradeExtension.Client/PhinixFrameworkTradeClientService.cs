using System;
using System.Collections.Generic;
using System.Linq;
using PhinixClient.Framework;
using PhinixClient.Trade;
using UserManagement;
using Utils;
using Utils.Framework;

namespace Phinix.TradeExtension.Client
{
    public sealed class PhinixFrameworkTradeClientService : IFrameworkTradeClientApi
    {
        private readonly PhinixFrameworkTradeClientRepository repository;
        private readonly ITradeItemPayloadEncoder itemPipeline;
        private readonly IClientUserDirectory userDirectory;
        private readonly Action<LogEventArgs> log;
        private readonly Dictionary<string, string> pendingTradeCreationByTradeId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> pendingTradeUpdateTokensByTradeId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public event EventHandler RepositoryChanged;
        public event EventHandler<TradeCreationEventArgs> OnTradeCreationSuccess;
        public event EventHandler<TradeCreationEventArgs> OnTradeCreationFailure;
        public event EventHandler<TradeUpdateEventArgs> OnTradeUpdateSuccess;
        public event EventHandler<TradeUpdateEventArgs> OnTradeUpdateFailure;
        public event EventHandler<TradesSyncedEventArgs> OnTradesSynced;
        public event EventHandler<TradeCompletionEventArgs> OnTradeCompleted;
        public event EventHandler<TradeCompletionEventArgs> OnTradeCancelled;

        public PhinixFrameworkTradeClientService(ITradeItemPayloadEncoder itemPipeline, IClientUserDirectory userDirectory, Action<LogEventArgs> log)
        {
            repository = new PhinixFrameworkTradeClientRepository();
            this.itemPipeline = itemPipeline;
            this.userDirectory = userDirectory;
            this.log = log;
        }

        public FrameworkTradeStateSnapshot[] GetRepositoryTrades() => repository.GetAll();

        public string[] GetTradeIds() => repository.GetAll().Select(snapshot => snapshot.TradeId).ToArray();

        public ClientTradeSnapshot[] GetTrades()
        {
            return repository.GetAll()
                .Select(ToTradeSnapshot)
                .Where(trade => trade != null && !string.IsNullOrEmpty(trade.TradeId))
                .ToArray();
        }

        public bool TryGetTrade(string tradeId, out ClientTradeSnapshot trade)
        {
            if (!repository.TryGet(tradeId, out FrameworkTradeStateSnapshot snapshot))
            {
                trade = null;
                return false;
            }

            trade = ToTradeSnapshot(snapshot);
            return trade != null;
        }

        public bool TryGetOtherPartyUuid(string tradeId, string localUuid, out string otherPartyUuid)
        {
            otherPartyUuid = null;
            if (!repository.TryGet(tradeId, out FrameworkTradeStateSnapshot snapshot))
            {
                return false;
            }

            FrameworkTradeParticipantSnapshot otherParty = (snapshot.Participants ?? new List<FrameworkTradeParticipantSnapshot>())
                .SingleOrDefault(participant => participant.Uuid != localUuid);
            if (otherParty == null)
            {
                return false;
            }

            otherPartyUuid = otherParty.Uuid;
            return true;
        }

        public bool TryGetOtherPartyAccepted(string tradeId, string localUuid, out bool otherPartyAccepted)
        {
            otherPartyAccepted = false;
            if (!repository.TryGet(tradeId, out FrameworkTradeStateSnapshot snapshot))
            {
                return false;
            }

            FrameworkTradeParticipantSnapshot otherParty = (snapshot.Participants ?? new List<FrameworkTradeParticipantSnapshot>())
                .SingleOrDefault(participant => participant.Uuid != localUuid);
            if (otherParty == null)
            {
                return false;
            }

            otherPartyAccepted = otherParty.Accepted;
            return true;
        }

        public bool TryGetPartyAccepted(string tradeId, string partyUuid, out bool accepted)
        {
            accepted = false;
            if (!repository.TryGet(tradeId, out FrameworkTradeStateSnapshot snapshot))
            {
                return false;
            }

            FrameworkTradeParticipantSnapshot participant = (snapshot.Participants ?? new List<FrameworkTradeParticipantSnapshot>())
                .SingleOrDefault(candidate => candidate.Uuid == partyUuid);
            if (participant == null)
            {
                return false;
            }

            accepted = participant.Accepted;
            return true;
        }

        public bool TryGetItemsOnOffer(string tradeId, string partyUuid, out IEnumerable<TradeItemSnapshot> items)
        {
            items = Array.Empty<TradeItemSnapshot>();
            if (!repository.TryGet(tradeId, out FrameworkTradeStateSnapshot snapshot))
            {
                return false;
            }

            FrameworkTradeParticipantSnapshot participant = (snapshot.Participants ?? new List<FrameworkTradeParticipantSnapshot>())
                .SingleOrDefault(candidate => candidate.Uuid == partyUuid);
            if (participant == null)
            {
                return false;
            }

            items = DecodeTradeItems(participant.ItemsOnOffer);
            return true;
        }

        public void RequestSnapshot(IFrameworkClientTransport frameworkClient, bool authenticated, bool loggedIn, string sessionId, string senderUuid)
        {
            if (frameworkClient == null || !authenticated || !loggedIn || string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(senderUuid))
            {
                return;
            }

            if (!frameworkClient.HasRemoteCapability(FrameworkTradeProtocol.Capability))
            {
                return;
            }

            FrameworkPacket packet = new FrameworkPacket
            {
                Flow = global::Phinix.Framework.FrameworkFlow.Command,
                CommandKind = global::Phinix.Framework.FrameworkCommandKind.Request,
                MessageType = FrameworkTradeProtocol.SnapshotType,
                SessionId = sessionId,
                SenderUuid = senderUuid,
                PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkTradeStateCollectionSnapshot())
            };
            packet.SetStateKind(FrameworkMetadataStateKinds.Snapshot);
            frameworkClient.SendFrameworkPacket(packet);
        }

        public FrameworkPacket CreateTradeRequest(string otherPartyUuid, ClientFrameworkContext context)
        {
            FrameworkTradeCreateRequest payload = new FrameworkTradeCreateRequest
            {
                OtherPartyUuid = otherPartyUuid
            };

            return new FrameworkPacket
            {
                Flow = global::Phinix.Framework.FrameworkFlow.Command,
                CommandKind = global::Phinix.Framework.FrameworkCommandKind.Request,
                MessageType = FrameworkTradeProtocol.CreateRequestType,
                MessageId = Guid.NewGuid().ToString(),
                SessionId = context.SessionId,
                SenderUuid = context.SenderUuid,
                PayloadJson = FrameworkSerialization.SerializePayload(payload)
            };
        }

        public FrameworkPacket CreateOfferUpdateRequest(string tradeId, IEnumerable<TradeItemSnapshot> tradeItems, ClientFrameworkContext context)
        {
            FrameworkTradeOfferUpdateRequest payload = new FrameworkTradeOfferUpdateRequest
            {
                TradeId = tradeId,
                Items = (itemPipeline.EncodeTradeItems(tradeItems) ?? Array.Empty<FrameworkItemPayload>()).ToList()
            };

            return new FrameworkPacket
            {
                Flow = global::Phinix.Framework.FrameworkFlow.Command,
                CommandKind = global::Phinix.Framework.FrameworkCommandKind.Request,
                MessageType = FrameworkTradeProtocol.OfferUpdateRequestType,
                MessageId = Guid.NewGuid().ToString(),
                SessionId = context.SessionId,
                SenderUuid = context.SenderUuid,
                PayloadJson = FrameworkSerialization.SerializePayload(payload)
            };
        }

        public FrameworkPacket CreateStatusUpdateRequest(string tradeId, bool? accepted, bool? cancelled, ClientFrameworkContext context)
        {
            FrameworkTradeStatusUpdateRequest payload = new FrameworkTradeStatusUpdateRequest
            {
                TradeId = tradeId,
                Accepted = accepted,
                Cancelled = cancelled
            };

            return new FrameworkPacket
            {
                Flow = global::Phinix.Framework.FrameworkFlow.Command,
                CommandKind = global::Phinix.Framework.FrameworkCommandKind.Request,
                MessageType = FrameworkTradeProtocol.StatusUpdateRequestType,
                MessageId = Guid.NewGuid().ToString(),
                SessionId = context.SessionId,
                SenderUuid = context.SenderUuid,
                PayloadJson = FrameworkSerialization.SerializePayload(payload)
            };
        }

        public void TrackPendingTradeUpdate(string tradeId, string token)
        {
            if (string.IsNullOrEmpty(tradeId))
            {
                return;
            }

            if (!pendingTradeUpdateTokensByTradeId.TryGetValue(tradeId, out List<string> tokens))
            {
                tokens = new List<string>();
                pendingTradeUpdateTokensByTradeId[tradeId] = tokens;
            }

            tokens.Add(token ?? string.Empty);
        }

        public void HandleSnapshot(FrameworkPacket packet)
        {
            FrameworkTradeStateCollectionSnapshot payload = FrameworkSerialization.DeserializePayload<FrameworkTradeStateCollectionSnapshot>(packet.PayloadJson);
            bool isFullSnapshot = packet.TryGetStateKind(out string stateKind) &&
                                  string.Equals(stateKind, FrameworkMetadataStateKinds.Snapshot, StringComparison.OrdinalIgnoreCase);

            if (isFullSnapshot)
            {
                repository.ReplaceAll(payload?.Trades);
                foreach (FrameworkTradeStateSnapshot trade in payload?.Trades ?? Enumerable.Empty<FrameworkTradeStateSnapshot>())
                {
                    FlushPendingEventsForTrade(trade.TradeId, false);
                }
            }
            else
            {
                foreach (FrameworkTradeStateSnapshot trade in payload?.Trades ?? Enumerable.Empty<FrameworkTradeStateSnapshot>())
                {
                    repository.Upsert(trade);
                    bool emittedPendingEvent = FlushPendingEventsForTrade(trade.TradeId, true);
                    if (!emittedPendingEvent && TryGetTrade(trade.TradeId, out ClientTradeSnapshot updatedTrade))
                    {
                        OnTradeUpdateSuccess?.Invoke(this, new TradeUpdateEventArgs(updatedTrade));
                    }
                }
            }

            RepositoryChanged?.Invoke(this, EventArgs.Empty);
            if (isFullSnapshot)
            {
                OnTradesSynced?.Invoke(this, new TradesSyncedEventArgs(GetTrades()));
            }

            log?.Invoke(new LogEventArgs(
                $"Framework trade repository {(isFullSnapshot ? "replaced" : "updated")} with {payload?.Trades?.Count ?? 0} trade snapshot(s).",
                LogLevel.DEBUG));
        }

        public void HandleCreateResponse(FrameworkPacket packet)
        {
            FrameworkTradeCreateResponse payload = FrameworkSerialization.DeserializePayload<FrameworkTradeCreateResponse>(packet.PayloadJson);
            if (payload == null)
            {
                return;
            }

            if (payload.Success)
            {
                pendingTradeCreationByTradeId[payload.TradeId] = payload.OtherPartyUuid;
                return;
            }

            OnTradeCreationFailure?.Invoke(this, new TradeCreationEventArgs(
                payload.OtherPartyUuid,
                ToClientFailureReason(payload.FailureReason),
                payload.FailureMessage));

            if (!string.IsNullOrEmpty(payload.FailureMessage))
            {
                log?.Invoke(new LogEventArgs($"Framework trade creation failed: {payload.FailureReason} - {payload.FailureMessage}", LogLevel.WARNING));
            }
        }

        public void HandleOfferUpdateResponse(FrameworkPacket packet)
        {
            FrameworkTradeOfferUpdateResponse payload = FrameworkSerialization.DeserializePayload<FrameworkTradeOfferUpdateResponse>(packet.PayloadJson);
            if (payload == null)
            {
                return;
            }

            string token = packet.GetCorrelationId();
            if (payload.Success)
            {
                TrackPendingTradeUpdate(payload.TradeId, token);
                return;
            }

            OnTradeUpdateFailure?.Invoke(this, new TradeUpdateEventArgs(
                GetTradeOrPlaceholder(payload.TradeId),
                ToClientFailureReason(payload.FailureReason),
                payload.FailureMessage,
                token));

            if (!string.IsNullOrEmpty(payload.FailureMessage))
            {
                log?.Invoke(new LogEventArgs($"Framework trade offer update failed for trade '{payload.TradeId}': {payload.FailureReason} - {payload.FailureMessage}", LogLevel.WARNING));
            }
        }

        public void HandleStatusUpdateResponse(FrameworkPacket packet)
        {
            FrameworkTradeStatusUpdateResponse payload = FrameworkSerialization.DeserializePayload<FrameworkTradeStatusUpdateResponse>(packet.PayloadJson);
            if (payload == null)
            {
                return;
            }

            string token = packet.GetCorrelationId();
            if (payload.Success)
            {
                TrackPendingTradeUpdate(payload.TradeId, string.Equals(token, payload.TradeId, StringComparison.OrdinalIgnoreCase) ? string.Empty : token);
                return;
            }

            OnTradeUpdateFailure?.Invoke(this, new TradeUpdateEventArgs(
                GetTradeOrPlaceholder(payload.TradeId),
                ToClientFailureReason(payload.FailureReason),
                payload.FailureMessage,
                token));

            if (!string.IsNullOrEmpty(payload.FailureMessage))
            {
                log?.Invoke(new LogEventArgs($"Framework trade status update failed for trade '{payload.TradeId}': {payload.FailureReason} - {payload.FailureMessage}", LogLevel.WARNING));
            }
        }

        public void HandleCompletedEvent(FrameworkPacket packet)
        {
            FrameworkTradeCompletionEvent payload = FrameworkSerialization.DeserializePayload<FrameworkTradeCompletionEvent>(packet.PayloadJson);
            if (payload == null)
            {
                return;
            }

            repository.Remove(payload.TradeId);
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
            OnTradeCompleted?.Invoke(this, new TradeCompletionEventArgs(payload.TradeId, true, payload.OtherPartyUuid, DecodeTradeItems(payload.Items)));
            log?.Invoke(new LogEventArgs($"Framework trade '{payload.TradeId}' completed with '{payload.OtherPartyUuid}'.", LogLevel.DEBUG));
        }

        public void HandleCancelledEvent(FrameworkPacket packet)
        {
            FrameworkTradeCompletionEvent payload = FrameworkSerialization.DeserializePayload<FrameworkTradeCompletionEvent>(packet.PayloadJson);
            if (payload == null)
            {
                return;
            }

            repository.Remove(payload.TradeId);
            RepositoryChanged?.Invoke(this, EventArgs.Empty);
            OnTradeCancelled?.Invoke(this, new TradeCompletionEventArgs(payload.TradeId, false, payload.OtherPartyUuid, DecodeTradeItems(payload.Items)));
            log?.Invoke(new LogEventArgs($"Framework trade '{payload.TradeId}' cancelled with '{payload.OtherPartyUuid}'.", LogLevel.DEBUG));
        }

        private bool FlushPendingEventsForTrade(string tradeId, bool emitUpdateSuccessWhenPendingCleared)
        {
            if (string.IsNullOrEmpty(tradeId))
            {
                return false;
            }

            bool emitted = false;
            if (pendingTradeCreationByTradeId.TryGetValue(tradeId, out string otherPartyUuid))
            {
                pendingTradeCreationByTradeId.Remove(tradeId);
                OnTradeCreationSuccess?.Invoke(this, new TradeCreationEventArgs(GetTradeOrPlaceholder(tradeId, otherPartyUuid)));
                emitted = true;
            }

            if (pendingTradeUpdateTokensByTradeId.TryGetValue(tradeId, out List<string> tokens))
            {
                pendingTradeUpdateTokensByTradeId.Remove(tradeId);
                ClientTradeSnapshot trade = GetTradeOrPlaceholder(tradeId);
                foreach (string token in tokens)
                {
                    OnTradeUpdateSuccess?.Invoke(this, new TradeUpdateEventArgs(trade, token));
                    emitted = true;
                }
            }

            if (emitUpdateSuccessWhenPendingCleared && emitted && !pendingTradeUpdateTokensByTradeId.ContainsKey(tradeId))
            {
                return true;
            }

            return emitted;
        }

        private ClientTradeSnapshot ToTradeSnapshot(FrameworkTradeStateSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return null;
            }

            FrameworkTradeParticipantSnapshot localParticipant = (snapshot.Participants ?? new List<FrameworkTradeParticipantSnapshot>())
                .SingleOrDefault(participant => participant.Uuid == userDirectory?.Uuid);
            FrameworkTradeParticipantSnapshot otherParticipant = (snapshot.Participants ?? new List<FrameworkTradeParticipantSnapshot>())
                .SingleOrDefault(participant => participant.Uuid != userDirectory?.Uuid);

            if (otherParticipant == null)
            {
                return null;
            }

            ImmutableUser otherParty = new ImmutableUser(otherParticipant.Uuid);
            if (userDirectory != null && userDirectory.TryGetUser(otherParticipant.Uuid, out ImmutableUser existingUser))
            {
                otherParty = existingUser;
            }

            return new ClientTradeSnapshot(
                snapshot.TradeId,
                otherParty,
                DecodeTradeItems(localParticipant?.ItemsOnOffer),
                DecodeTradeItems(otherParticipant.ItemsOnOffer),
                localParticipant?.Accepted ?? false,
                otherParticipant.Accepted);
        }

        private static TradeItemSnapshot[] DecodeTradeItems(IEnumerable<FrameworkItemPayload> items)
        {
            return (items ?? Enumerable.Empty<FrameworkItemPayload>())
                .Select(item =>
                {
                    if (FrameworkTradeItemPayloadCodec.TryDecodeToTradeItemSnapshot(item, out TradeItemSnapshot tradeItem))
                    {
                        return tradeItem;
                    }

                    return FrameworkTradeItemPayloadCodec.CreateUnknownTradeItemSnapshot(item?.CodecId ?? "UnknownCodec");
                })
                .ToArray();
        }

        private ClientTradeSnapshot GetTradeOrPlaceholder(string tradeId, string otherPartyUuid = "")
        {
            if (TryGetTrade(tradeId, out ClientTradeSnapshot trade))
            {
                return trade;
            }

            return new ClientTradeSnapshot(tradeId, new ImmutableUser(otherPartyUuid));
        }

        private static TradeFailureReason ToClientFailureReason(FrameworkTradeFailureReason failureReason)
        {
            switch (failureReason)
            {
                case FrameworkTradeFailureReason.SessionInvalid: return TradeFailureReason.SessionInvalid;
                case FrameworkTradeFailureReason.LoginInvalid: return TradeFailureReason.LoginInvalid;
                case FrameworkTradeFailureReason.OtherPartyDoesNotExist: return TradeFailureReason.OtherPartyDoesNotExist;
                case FrameworkTradeFailureReason.OtherPartyOffline: return TradeFailureReason.OtherPartyOffline;
                case FrameworkTradeFailureReason.NotAcceptingTrades: return TradeFailureReason.NotAcceptingTrades;
                case FrameworkTradeFailureReason.AlreadyTrading: return TradeFailureReason.AlreadyTrading;
                case FrameworkTradeFailureReason.TradeDoesNotExist: return TradeFailureReason.TradeDoesNotExist;
                case FrameworkTradeFailureReason.NotTradeParticipant: return TradeFailureReason.NotTradeParticipant;
                case FrameworkTradeFailureReason.InternalServerError:
                default:
                    return TradeFailureReason.InternalServerError;
            }
        }
    }
}
