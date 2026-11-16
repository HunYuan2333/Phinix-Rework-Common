using System;

namespace Connections
{
    public class InvalidAddressException : Exception
    {
        public override string Message => $"\'{Address}\' could not be resolved as an IP address or hostname.";

        public readonly string Address;

        public InvalidAddressException(string address) : this(address,null) { }
        public InvalidAddressException(string address,Exception cause) : base(null,cause)
        {
            this.Address = address;
        }
    }
}