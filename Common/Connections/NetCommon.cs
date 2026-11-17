using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LiteNetLib;
using LiteNetLib.Utils;
using Utils;

namespace Connections
{
    public abstract class NetCommon : ILoggable
    {
        public static readonly Version Version = Assembly.GetAssembly(typeof(NetCommon)).GetName().Version;
        
        /// <inheritdoc />
        public abstract event EventHandler<LogEventArgs> OnLogEntry;
        /// <inheritdoc />
        public abstract void RaiseLogEntry(LogEventArgs e);
        
        /// <summary>
        /// Listener that handles communications over the wire.
        /// </summary>
        protected internal EventBasedNetListener listener = new EventBasedNetListener();

        /// <summary>
        /// Packet handler callback delegate. Used as a callback when registering packet handlers.
        /// Exposes basic information about the incoming packet and a copy of the data.
        /// </summary>
        /// <param name="module">Target module</param>
        /// <param name="connectionId">Original connection ID</param>
        /// <param name="incomingObject">Data payload</param>
        public delegate void PacketHandlerDelegate(string module, string connectionId, byte[] incomingObject);

        /// <summary>
        /// Dictionary containing packet types and the handlers registered to them.
        /// Protected by <see cref="handlerLock"/> for concurrent access from
        /// init/registration threads and the LiteNetLib polling thread.
        /// </summary>
        private readonly Dictionary<string, PacketHandlerDelegate> registeredPacketHandlers = new Dictionary<string, PacketHandlerDelegate>();
        private readonly object handlerLock = new object();

        public NetCommon()
        {
            // Register the packet handler wrapper
            listener.NetworkReceiveEvent += packetHandlerCallbackWrapper;
        }

        /// <summary>
        /// Registers a callback to be run whenever a given packet type is received.
        /// </summary>
        /// <param name="packetType">Type of packets handled by handler</param>
        /// <param name="callback">Callback delegate</param>
        public void RegisterPacketHandler(string packetType, PacketHandlerDelegate callback)
        {
            lock (handlerLock)
            {
                if (registeredPacketHandlers.ContainsKey(packetType))
                {
                    throw new PacketHandlerAlreadyRegisteredException(packetType);
                }

                registeredPacketHandlers.Add(packetType, callback);
            }
        }

        /// <summary>
        /// Unregisters a packet handler callback by the type of packets it handles.
        /// </summary>
        /// <param name="packetType">Type of packets handled by handler</param>
        public void UnregisterPacketHandler(string packetType)
        {
            lock (handlerLock)
            {
                if (registeredPacketHandlers.ContainsKey(packetType))
                {
                    registeredPacketHandlers.Remove(packetType);
                }
            }
        }

        /// <summary>
        /// Unregisters all currently registered packet handlers.
        /// </summary>
        public void UnregisterAllPacketHandlers()
        {
            lock (handlerLock)
            {
                registeredPacketHandlers.Clear();
            }
        }
        
        /// <summary>
        /// Wrapper callback for incoming packets to translate from network library-specific classes to more generic ones.
        /// Called whenever any packet type with a registered handler is received to adapt the callback values.
        /// </summary>
        /// <param name="peer">Peer the packet originated from</param>
        /// <param name="reader">Data reader containing the payload</param>
        // Endpoint adapters can bind a sensitive diagnostic sink without changing wire contracts.
        protected Action<string,Exception> PacketFault;
        private void ReportPacketFault(string operation,Exception error)
        {
            if(PacketFault!=null)try {PacketFault(operation,error);return;}catch(Exception observer) {
                Utils.Framework.ExtensionDiagnostics.Write(null,(text,level)=>RaiseLogEntry(new LogEventArgs(text,level)),
                    new Utils.Framework.HostLogEntry("transport",LogLevel.ERROR,"PacketDiagnosticObserverFailed",observer));
            }
            Utils.Framework.ExtensionDiagnostics.Write(null,(text,level)=>RaiseLogEntry(new LogEventArgs(text,level)),
                new Utils.Framework.HostLogEntry("transport",LogLevel.ERROR,operation,error));
        }

        private void packetHandlerCallbackWrapper(NetPeer peer, NetDataReader reader)
        {
            string type;
            byte[] data;
            string connectionId;

            try
            {
                connectionId = peer.ConnectId.ToString("X");
                type = reader.GetString();
                data = reader.GetRemainingBytes();
            }
            catch (Exception ex)
            {
                ReportPacketFault("PacketEnvelopeReadFailed",ex);
                return;
            }

            lock (handlerLock)
            {
                if (registeredPacketHandlers.TryGetValue(type, out PacketHandlerDelegate handler))
                {
                    try
                    {
                        handler.Invoke(type, connectionId, data);
                    }
                    catch (Exception ex)
                    {
                        ReportPacketFault("PacketHandlerFailed",ex);
                    }
                }
            }
        }
    }
}