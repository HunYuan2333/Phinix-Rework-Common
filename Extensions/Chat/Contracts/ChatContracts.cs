namespace Phinix.ChatExtension
{
    public static class FrameworkChatProtocol
    {
        public const string Capability = "builtin.chat";
        public const string MessageType = "builtin.chat.message";
        public const string HistoryRequestType = "builtin.chat.history.request";
        public const string HistorySyncCompleteType = "builtin.chat.history.sync-complete";
    }
}
