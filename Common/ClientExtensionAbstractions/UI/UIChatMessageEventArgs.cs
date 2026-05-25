using System;

namespace PhinixClient
{
    public class UIChatMessageEventArgs : EventArgs
    {
        public UIChatMessage Message;

        public UIChatMessageEventArgs(UIChatMessage message)
        {
            Message = message;
        }
    }
}
