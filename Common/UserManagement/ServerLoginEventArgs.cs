using System;

namespace UserManagement
{
    public class ServerLoginEventArgs : EventArgs
    {
        public string ConnectionId;

        public string Uuid;

        public ServerLoginEventArgs(string connectionId, string uuid)
        {
            ConnectionId = connectionId;
            Uuid = uuid;
        }
    }
}
