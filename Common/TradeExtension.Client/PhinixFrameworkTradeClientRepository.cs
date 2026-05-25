using System;
using System.Collections.Generic;
using System.Linq;
using Utils.Framework;

namespace Phinix.TradeExtension.Client
{
    public sealed class PhinixFrameworkTradeClientRepository
    {
        private readonly Dictionary<string, FrameworkTradeStateSnapshot> trades = new Dictionary<string, FrameworkTradeStateSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly object syncLock = new object();

        public void ReplaceAll(IEnumerable<FrameworkTradeStateSnapshot> snapshots)
        {
            lock (syncLock)
            {
                trades.Clear();
                foreach (FrameworkTradeStateSnapshot snapshot in snapshots ?? Enumerable.Empty<FrameworkTradeStateSnapshot>())
                {
                    if (snapshot == null || string.IsNullOrEmpty(snapshot.TradeId))
                    {
                        continue;
                    }

                    trades[snapshot.TradeId] = Clone(snapshot);
                }
            }
        }

        public void Upsert(FrameworkTradeStateSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrEmpty(snapshot.TradeId))
            {
                return;
            }

            lock (syncLock)
            {
                trades[snapshot.TradeId] = Clone(snapshot);
            }
        }

        public bool Remove(string tradeId)
        {
            if (string.IsNullOrEmpty(tradeId))
            {
                return false;
            }

            lock (syncLock)
            {
                return trades.Remove(tradeId);
            }
        }

        public FrameworkTradeStateSnapshot[] GetAll()
        {
            lock (syncLock)
            {
                return trades.Values.Select(Clone).OrderBy(snapshot => snapshot.TradeId).ToArray();
            }
        }

        public bool TryGet(string tradeId, out FrameworkTradeStateSnapshot snapshot)
        {
            lock (syncLock)
            {
                if (trades.TryGetValue(tradeId, out FrameworkTradeStateSnapshot stored))
                {
                    snapshot = Clone(stored);
                    return true;
                }
            }

            snapshot = null;
            return false;
        }

        private static FrameworkTradeStateSnapshot Clone(FrameworkTradeStateSnapshot snapshot)
        {
            return new FrameworkTradeStateSnapshot
            {
                TradeId = snapshot.TradeId,
                SnapshotVersion = snapshot.SnapshotVersion,
                Participants = (snapshot.Participants ?? new List<FrameworkTradeParticipantSnapshot>())
                    .Select(participant => new FrameworkTradeParticipantSnapshot
                    {
                        Uuid = participant.Uuid,
                        Accepted = participant.Accepted,
                        ItemsOnOffer = (participant.ItemsOnOffer ?? new List<FrameworkItemPayload>())
                            .Select(item => new FrameworkItemPayload
                            {
                                CodecId = item.CodecId,
                                PayloadJson = item.PayloadJson,
                                PayloadBytes = item.PayloadBytes?.ToArray() ?? Array.Empty<byte>(),
                                Metadata = (item.Metadata ?? new List<FrameworkMetadataEntry>())
                                    .Select(entry => new FrameworkMetadataEntry { Key = entry.Key, Value = entry.Value })
                                    .ToList()
                            })
                            .ToList()
                    })
                    .ToList()
            };
        }
    }
}
