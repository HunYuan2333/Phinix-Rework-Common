using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using PhinixClient;
using PhinixClient.Framework;
using UserManagement;
using Utils.Framework;
using Verse;

namespace Phinix.ChatExtension.Client
{
    public class PhinixFrameworkChatService : IFrameworkChatClientApi
    {
        public event EventHandler HistorySynced;

        public FrameworkPacket CreateOutgoingMessage(string rawMessage, ClientFrameworkContext context)
        {
            global::Phinix.Framework.BuiltInChatMessagePayload payload = new global::Phinix.Framework.BuiltInChatMessagePayload
            {
                MessageId = Guid.NewGuid().ToString(),
                Message = rawMessage ?? string.Empty
            };

            return new FrameworkPacket
            {
                Flow = global::Phinix.Framework.FrameworkFlow.Message,
                MessageType = FrameworkChatProtocol.MessageType,
                MessageId = payload.MessageId,
                SenderUuid = context.SenderUuid,
                PayloadBytes = payload.ToByteArray()
            };
        }

        public FrameworkDisplayMessage RenderMessage(FrameworkPacket message)
        {
            global::Phinix.Framework.BuiltInChatMessagePayload payload = global::Phinix.Framework.BuiltInChatMessagePayload.Parser.ParseFrom(message.PayloadBytes ?? Array.Empty<byte>());
            long timestampTicks = payload.Timestamp != null
                ? payload.Timestamp.ToDateTime().ToUniversalTime().Ticks
                : message.TimestampUtcTicks;

            return new FrameworkDisplayMessage
            {
                MessageId = payload.MessageId ?? message.MessageId,
                SenderUuid = payload.SenderUuid ?? message.SenderUuid,
                TimestampUtcTicks = timestampTicks,
                Source = "builtin_chat",
                Text = payload.Message
            };
        }

        public FrameworkPacket CreateHistoryRequestPacket(string sessionId, string senderUuid)
        {
            return new FrameworkPacket
            {
                Flow = global::Phinix.Framework.FrameworkFlow.Command,
                CommandKind = global::Phinix.Framework.FrameworkCommandKind.Request,
                MessageType = FrameworkChatProtocol.HistoryRequestType,
                SessionId = sessionId ?? string.Empty,
                SenderUuid = senderUuid ?? string.Empty,
                PayloadBytes = new Empty().ToByteArray()
            };
        }

        public void RequestHistory(IFrameworkClientTransport frameworkClient, bool authenticated, bool loggedIn, string sessionId, string senderUuid)
        {
            if (frameworkClient == null || !authenticated || !loggedIn || string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(senderUuid))
            {
                return;
            }

            if (!frameworkClient.HasRemoteCapability(FrameworkChatProtocol.HistoryRequestType))
            {
                return;
            }

            frameworkClient.SendFrameworkPacket(CreateHistoryRequestPacket(sessionId, senderUuid));
        }

        public UIChatMessage[] BuildUiMessages(IEnumerable<FrameworkDisplayMessage> messages, IClientUserDirectory userDirectory)
        {
            return (messages ?? Enumerable.Empty<FrameworkDisplayMessage>())
                .Select(message => ToUiMessage(message, userDirectory))
                .OrderBy(message => message.Timestamp)
                .ToArray();
        }

        public bool TryGetUiMessage(IEnumerable<FrameworkDisplayMessage> messages, string messageId, IClientUserDirectory userDirectory, out UIChatMessage message)
        {
            FrameworkDisplayMessage frameworkMessage = (messages ?? Enumerable.Empty<FrameworkDisplayMessage>())
                .SingleOrDefault(candidate => candidate.MessageId == messageId);

            if (frameworkMessage == null)
            {
                message = null;
                return false;
            }

            message = ToUiMessage(frameworkMessage, userDirectory);
            return true;
        }

        public int CountUnreadExcluding(IEnumerable<FrameworkDisplayMessage> messages, IEnumerable<string> excludedUuids)
        {
            HashSet<string> excluded = new HashSet<string>(excludedUuids ?? Enumerable.Empty<string>());
            return (messages ?? Enumerable.Empty<FrameworkDisplayMessage>())
                .Count(message => !excluded.Contains(message.SenderUuid));
        }

        public bool ShouldDisplayChatMessage(UIChatMessage message, IEnumerable<string> blockedUserUuids, bool includeBlockedMessages)
        {
            if (message == null)
            {
                return false;
            }

            if (includeBlockedMessages)
            {
                return true;
            }

            HashSet<string> blockedUsers = new HashSet<string>(blockedUserUuids ?? Enumerable.Empty<string>());
            return !blockedUsers.Contains(message.SenderUuid);
        }

        public bool ShouldPlayNotification(UIChatMessage message, string localUuid, bool playNoiseOnMessageReceived, bool isInGame, IEnumerable<string> blockedUserUuids)
        {
            if (message == null || !playNoiseOnMessageReceived || !isInGame)
            {
                return false;
            }

            if (message.SenderUuid == localUuid || message.SenderUuid == FrameworkProtocol.SystemSenderUuid)
            {
                return false;
            }

            HashSet<string> blockedUsers = new HashSet<string>(blockedUserUuids ?? Enumerable.Empty<string>());
            return !blockedUsers.Contains(message.SenderUuid);
        }

        public UIChatMessage ToUiMessage(FrameworkDisplayMessage message, IClientUserDirectory userDirectory)
        {
            ImmutableUser user;
            if (message.SenderUuid == FrameworkProtocol.SystemSenderUuid)
            {
                user = new ImmutableUser(
                    FrameworkProtocol.SystemSenderUuid,
                    "Phinix_framework_systemDisplayName".Translate().Resolve(),
                    true,
                    false);
            }
            else if (userDirectory == null || !userDirectory.TryGetUser(message.SenderUuid, out user))
            {
                user = new ImmutableUser(message.SenderUuid);
            }

            return new UIChatMessage(
                messageId: message.MessageId,
                senderUuid: message.SenderUuid,
                message: ResolveDisplayText(message),
                timestamp: new DateTime(message.TimestampUtcTicks, DateTimeKind.Utc),
                status: UIChatMessageStatus.Confirmed,
                user: user,
                source: message.Source);
        }

        public void NotifyHistorySynced()
        {
            HistorySynced?.Invoke(this, EventArgs.Empty);
        }

        private string ResolveDisplayText(FrameworkDisplayMessage message)
        {
            if (!string.IsNullOrEmpty(message.TranslationKey))
            {
                List<string> translationArgs = message.TranslationArgs ?? new List<string>();
                if (translationArgs.Any())
                {
#pragma warning disable CS0618
                    return message.TranslationKey.Translate(translationArgs.Cast<object>().ToArray());
#pragma warning restore CS0618
                }

                return message.TranslationKey.Translate();
            }

            return message.Text ?? string.Empty;
        }
    }
}
