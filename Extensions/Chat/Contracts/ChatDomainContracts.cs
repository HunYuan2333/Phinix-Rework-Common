using System;
using System.Collections.Generic;
using PhinixClient;

using UserManagement;
using Utils;
using Utils.Framework;

namespace PhinixClient
{
    public enum UIChatMessageStatus
    {
        Pending,
        Confirmed,
        Denied
    }

    public class UIChatMessage
    {
        public string MessageId;
        public DateTime Timestamp;
        public string SenderUuid;
        public string Message;
        public UIChatMessageStatus Status;
        public ImmutableUser User;
        public string Source;
        public List<string> MentionedUuids;
        public string ReplyToMessageId;
        public string ReplyToSnippet;
        public bool IsNotice;
        public int NoticeDurationSeconds;

        public UIChatMessage(
            string messageId,
            string senderUuid,
            string message,
            DateTime timestamp,
            UIChatMessageStatus status,
            ImmutableUser user,
            string source = null)
        {
            MessageId = messageId;
            SenderUuid = senderUuid;
            Message = message;
            Timestamp = timestamp;
            Status = status;
            User = user;
            Source = source;
        }
    }

    public class UIChatMessageEventArgs : EventArgs
    {
        public UIChatMessageEventArgs(UIChatMessage message)
        {
            Message = message;
        }

        public UIChatMessage Message;
    }
}

namespace Phinix.ChatExtension.Client
{
    public interface IClientChatService
    {
        event EventHandler<UIChatMessageEventArgs> OnChatMessageReceived;

        int UnreadMessages { get; }

        UIChatMessage[] GetChatMessages(bool markAsRead = true, bool unreadOnly = false);

        bool TryGetMessage(string messageId, out UIChatMessage message);

        int CountUnreadExcluding(IEnumerable<string> excludedUuids);

        void MarkAsRead();

        bool ShouldDisplayChatMessage(UIChatMessage message, IEnumerable<string> blockedUserUuids, bool includeBlockedMessages);

        bool ShouldPlayNotification(UIChatMessage message, string localUuid, bool playNoiseOnMessageReceived, bool isInGame, IEnumerable<string> blockedUserUuids);
    }


}
