/* Copyright (c) 1996-2016, OPC Foundation. All rights reserved.
   The source code in this file is covered under a dual-license scenario:
     - RCL: for OPC Foundation members in good-standing
     - GPL V2: everybody else
   RCL license terms accompanied with this source code. See http://opcfoundation.org/License/RCL/1.00/
   GNU General Public License as published by the Free Software Foundation;
   version 2 of the License are accompanied with this source code. See http://opcfoundation.org/License/GPLv2
   This source code is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
*/

using System;
using System.Collections.Generic;
using System.IdentityModel.Selectors;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Opc.Ua.Bindings
{
    /// <summary>
    /// Manages the connections for a UA TCP server.
    /// </summary>
    public class UaTcpChannelListener : ITransportListener, ITcpChannelListener
    {
        #region Constructors
        /// <summary>
        /// Initializes a new instance of the <see cref="UaTcpChannelListener"/> class.
        /// </summary>
        public UaTcpChannelListener()
        {
        }
        #endregion

        #region IDisposable Members
        /// <summary>
        /// Frees any unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// An overrideable version of the Dispose.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", MessageId = "m_simulator")]
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (m_lock)
                {
                    if (m_listeningSocket != null)
                    {
                        Utils.SilentDispose(m_listeningSocket);
                        m_listeningSocket = null;
                    }

                    if (m_listeningSocketIPv6 != null)
                    {
                        Utils.SilentDispose(m_listeningSocketIPv6);
                        m_listeningSocketIPv6 = null;
                    }

                    foreach (TcpServerChannel channel in m_channels.Values)
                    {
                        Utils.SilentDispose(channel);
                    }
                }
            }
        }
        #endregion

        #region ITransportListener Members
        /// <summary>
        /// Opens the listener and starts accepting connection.
        /// </summary>
        /// <param name="baseAddress">The base address.</param>
        /// <param name="settings">The settings to use when creating the listener.</param>
        /// <param name="callback">The callback to use when requests arrive via the channel.</param>
        /// <exception cref="ArgumentNullException">Thrown if any parameter is null.</exception>
        /// <exception cref="ServiceResultException">Thrown if any communication error occurs.</exception>
        public void Open(Uri baseAddress, TransportListenerSettings settings, ITransportListenerCallback callback)
        {
            // assign a unique guid to the listener.
            m_listenerId = Guid.NewGuid().ToString();

            m_uri = baseAddress;
            m_descriptions = settings.Descriptions;
            EndpointConfiguration configuration = settings.Configuration;

            // initialize the quotas.
            m_quotas = new ChannelQuotas();

            m_quotas.MaxBufferSize = configuration.MaxBufferSize;
            m_quotas.MaxMessageSize = configuration.MaxMessageSize;
            m_quotas.ChannelLifetime = configuration.ChannelLifetime;
            m_quotas.SecurityTokenLifetime = configuration.SecurityTokenLifetime;

            m_quotas.MessageContext = new ServiceMessageContext();

            m_quotas.MessageContext.MaxArrayLength = configuration.MaxArrayLength;
            m_quotas.MessageContext.MaxByteStringLength = configuration.MaxByteStringLength;
            m_quotas.MessageContext.MaxMessageSize = configuration.MaxMessageSize;
            m_quotas.MessageContext.MaxStringLength = configuration.MaxStringLength;
            m_quotas.MessageContext.NamespaceUris = settings.NamespaceUris;
            m_quotas.MessageContext.ServerUris = new StringTable();
            m_quotas.MessageContext.Factory = settings.Factory;

            m_quotas.CertificateValidator = settings.CertificateValidator;

            // save the server certificate.
            m_serverCertificate = settings.ServerCertificate;
            m_serverCertificateChain = settings.ServerCertificateChain;

            m_bufferManager = new BufferManager("Server", (int)Int32.MaxValue, m_quotas.MaxBufferSize);
            m_channels = new Dictionary<uint, TcpServerChannel>();

            // save the callback to the server.
            m_callback = callback;

            // start the listener.
            Start();
        }

        /// <summary>
        /// Closes the listener and stops accepting connection.
        /// </summary>
        /// <exception cref="ServiceResultException">Thrown if any communication error occurs.</exception>
        public void Close()
        {
            Stop();
        }
        #endregion

        #region ITcpChannelListener
        /// <summary>
        /// Gets the URL for the listener's endpoint.
        /// </summary>
        /// <value>The URL for the listener's endpoint.</value>
        public Uri EndpointUrl => m_uri;
        /// <summary>
        /// Binds a new socket to an existing channel.
        /// </summary>
        public bool ReconnectToExistingChannel(
            IMessageSocket socket,
            uint requestId,
            uint sequenceNumber,
            uint channelId,
            X509Certificate2 clientCertificate,
            ChannelToken token,
            OpenSecureChannelRequest request)
        {
            TcpServerChannel channel = null;

            lock (m_lock)
            {
                if (!m_channels.TryGetValue(channelId, out channel))
                {
                    throw ServiceResultException.Create(StatusCodes.BadTcpSecureChannelUnknown, "Could not find secure channel referenced in the OpenSecureChannel request.");
                }
            }

            channel.Reconnect(socket, requestId, sequenceNumber, clientCertificate, token, request);
            Utils.Trace("Channel {0} reconnected", channelId);
            return true;
        }

        /// <summary>
        /// Called when a channel closes.
        /// </summary>
        public void ChannelClosed(uint channelId)
        {
            lock (m_lock)
            {
                m_channels.Remove(channelId);
            }

            Utils.Trace("Channel {0} closed", channelId);
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Starts listening at the specified port.
        /// </summary>
        public void Start()
        {
            lock (m_lock)
            {
                // ensure a valid port.
                int port = m_uri.Port;

                if (port <= 0 || port > UInt16.MaxValue)
                {
                    port = Utils.UaTcpDefaultPort;
                }

                // create IPv4 socket.
                try
                {
                    IPEndPoint endpoint = new IPEndPoint(IPAddress.Any, port);
                    m_listeningSocket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    SocketAsyncEventArgs args = new SocketAsyncEventArgs();
                    args.Completed += OnAccept;
                    args.UserToken = m_listeningSocket;
                    m_listeningSocket.Bind(endpoint);
                    m_listeningSocket.Listen(Int32.MaxValue);
                    if (!m_listeningSocket.AcceptAsync(args))
                    {
                        OnAccept(null, args);
                    }
                }
                catch (Exception ex)
                {
                    // no IPv4 support.
                    m_listeningSocket = null;
                    Utils.Trace("failed to create IPv4 listening socket: " + ex.Message);
                }

                // create IPv6 socket
                try
                {
                    IPEndPoint endpointIPv6 = new IPEndPoint(IPAddress.IPv6Any, port);
                    m_listeningSocketIPv6 = new Socket(endpointIPv6.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    SocketAsyncEventArgs args = new SocketAsyncEventArgs();
                    args.Completed += OnAccept;
                    args.UserToken = m_listeningSocketIPv6;
                    m_listeningSocketIPv6.Bind(endpointIPv6);
                    m_listeningSocketIPv6.Listen(Int32.MaxValue);
                    if (!m_listeningSocketIPv6.AcceptAsync(args))
                    {
                        OnAccept(null, args);
                    }
                }
                catch (Exception ex)
                {
                    // no IPv6 support
                    m_listeningSocketIPv6 = null;
                    Utils.Trace("failed to create IPv6 listening socket: " + ex.Message);
                }

                if (m_listeningSocketIPv6 == null && m_listeningSocket == null)
                {
                    throw ServiceResultException.Create(
                        StatusCodes.BadNoCommunication,
                        "Failed to establish tcp listener sockets for Ipv4 and IPv6.\r\n");
                }
            }
        }

        /// <summary>
        /// Stops listening.
        /// </summary>
        public void Stop()
        {
            lock (m_lock)
            {
                if (m_listeningSocket != null)
                {
                    m_listeningSocket.Dispose();
                    m_listeningSocket = null;
                }

                if (m_listeningSocketIPv6 != null)
                {
                    m_listeningSocketIPv6.Dispose();
                    m_listeningSocketIPv6 = null;
                }
            }
        }

        /// <summary>
        /// Called when a UpdateCertificate event occured.
        /// </summary>
        internal void CertificateUpdate(
            X509CertificateValidator validator,
            X509Certificate2 serverCertificate,
            X509Certificate2Collection serverCertificateChain)
        {
            m_quotas.CertificateValidator = validator;
            m_serverCertificate = serverCertificate;
            m_serverCertificateChain = serverCertificateChain;
            foreach (var description in m_descriptions)
            {
                if (description.ServerCertificate != null)
                {
                    description.ServerCertificate = serverCertificate.RawData;
                }
            }
        }
        #endregion

        #region Socket Event Handler
        /// <summary>
        /// Handles a new connection.
        /// </summary>
        private void OnAccept(object sender, SocketAsyncEventArgs e)
        {
            TcpServerChannel channel = null;
            bool repeatAccept = false;
            do
            {
                repeatAccept = false;
                lock (m_lock)
                {
                    Socket listeningSocket = e.UserToken as Socket;

                    if (listeningSocket == null)
                    {
                        Utils.Trace("OnAccept: Listensocket was null.");
                        e.Dispose();
                        return;
                    }

                    // check if the accept socket has been created.
                    if (e.AcceptSocket != null && e.SocketError == SocketError.Success)
                    {
                        try
                        {
                            // create the channel to manage incoming messages.
                            channel = new TcpServerChannel(
                                m_listenerId,
                                this,
                                m_bufferManager,
                                m_quotas,
                                m_serverCertificate,
                                m_serverCertificateChain,
                                m_descriptions);

                            if (m_callback != null)
                            {
                                channel.SetRequestReceivedCallback(new TcpChannelRequestEventHandler(OnRequestReceived));
                            }

                            // start accepting messages on the channel.
                            channel.Attach(++m_lastChannelId, e.AcceptSocket);

                            // save the channel for shutdown and reconnects.
                            m_channels.Add(m_lastChannelId, channel);

                        }
                        catch (Exception ex)
                        {
                            Utils.Trace(ex, "Unexpected error accepting a new connection.");
                        }
                    }

                    e.Dispose();

                    if (e.SocketError != SocketError.OperationAborted)
                    {
                        // go back and wait for the next connection.
                        try
                        {
                            e = new SocketAsyncEventArgs();
                            e.Completed += OnAccept;
                            e.UserToken = listeningSocket;
                            if (!listeningSocket.AcceptAsync(e))
                            {
                                repeatAccept = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            Utils.Trace(ex, "Unexpected error listening for a new connection.");
                        }
                    }
                }
            } while (repeatAccept);
        }
        #endregion

        #region Private Methods
        /// <summary>
        /// Handles requests arriving from a channel.
        /// </summary>
        private void OnRequestReceived(TcpServerChannel channel, uint requestId, IServiceRequest request)
        {
            try
            {
                if (m_callback != null)
                {
                    IAsyncResult result = m_callback.BeginProcessRequest(
                        channel.GlobalChannelId,
                        channel.EndpointDescription,
                        request,
                        OnProcessRequestComplete,
                        new object[] { channel, requestId, request });
                }
            }
            catch (Exception e)
            {
                Utils.Trace(e, "TCPLISTENER - Unexpected error processing request.");
            }
        }

        private void OnProcessRequestComplete(IAsyncResult result)
        {
            try
            {
                object[] args = (object[])result.AsyncState;

                if (m_callback != null)
                {
                    TcpServerChannel channel = (TcpServerChannel)args[0];
                    IServiceResponse response = m_callback.EndProcessRequest(result);
                    channel.SendResponse((uint)args[1], response);
                }
            }
            catch (Exception e)
            {
                Utils.Trace(e, "TCPLISTENER - Unexpected error sending result.");
            }
        }
        #endregion

        #region Private Fields
        private object m_lock = new object();

        private string m_listenerId;
        private Uri m_uri;
        private EndpointDescriptionCollection m_descriptions;

        private BufferManager m_bufferManager;
        private ChannelQuotas m_quotas;
        private X509Certificate2 m_serverCertificate;
        private X509Certificate2Collection m_serverCertificateChain;

        private uint m_lastChannelId;

        private Socket m_listeningSocket;
        private Socket m_listeningSocketIPv6;
        private Dictionary<uint, TcpServerChannel> m_channels;

        private ITransportListenerCallback m_callback;
        #endregion
    }
}
