using System;
using UserManagement;

namespace PhinixClient
{
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
}
