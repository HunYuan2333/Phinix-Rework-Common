using System;

namespace UserManagement
{
    public interface IServerUserManager
    {
        event EventHandler<ServerLoginEventArgs> OnLogin;

        bool IsLoggedIn(string connectionId, string uuid);

        string[] GetConnections();

        bool TryGetConnection(string uuid, out string connectionId);

        bool TryGetDisplayName(string uuid, out string displayName);

        bool TryGetLoggedIn(string uuid, out bool loggedIn);

        bool TryGetAcceptingTrades(string uuid, out bool acceptingTrades);
    }
}
