using System;
using System.Collections.Generic;
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

namespace PhinixClient.Framework
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

    public interface IFrameworkChatClientApi
    {
        FrameworkPacket CreateOutgoingMessage(string rawMessage, ClientFrameworkContext context);

        FrameworkDisplayMessage RenderMessage(FrameworkPacket message);

        FrameworkPacket CreateHistoryRequestPacket(string sessionId, string senderUuid);

        void RequestHistory(IFrameworkClientTransport frameworkClient, bool authenticated, bool loggedIn, string sessionId, string senderUuid);

        UIChatMessage[] BuildUiMessages(IEnumerable<FrameworkDisplayMessage> messages, IClientUserDirectory userDirectory);

        bool TryGetUiMessage(IEnumerable<FrameworkDisplayMessage> messages, string messageId, IClientUserDirectory userDirectory, out UIChatMessage message);

        int CountUnreadExcluding(IEnumerable<FrameworkDisplayMessage> messages, IEnumerable<string> excludedUuids);

        bool ShouldDisplayChatMessage(UIChatMessage message, IEnumerable<string> blockedUserUuids, bool includeBlockedMessages);

        bool ShouldPlayNotification(UIChatMessage message, string localUuid, bool playNoiseOnMessageReceived, bool isInGame, IEnumerable<string> blockedUserUuids);

        UIChatMessage ToUiMessage(FrameworkDisplayMessage message, IClientUserDirectory userDirectory);
    }

    public interface IChatUiHostContext
    {
        IClientChatService ChatService { get; }

        string Uuid { get; }

        int ChatMessageLimit { get; }

        bool ShowNameFormatting { get; }

        bool ShowChatFormatting { get; }

        ISet<string> BlockedUsers { get; }

        event EventHandler OnDisconnect;

        event EventHandler OnUsersChanged;

        event EventHandler<UserDisplayNameChangedEventArgs> OnUserDisplayNameChanged;

        event EventHandler<UserBlockStateChangedEventArgs> OnBlockedUsersChanged;

        void CreateTrade(string uuid);

        void BlockUser(string uuid);

        void UnBlockUser(string uuid);

        void Log(LogEventArgs args);
    }
}
