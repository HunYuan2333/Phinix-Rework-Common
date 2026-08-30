using System;
using System.Collections.Generic;
using PhinixClient;
#if NET472
using PhinixClient.Framework;
#endif
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

#if NET472
    public interface IFrameworkChatClientApi
    {
        FrameworkPacket CreateOutgoingMessage(string rawMessage, ClientFrameworkContext context);

        FrameworkPacket CreateOutgoingMessage(string rawMessage, ClientFrameworkContext context, IEnumerable<string> mentionedUuids, string replyToMessageId, string replyToSnippet);

        FrameworkDisplayMessage RenderMessage(FrameworkPacket message);

        FrameworkPacket CreateHistoryRequestPacket(string sessionId, string senderUuid);

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
        bool ShowImages { get; }

        float MaxImageHeight { get; }

        ISet<string> BlockedUsers { get; }

        /// <summary>
        /// 当前会话是否已连接并登录（Authenticated &amp;&amp; LoggedIn）。
        /// 用于让聊天列表在"上线"瞬间从消息存储重新同步一次，
        /// 避免启动时就链接、进入存档后 UI 未初始化（UI 与后端不同步）。
        /// </summary>
        bool IsOnline { get; }

        event EventHandler OnDisconnect;

        event EventHandler OnUsersChanged;

        event EventHandler<UserDisplayNameChangedEventArgs> OnUserDisplayNameChanged;

        event EventHandler<UserBlockStateChangedEventArgs> OnBlockedUsersChanged;

        void CreateTrade(string uuid);

        void BlockUser(string uuid);

        void UnBlockUser(string uuid);

        void Log(LogEventArgs args);

        UIChatMessage ReplyTarget { get; }

        void SetReplyTarget(UIChatMessage message);

        void ClearReplyTarget();

        event EventHandler ReplyTargetChanged;

        void SendChatMessage(string text, IEnumerable<string> mentionedUuids);
    }
#endif
}
