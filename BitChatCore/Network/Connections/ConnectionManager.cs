﻿/*
Technitium Bit Chat
Copyright (C) 2017  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using BitChatCore.Network.KademliaDHT;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using TechnitiumLibrary.IO;
using TechnitiumLibrary.Net;
using TechnitiumLibrary.Net.Proxy;
using TechnitiumLibrary.Net.UPnP.Networking;

namespace BitChatCore.Network.Connections
{
    public enum InternetConnectivityStatus
    {
        Identifying = 0,
        NoInternetConnection = 1,
        DirectInternetConnection = 2,
        HttpProxyInternetConnection = 3,
        Socks5ProxyInternetConnection = 4,
        NatInternetConnectionViaUPnPRouter = 5,
        NatOrFirewalledInternetConnection = 6,
        FirewalledInternetConnection = 7,
        ProxyConnectionFailed = 8,
        NoProxyInternetConnection = 9
    }

    public enum UPnPDeviceStatus
    {
        Identifying = 0,
        Disabled = 1,
        DeviceNotFound = 2,
        ExternalIpPrivate = 3,
        PortForwarded = 4,
        PortForwardingFailed = 5,
        PortForwardedNotAccessible = 6
    }

    class ConnectionManager : IDhtNodeManager, IDisposable
    {
        #region events

        public event EventHandler IPv4InternetConnectivityStatusChanged;
        public event EventHandler IPv6InternetConnectivityStatusChanged;
        public event BitChatNetworkInvitation BitChatNetworkChannelInvitation;
        public event BitChatNetworkChannelRequest BitChatNetworkChannelRequest;
        public event TcpRelayPeersAvailable TcpRelayPeersAvailable;

        #endregion

        #region variables

        const int WRITE_BUFFERED_STREAM_SIZE = 8 * 1024;

        const int SOCKET_CONNECTION_TIMEOUT = 10000; //10 sec connection timeout
        const int SOCKET_INITIAL_SEND_TIMEOUT = 10000;
        const int SOCKET_INITIAL_RECV_TIMEOUT = 10000;

        const int SOCKET_SEND_BUFFER_SIZE = 16 * 1024;
        const int SOCKET_RECV_BUFFER_SIZE = 16 * 1024;

        const int WRITE_TIMEOUT = 30000; //30 sec socket timeout; application protocol NOOPs at 15 sec
        const int READ_TIMEOUT = 120000; //keep socket open for long time to allow tunnelling requests between time

        const int DHT_SEED_TRACKER_UPDATE_INTERVAL = 300;

        readonly BitChatProfile _profile;

        readonly BinaryNumber _localPeerID;

        readonly Dictionary<IPEndPoint, object> _makeConnectionList = new Dictionary<IPEndPoint, object>();
        readonly Dictionary<IPEndPoint, object> _makeVirtualConnectionList = new Dictionary<IPEndPoint, object>();

        readonly Dictionary<IPEndPoint, Connection> _connectionListByConnectionID = new Dictionary<IPEndPoint, Connection>();
        readonly Dictionary<BinaryNumber, Connection> _connectionListByPeerID = new Dictionary<BinaryNumber, Connection>();

        //tcp listener
        readonly Socket _tcpListener;
        readonly Thread _tcpListenerThread;

        //dht
        const string DHT_DNS_BOOTSTRAP_DOMAIN = "dht.bitchat.im";
        readonly BinaryNumber _ipv4DhtBootstrapTrackerNetworkID = new BinaryNumber(new byte[] { 0xfa, 0x20, 0xf3, 0x45, 0xe6, 0xbe, 0x43, 0x68, 0xcb, 0x1e, 0x2a, 0xfb, 0xc0, 0x08, 0x0d, 0x95, 0xf1, 0xd1, 0xe6, 0x5b });
        readonly BinaryNumber _ipv6DhtBootstrapTrackerNetworkID = new BinaryNumber(new byte[] { 0xB9, 0xB0, 0x21, 0xA5, 0x59, 0xBC, 0xA7, 0x2E, 0x3A, 0x08, 0x27, 0x87, 0xDA, 0x6D, 0xE5, 0x70, 0xB6, 0xE1, 0xD4, 0xE9 });
        readonly TrackerManager _ipv4DhtBootstrapTracker;
        readonly TrackerManager _ipv6DhtBootstrapTracker;
        readonly DhtNode _ipv4DhtNode;
        readonly DhtNode _ipv6DhtNode;

        //internet connectivity
        const int CONNECTIVITY_CHECK_TIMER_INTERVAL = 60 * 1000;
        Timer _connectivityCheckTimer;

        readonly Uri IPv4_CONNECTIVITY_CHECK_WEB_SERVICE = new Uri("https://bitchat.im/connectivity/check.aspx");
        InternetConnectivityStatus _ipv4InternetStatus = InternetConnectivityStatus.Identifying;
        InternetGatewayDevice _upnpDevice;
        UPnPDeviceStatus _upnpDeviceStatus = UPnPDeviceStatus.Identifying;
        IPAddress _ipv4LocalLiveIP;
        IPAddress _upnpExternalIP;
        IPEndPoint _ipv4ConnectivityCheckExternalEP;

        InternetConnectivityStatus _ipv6InternetStatus = InternetConnectivityStatus.Identifying;
        IPAddress _ipv6LocalLiveIP;

        //received invitations
        readonly Dictionary<IPEndPoint, DateTime> _receivedInvitations = new Dictionary<IPEndPoint, DateTime>(10);
        const int INVITATION_INFO_EXPIRY_MINUTES = 15;

        readonly int _localPort;

        #endregion

        #region constructor

        public ConnectionManager(BitChatProfile profile)
        {
            IPEndPoint localEP;

            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                    if (Environment.OSVersion.Version.Major < 6)
                    {
                        //below vista
                        _tcpListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        localEP = new IPEndPoint(IPAddress.Any, profile.LocalPort);
                    }
                    else
                    {
                        //vista & above
                        _tcpListener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                        _tcpListener.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
                        localEP = new IPEndPoint(IPAddress.IPv6Any, profile.LocalPort);
                    }
                    break;

                case PlatformID.Unix: //mono framework
                    if (Socket.OSSupportsIPv6)
                    {
                        _tcpListener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                        localEP = new IPEndPoint(IPAddress.IPv6Any, profile.LocalPort);
                    }
                    else
                    {
                        _tcpListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        localEP = new IPEndPoint(IPAddress.Any, profile.LocalPort);
                    }

                    break;

                default: //unknown
                    _tcpListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    localEP = new IPEndPoint(IPAddress.Any, profile.LocalPort);
                    break;
            }

            try
            {
                _tcpListener.Bind(localEP);
                _tcpListener.Listen(10);
            }
            catch
            {
                localEP.Port = 0;

                _tcpListener.Bind(localEP);
                _tcpListener.Listen(10);
            }

            _profile = profile;

            _localPort = (_tcpListener.LocalEndPoint as IPEndPoint).Port;
            _localPeerID = BinaryNumber.GenerateRandomNumber160();

            //init dht node
            _ipv4DhtNode = new DhtNode(new IPEndPoint(IPAddress.Any, _localPort), this);
            _ipv6DhtNode = new DhtNode(new IPEndPoint(IPAddress.IPv6Any, _localPort), this);

            //add bootstrap node via DNS
            ThreadPool.QueueUserWorkItem(delegate (object state)
            {
                try
                {
                    DnsClient dnsClient = new DnsClient();
                    dnsClient.Proxy = _profile.Proxy;

                    DnsDatagram response = dnsClient.Resolve(DHT_DNS_BOOTSTRAP_DOMAIN, DnsResourceRecordType.TXT);

                    foreach (DnsResourceRecord answer in response.Answer)
                    {
                        if (answer.Name.Equals(DHT_DNS_BOOTSTRAP_DOMAIN) && (answer.Type == DnsResourceRecordType.TXT))
                        {
                            DnsTXTRecord txtRecord = (DnsTXTRecord)answer.RDATA;

                            try
                            {
                                string[] values = txtRecord.TXTData.Split('|');

                                IPEndPoint nodeEP = new IPEndPoint(IPAddress.Parse(values[0]), int.Parse(values[1]));

                                switch (nodeEP.AddressFamily)
                                {
                                    case AddressFamily.InterNetwork:
                                        _ipv4DhtNode.AddNode(nodeEP);
                                        break;

                                    case AddressFamily.InterNetworkV6:
                                        _ipv6DhtNode.AddNode(nodeEP);
                                        break;
                                }
                            }
                            catch
                            { }
                        }
                    }
                }
                catch
                { }

                //add other bootstrap nodes
                _ipv4DhtNode.AddNode(profile.BootstrapDhtNodes);
                _ipv6DhtNode.AddNode(profile.BootstrapDhtNodes);
            });

            //setup dht bootstrap tracker
            _ipv4DhtBootstrapTracker = new TrackerManager(_ipv4DhtBootstrapTrackerNetworkID, _localPort, null, null, DHT_SEED_TRACKER_UPDATE_INTERVAL);
            _ipv4DhtBootstrapTracker.Proxy = _profile.Proxy;
            _ipv4DhtBootstrapTracker.DiscoveredPeers += IPv4DhtSeedingTracker_DiscoveredPeers;
            _ipv4DhtBootstrapTracker.AddTracker(BitChatProfile.IPv4DefaultTrackerURIs);

            _ipv6DhtBootstrapTracker = new TrackerManager(_ipv6DhtBootstrapTrackerNetworkID, _localPort, null, null, DHT_SEED_TRACKER_UPDATE_INTERVAL);
            _ipv6DhtBootstrapTracker.Proxy = _profile.Proxy;
            _ipv6DhtBootstrapTracker.DiscoveredPeers += IPv6DhtSeedingTracker_DiscoveredPeers;
            _ipv6DhtBootstrapTracker.AddTracker(BitChatProfile.IPv6DefaultTrackerURIs);

            //start accepting connections
            _tcpListenerThread = new Thread(AcceptTcpConnectionAsync);
            _tcpListenerThread.IsBackground = true;
            _tcpListenerThread.Start();

            //start upnp process
            _connectivityCheckTimer = new Timer(ConnectivityCheckTimerCallback, null, 1000, Timeout.Infinite);
        }

        #endregion

        #region IDisposable

        ~ConnectionManager()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        bool _disposed = false;

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                //shutdown tcp
                if (_tcpListener != null)
                    _tcpListener.Dispose();

                //stop dht bootstrap tracker
                if (_ipv4DhtBootstrapTracker != null)
                    _ipv4DhtBootstrapTracker.Dispose();

                if (_ipv6DhtBootstrapTracker != null)
                    _ipv6DhtBootstrapTracker.Dispose();

                //stop dht client
                if (_ipv4DhtNode != null)
                    _ipv4DhtNode.Dispose();

                if (_ipv6DhtNode != null)
                    _ipv6DhtNode.Dispose();

                //shutdown upnp port mapping
                if (_connectivityCheckTimer != null)
                {
                    _connectivityCheckTimer.Dispose();
                    _connectivityCheckTimer = null;
                }

                //stop channel services
                List<Connection> connectionList = new List<Connection>();

                lock (_connectionListByConnectionID)
                {
                    foreach (Connection connection in _connectionListByConnectionID.Values)
                        connectionList.Add(connection);
                }

                foreach (Connection connection in connectionList)
                {
                    try
                    {
                        connection.Dispose();
                    }
                    catch
                    { }
                }

                _disposed = true;
            }
        }

        #endregion

        #region private

        private Socket MakeTcpConnection(IPEndPoint remoteNodeEP)
        {
            Socket socket = null;

            try
            {
                if (_profile.Proxy != null)
                {
                    switch (_profile.Proxy.Type)
                    {
                        case NetProxyType.Http:
                            socket = _profile.Proxy.HttpProxy.Connect(remoteNodeEP);
                            break;

                        case NetProxyType.Socks5:
                            using (SocksConnectRequestHandler requestHandler = _profile.Proxy.SocksProxy.Connect(remoteNodeEP))
                            {
                                socket = requestHandler.GetSocket();
                            }
                            break;

                        default:
                            throw new NotSupportedException("Proxy type not supported.");
                    }
                }
                else
                {
                    socket = new Socket(remoteNodeEP.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                    IAsyncResult result = socket.BeginConnect(remoteNodeEP, null, null);
                    if (!result.AsyncWaitHandle.WaitOne(SOCKET_CONNECTION_TIMEOUT))
                        throw new SocketException((int)SocketError.TimedOut);

                    if (!socket.Connected)
                        throw new SocketException((int)SocketError.ConnectionRefused);
                }

                socket.NoDelay = true;
                socket.SendTimeout = SOCKET_INITIAL_SEND_TIMEOUT;
                socket.ReceiveTimeout = SOCKET_INITIAL_RECV_TIMEOUT;

                return socket;
            }
            catch
            {
                if (socket != null)
                    socket.Dispose();

                throw;
            }
        }

        private void AcceptTcpConnectionAsync(object parameter)
        {
            try
            {
                do
                {
                    Socket socket = _tcpListener.Accept();

                    socket.NoDelay = true;
                    socket.SendTimeout = SOCKET_INITIAL_SEND_TIMEOUT;
                    socket.ReceiveTimeout = SOCKET_INITIAL_RECV_TIMEOUT;

                    Thread t = new Thread(delegate (object state)
                    {
                        try
                        {
                            Stream s = new WriteBufferedStream(new NetworkStream(socket, true), WRITE_BUFFERED_STREAM_SIZE);

                            AcceptDecoyHttpConnection(s);

                            //set socket options
                            socket.SendBufferSize = SOCKET_SEND_BUFFER_SIZE;
                            socket.ReceiveBufferSize = SOCKET_RECV_BUFFER_SIZE;

                            IPEndPoint remotePeerEP = socket.RemoteEndPoint as IPEndPoint;

                            if (NetUtilities.IsIPv4MappedIPv6Address(remotePeerEP.Address))
                                remotePeerEP = new IPEndPoint(NetUtilities.ConvertFromIPv4MappedIPv6Address(remotePeerEP.Address), remotePeerEP.Port);

                            AcceptConnectionInitiateProtocol(s, remotePeerEP);
                        }
                        catch
                        {
                            socket.Dispose();
                        }
                    });

                    t.IsBackground = true;
                    t.Start();
                }
                while (true);
            }
            catch
            { }
        }

        private Connection AddConnection(Stream s, BinaryNumber remotePeerID, IPEndPoint remotePeerEP)
        {
            if ((remotePeerEP.AddressFamily == AddressFamily.InterNetworkV6) && (remotePeerEP.Address.ScopeId != 0))
                remotePeerEP = new IPEndPoint(new IPAddress(remotePeerEP.Address.GetAddressBytes()), remotePeerEP.Port);

            lock (_connectionListByConnectionID)
            {
                //check for self
                if (_localPeerID.Equals(remotePeerID))
                    return null;

                //check for existing connection by connection id
                if (_connectionListByConnectionID.ContainsKey(remotePeerEP))
                {
                    Connection existingConnection = _connectionListByConnectionID[remotePeerEP];

                    //check for virtual vs real connection
                    bool currentIsVirtual = Connection.IsStreamProxyTunnelConnection(s);
                    bool existingIsVirtual = existingConnection.IsProxyTunnelConnection;

                    if (existingIsVirtual && !currentIsVirtual)
                    {
                        //existing is virtual and current is real; remove existing connection
                        existingConnection.Dispose();
                    }
                    else if (currentIsVirtual)
                    {
                        //existing is real/virtual and current is virtual; keep existing connection
                        return null;
                    }
                }
                else if (_connectionListByPeerID.ContainsKey(remotePeerID)) //check for existing connection by peer id
                {
                    Connection existingConnection = _connectionListByPeerID[remotePeerID];

                    //check for virtual vs real connection
                    bool currentIsVirtual = Connection.IsStreamProxyTunnelConnection(s);
                    bool existingIsVirtual = existingConnection.IsProxyTunnelConnection;

                    if (existingIsVirtual && !currentIsVirtual)
                    {
                        //existing is virtual and current is real; remove existing connection
                        existingConnection.Dispose();
                    }
                    else if (currentIsVirtual)
                    {
                        //existing is real/virtual and current is virtual; keep existing connection
                        return null;
                    }
                    else
                    {
                        //compare existing and new peer ip end-point
                        if (AllowNewConnection(existingConnection.RemotePeerEP, remotePeerEP))
                        {
                            //remove existing connection and allow new connection
                            existingConnection.Dispose();
                        }
                        else
                        {
                            //keep existing connection
                            return null;
                        }
                    }
                }

                //add connection
                Connection connection = new Connection(s, remotePeerID, remotePeerEP, this);
                _connectionListByConnectionID.Add(remotePeerEP, connection);
                _connectionListByPeerID.Add(remotePeerID, connection);

                //set event handlers
                connection.BitChatNetworkInvitation += Connection_BitChatNetworkInvitation;
                connection.BitChatNetworkChannelRequest += BitChatNetworkChannelRequest;
                connection.TcpRelayPeersAvailable += TcpRelayPeersAvailable;
                connection.Disposed += Connection_Disposed;

                //start service
                connection.Start();

                return connection;
            }
        }

        private void Connection_BitChatNetworkInvitation(BinaryNumber hashedEmailAddress, IPEndPoint peerEP, string message)
        {
            //this method is called async by the event
            //this mechanism prevents multiple invitations events from same peerEP
            try
            {
                lock (_receivedInvitations)
                {
                    if (_receivedInvitations.ContainsKey(peerEP))
                    {
                        //check for expiry
                        DateTime receivedOn = _receivedInvitations[peerEP];
                        DateTime currentTime = DateTime.UtcNow;

                        if (receivedOn.AddMinutes(INVITATION_INFO_EXPIRY_MINUTES) > currentTime)
                            return; //current entry still not expired so dont invoke the event to UI

                        //update received on time
                        _receivedInvitations[peerEP] = currentTime;
                    }
                    else
                    {
                        //add entry
                        _receivedInvitations.Add(peerEP, DateTime.UtcNow);
                    }
                }

                BitChatNetworkChannelInvitation?.Invoke(hashedEmailAddress, peerEP, message);
            }
            catch
            { }
            finally
            {
                //remove expired entries
                lock (_receivedInvitations)
                {
                    List<IPEndPoint> removeList = new List<IPEndPoint>(5);
                    DateTime currentTime = DateTime.UtcNow;

                    foreach (KeyValuePair<IPEndPoint, DateTime> item in _receivedInvitations)
                    {
                        if (item.Value.AddMinutes(INVITATION_INFO_EXPIRY_MINUTES) < currentTime)
                            removeList.Add(item.Key);
                    }

                    foreach (IPEndPoint key in removeList)
                        _receivedInvitations.Remove(key);
                }
            }
        }

        private void Connection_Disposed(object sender, EventArgs e)
        {
            //remove connection from connection manager
            Connection connection = sender as Connection;
            IPEndPoint remotePeerEP = connection.RemotePeerEP;

            if ((remotePeerEP.AddressFamily == AddressFamily.InterNetworkV6) && (remotePeerEP.Address.ScopeId != 0))
                remotePeerEP = new IPEndPoint(new IPAddress(remotePeerEP.Address.GetAddressBytes()), remotePeerEP.Port);

            lock (_connectionListByConnectionID)
            {
                _connectionListByConnectionID.Remove(remotePeerEP);
                _connectionListByPeerID.Remove(connection.RemotePeerID);
            }
        }

        private bool AllowNewConnection(IPEndPoint existingIP, IPEndPoint newIP)
        {
            if (existingIP.AddressFamily != newIP.AddressFamily)
                return false;

            if (NetUtilities.IsPrivateIP(existingIP.Address))
                return false;

            return true;
        }

        internal bool IsPeerConnectionAvailable(IPEndPoint remotePeerEP)
        {
            if ((remotePeerEP.AddressFamily == AddressFamily.InterNetworkV6) && (remotePeerEP.Address.ScopeId != 0))
                remotePeerEP = new IPEndPoint(new IPAddress(remotePeerEP.Address.GetAddressBytes()), remotePeerEP.Port);

            lock (_connectionListByConnectionID)
            {
                return _connectionListByConnectionID.ContainsKey(remotePeerEP);
            }
        }

        internal Connection GetExistingConnection(IPEndPoint remotePeerEP)
        {
            if ((remotePeerEP.AddressFamily == AddressFamily.InterNetworkV6) && (remotePeerEP.Address.ScopeId != 0))
                remotePeerEP = new IPEndPoint(new IPAddress(remotePeerEP.Address.GetAddressBytes()), remotePeerEP.Port);

            lock (_connectionListByConnectionID)
            {
                if (_connectionListByConnectionID.ContainsKey(remotePeerEP))
                    return _connectionListByConnectionID[remotePeerEP];

                return null;
            }
        }

        private Connection MakeVirtualConnection(IPEndPoint remotePeerEP)
        {
            //ping all connected peer channels
            object lockObject = new object();
            Connection proxyPeerConnection = null;

            lock (lockObject)
            {
                lock (_connectionListByConnectionID)
                {
                    if (_connectionListByConnectionID.Count == 0)
                        throw new Exception("No peer available for virtual connection.");

                    foreach (Connection connection in _connectionListByConnectionID.Values)
                    {
                        Thread t = new Thread(delegate (object state)
                        {
                            try
                            {
                                if (connection.RequestPeerStatus(remotePeerEP))
                                {
                                    lock (lockObject)
                                    {
                                        if (proxyPeerConnection == null)
                                        {
                                            proxyPeerConnection = connection;
                                            Monitor.Pulse(lockObject);
                                        }
                                    }
                                }
                            }
                            catch
                            { }
                        });

                        t.IsBackground = true;
                        t.Start();
                    }
                }

                if (!Monitor.Wait(lockObject, 10000))
                    throw new Exception("Timed out while waiting for available peers for virtual connection.");

                //create tunnel via proxy peer
                Stream proxyNetworkStream = proxyPeerConnection.RequestProxyTunnel(remotePeerEP);

                //make new connection protocol begins
                return MakeConnectionInitiateProtocol(proxyNetworkStream, remotePeerEP);
            }
        }

        private static void AcceptDecoyHttpConnection(Stream s)
        {
            //read http request
            int byteRead;
            int crlfCount = 0;

            while (true)
            {
                byteRead = s.ReadByte();
                switch (byteRead)
                {
                    case '\r':
                    case '\n':
                        crlfCount++;
                        break;

                    case -1:
                        throw new EndOfStreamException();

                    default:
                        crlfCount = 0;
                        break;
                }

                if (crlfCount == 4)
                    break; //http request completed
            }

            //write http response
            string httpHeaders = "HTTP/1.1 200 OK\r\nDate: $DATE GMT\r\nServer: Apache\r\nKeep-Alive: timeout=15, max=100\r\nConnection: Keep-Alive\r\nContent-Type: application/octet-stream\r\n\r\n";

            httpHeaders = httpHeaders.Replace("$DATE", DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss"));
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(httpHeaders);

            s.Write(buffer, 0, buffer.Length);
            s.Flush();
        }

        private static void MakeDecoyHttpConnection(Stream s, IPEndPoint remotePeerEP)
        {
            //write http request
            string httpHeaders = "GET / HTTP/1.1\r\nHost: $HOST\r\nConnection: keep-alive\r\nUser-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/59.0.3071.115 Safari/537.36\r\nAccept: text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8\r\nAccept-Encoding: gzip, deflate\r\nAccept-Language: en-GB,en-US;q=0.8,en;q=0.6\r\n\r\n";

            httpHeaders = httpHeaders.Replace("$HOST", remotePeerEP.ToString());
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(httpHeaders);

            s.Write(buffer, 0, buffer.Length);
            s.Flush();

            //read http response
            int byteRead;
            int crlfCount = 0;

            while (true)
            {
                byteRead = s.ReadByte();
                switch (byteRead)
                {
                    case '\r':
                    case '\n':
                        crlfCount++;
                        break;

                    case -1:
                        throw new EndOfStreamException();

                    default:
                        crlfCount = 0;
                        break;
                }

                if (crlfCount == 4)
                    break; //http request completed
            }
        }

        internal void AcceptConnectionInitiateProtocol(Stream s, IPEndPoint remotePeerEP)
        {
            //read version
            int version = s.ReadByte();

            switch (version)
            {
                case -1:
                    throw new EndOfStreamException();

                case 0: //DHT TCP support switch
                    switch (remotePeerEP.AddressFamily)
                    {
                        case AddressFamily.InterNetwork:
                            _ipv4DhtNode.AcceptConnection(s, remotePeerEP.Address);
                            break;

                        case AddressFamily.InterNetworkV6:
                            _ipv6DhtNode.AcceptConnection(s, remotePeerEP.Address);
                            break;

                        default:
                            throw new NotSupportedException("AddressFamily not supported.");
                    }
                    break;

                case 1:
                    //read peer id
                    byte[] peerID = new byte[20];
                    OffsetStream.StreamRead(s, peerID, 0, 20);
                    BinaryNumber remotePeerID = new BinaryNumber(peerID);

                    //read service port
                    byte[] remoteServicePort = new byte[2];
                    OffsetStream.StreamRead(s, remoteServicePort, 0, 2);
                    remotePeerEP = new IPEndPoint(remotePeerEP.Address, BitConverter.ToUInt16(remoteServicePort, 0));

                    //add
                    Connection connection = AddConnection(s, remotePeerID, remotePeerEP);
                    if (connection != null)
                    {
                        //send ok
                        s.WriteByte(0); //signal ok
                        s.Write(_localPeerID.Number, 0, 20); //peer id
                        s.Flush();

                        //set stream timeout
                        s.WriteTimeout = WRITE_TIMEOUT;
                        s.ReadTimeout = READ_TIMEOUT;
                    }
                    else
                    {
                        //send cancel
                        s.WriteByte(1); //signal cancel
                        s.Flush();

                        throw new IOException("Cannot accept remote connection: duplicate connection detected.");
                    }

                    break;

                default:
                    throw new IOException("Cannot accept remote connection: protocol version not supported.");
            }
        }

        private Connection MakeConnectionInitiateProtocol(Stream s, IPEndPoint remotePeerEP)
        {
            try
            {
                //send request
                s.WriteByte(1); //version
                s.Write(_localPeerID.Number, 0, 20); //peer id
                s.Write(BitConverter.GetBytes(Convert.ToUInt16(_localPort)), 0, 2); //service port
                s.Flush();

                //read response
                int response = s.ReadByte();
                switch (response)
                {
                    case -1:
                        throw new EndOfStreamException();

                    case 0:
                        byte[] buffer = new byte[20];
                        OffsetStream.StreamRead(s, buffer, 0, 20);
                        BinaryNumber remotePeerID = new BinaryNumber(buffer);

                        Connection connection = AddConnection(s, remotePeerID, remotePeerEP);
                        if (connection == null)
                        {
                            //check for existing connection again!
                            Connection existingConnection = GetExistingConnection(remotePeerEP);
                            if (existingConnection != null)
                            {
                                s.Dispose();
                                return existingConnection;
                            }

                            throw new IOException("Cannot connect to remote peer: connection already exists.");
                        }

                        //set stream timeout
                        s.WriteTimeout = WRITE_TIMEOUT;
                        s.ReadTimeout = READ_TIMEOUT;

                        return connection;

                    default:
                        Thread.Sleep(500); //wait so that other thread gets time to add his connection in list so that this thread can pick same connection to proceed

                        //check for existing connection again!
                        {
                            Connection existingConnection = GetExistingConnection(remotePeerEP);
                            if (existingConnection != null)
                            {
                                s.Dispose();
                                return existingConnection;
                            }
                        }

                        throw new IOException("Cannot connect to remote peer: request rejected.");
                }
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }

        private void IPv4DhtSeedingTracker_DiscoveredPeers(TrackerManager sender, IEnumerable<IPEndPoint> peerEPs)
        {
            if (_ipv4DhtNode != null)
                _ipv4DhtNode.AddNode(peerEPs);
        }

        private void IPv6DhtSeedingTracker_DiscoveredPeers(TrackerManager sender, IEnumerable<IPEndPoint> peerEPs)
        {
            if (_ipv6DhtNode != null)
                _ipv6DhtNode.AddNode(peerEPs);
        }

        internal void ClientProfileProxyUpdated()
        {
            NetProxy proxy = _profile.Proxy;

            _ipv4DhtBootstrapTracker.Proxy = proxy;
            _ipv6DhtBootstrapTracker.Proxy = proxy;

            if (proxy != null)
            {
                //stop tcp relay for all networks since this client switched to proxy and can no longer provide tcp relay service
                TcpRelayService.StopAllTcpRelays();
            }
        }

        #endregion

        #region internet connectivity

        private void ConnectivityCheckTimerCallback(object state)
        {
            try
            {
                Thread t1 = new Thread(IPv4ConnectivityCheck);
                t1.IsBackground = true;
                t1.Start();

                Thread t2 = new Thread(IPv6ConnectivityCheck);
                t2.IsBackground = true;
                t2.Start();

                t1.Join();
                t2.Join();
            }
            finally
            {
                //schedule next check
                if (_connectivityCheckTimer != null)
                {
                    if (((_ipv4InternetStatus == InternetConnectivityStatus.NoInternetConnection) && (_ipv6InternetStatus == InternetConnectivityStatus.NoInternetConnection)) || (_upnpDeviceStatus == UPnPDeviceStatus.DeviceNotFound) || (_upnpDeviceStatus == UPnPDeviceStatus.PortForwardingFailed))
                        _connectivityCheckTimer.Change(10000, Timeout.Infinite);
                    else
                        _connectivityCheckTimer.Change(CONNECTIVITY_CHECK_TIMER_INTERVAL, Timeout.Infinite);
                }
            }
        }

        private void IPv4ConnectivityCheck()
        {
            if (_upnpDeviceStatus == UPnPDeviceStatus.Identifying)
                _upnpDevice = null;

            IPEndPoint oldExternalEP = this.IPv4ExternalEndPoint;
            InternetConnectivityStatus oldInternetStatus = _ipv4InternetStatus;
            InternetConnectivityStatus newInternetStatus = InternetConnectivityStatus.Identifying;
            UPnPDeviceStatus newUPnPStatus;
            NetworkInfo defaultNetworkInfo;

            if (_profile.EnableUPnP)
                newUPnPStatus = UPnPDeviceStatus.Identifying;
            else
                newUPnPStatus = UPnPDeviceStatus.Disabled;

            try
            {
                if (_profile.Proxy != null)
                {
                    switch (_profile.Proxy.Type)
                    {
                        case NetProxyType.Http:
                            newInternetStatus = InternetConnectivityStatus.HttpProxyInternetConnection;
                            break;

                        case NetProxyType.Socks5:
                            newInternetStatus = InternetConnectivityStatus.Socks5ProxyInternetConnection;
                            break;

                        default:
                            throw new NotSupportedException("Proxy type not supported.");
                    }

                    newUPnPStatus = UPnPDeviceStatus.Disabled;
                    return;
                }

                defaultNetworkInfo = NetUtilities.GetDefaultIPv4NetworkInfo();
                if (defaultNetworkInfo == null)
                {
                    //no internet available;
                    newInternetStatus = InternetConnectivityStatus.NoInternetConnection;
                    newUPnPStatus = UPnPDeviceStatus.Disabled;
                    return;
                }

                if (!NetUtilities.IsPrivateIP(defaultNetworkInfo.LocalIP))
                {
                    //public ip so, direct internet connection available
                    newInternetStatus = InternetConnectivityStatus.DirectInternetConnection;
                    newUPnPStatus = UPnPDeviceStatus.Disabled;
                    _ipv4LocalLiveIP = defaultNetworkInfo.LocalIP;
                    return;
                }
                else
                {
                    _ipv4LocalLiveIP = null;
                }

                if (newUPnPStatus == UPnPDeviceStatus.Disabled)
                {
                    newInternetStatus = InternetConnectivityStatus.NatOrFirewalledInternetConnection;
                    return;
                }

                //check for upnp device
                if ((_upnpDevice == null) || !_upnpDevice.LocalIP.Equals(defaultNetworkInfo.LocalIP) || (_upnpDeviceStatus == UPnPDeviceStatus.PortForwardingFailed))
                {
                    foreach (GatewayIPAddressInformation gateway in defaultNetworkInfo.Interface.GetIPProperties().GatewayAddresses)
                    {
                        if (gateway.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            try
                            {
                                InternetGatewayDevice[] upnpDevices = InternetGatewayDevice.Discover(defaultNetworkInfo.LocalIP, gateway.Address);
                                if (upnpDevices.Length > 0)
                                    _upnpDevice = upnpDevices[0];
                                else
                                    _upnpDevice = null;
                            }
                            catch (Exception ex)
                            {
                                Debug.Write("ConnectionManager.IPv4ConnectivityCheck", ex);
                            }

                            break;
                        }
                    }
                }

                if (_upnpDevice == null)
                {
                    newInternetStatus = InternetConnectivityStatus.NatOrFirewalledInternetConnection;
                    newUPnPStatus = UPnPDeviceStatus.DeviceNotFound;
                }
                else
                {
                    newInternetStatus = InternetConnectivityStatus.NatInternetConnectionViaUPnPRouter;

                    //find external ip from router
                    try
                    {
                        _upnpExternalIP = _upnpDevice.GetExternalIPAddress();

                        if (_upnpExternalIP.ToString() == "0.0.0.0")
                        {
                            newInternetStatus = InternetConnectivityStatus.NoInternetConnection;
                            newUPnPStatus = UPnPDeviceStatus.Disabled;
                            return; //external ip not available so no internet connection available
                        }
                        else if (NetUtilities.IsPrivateIP(_upnpExternalIP))
                        {
                            newUPnPStatus = UPnPDeviceStatus.ExternalIpPrivate;
                            return; //no use of doing port forwarding for private upnp ip address
                        }
                    }
                    catch
                    {
                        _upnpExternalIP = null;
                    }

                    //do upnp port forwarding for Bit Chat
                    if (_upnpDevice.ForwardPort(ProtocolType.Tcp, _localPort, new IPEndPoint(defaultNetworkInfo.LocalIP, _localPort), "Bit Chat", true))
                        newUPnPStatus = UPnPDeviceStatus.PortForwarded;
                    else
                        newUPnPStatus = UPnPDeviceStatus.PortForwardingFailed;
                }
            }
            catch (Exception ex)
            {
                Debug.Write("ConnectionManager.IPv4ConnectivityCheck", ex);
            }
            finally
            {
                try
                {
                    //validate change in status by performing tests
                    if ((oldInternetStatus != newInternetStatus) || !Equals(oldExternalEP, this.IPv4ExternalEndPoint))
                    {
                        switch (newInternetStatus)
                        {
                            case InternetConnectivityStatus.NoInternetConnection:
                                _ipv4LocalLiveIP = null;
                                _upnpExternalIP = null;
                                _ipv4ConnectivityCheckExternalEP = null;
                                break;

                            case InternetConnectivityStatus.HttpProxyInternetConnection:
                            case InternetConnectivityStatus.Socks5ProxyInternetConnection:
                                if (!_profile.Proxy.IsProxyAvailable())
                                    newInternetStatus = InternetConnectivityStatus.ProxyConnectionFailed;
                                else if (!WebUtilities.IsWebAccessible(null, _profile.Proxy, WebClientExNetworkType.IPv4Only, 10000, false))
                                    newInternetStatus = InternetConnectivityStatus.NoProxyInternetConnection;

                                _ipv4LocalLiveIP = null;
                                _upnpExternalIP = null;
                                _ipv4ConnectivityCheckExternalEP = null;
                                break;

                            default:
                                if (WebUtilities.IsWebAccessible(null, null, WebClientExNetworkType.IPv4Only, 10000, false))
                                {
                                    switch (newInternetStatus)
                                    {
                                        case InternetConnectivityStatus.DirectInternetConnection:
                                            if (!DoWebCheckIncomingConnection(_localPort))
                                                _ipv4LocalLiveIP = null;

                                            break;

                                        case InternetConnectivityStatus.NatOrFirewalledInternetConnection:
                                            if (!DoWebCheckIncomingConnection(_localPort))
                                                _ipv4ConnectivityCheckExternalEP = null;

                                            break;

                                        case InternetConnectivityStatus.NatInternetConnectionViaUPnPRouter:
                                            break;

                                        default:
                                            _ipv4LocalLiveIP = null;
                                            _upnpExternalIP = null;
                                            _ipv4ConnectivityCheckExternalEP = null;
                                            break;
                                    }
                                }
                                else
                                {
                                    newInternetStatus = InternetConnectivityStatus.NoInternetConnection;
                                    _ipv4LocalLiveIP = null;
                                    _upnpExternalIP = null;
                                    _ipv4ConnectivityCheckExternalEP = null;
                                }
                                break;
                        }
                    }

                    if ((newInternetStatus == InternetConnectivityStatus.NatInternetConnectionViaUPnPRouter) && (_upnpDeviceStatus != newUPnPStatus) && (newUPnPStatus == UPnPDeviceStatus.PortForwarded))
                    {
                        if (_upnpDeviceStatus == UPnPDeviceStatus.PortForwardedNotAccessible)
                        {
                            newUPnPStatus = UPnPDeviceStatus.PortForwardedNotAccessible;
                        }
                        else if (!DoWebCheckIncomingConnection(_localPort))
                        {
                            newUPnPStatus = UPnPDeviceStatus.PortForwardedNotAccessible;
                        }
                    }

                    //update status
                    _ipv4InternetStatus = newInternetStatus;
                    _upnpDeviceStatus = newUPnPStatus;

                    //do dht bootstrap via tracker
                    if (this.IPv4ExternalEndPoint == null)
                    {
                        //if no incoming connection possible
                        IPEndPoint[] dhtNodeEPs = _ipv4DhtNode.GetKClosestRandomNodeEPs();

                        if (dhtNodeEPs.Length == 0)
                        {
                            _ipv4DhtBootstrapTracker.LookupOnly = true;
                            _ipv4DhtBootstrapTracker.ScheduleUpdateNow(); //start finding dht nodes immediately
                        }
                        else
                        {
                            _ipv4DhtBootstrapTracker.StopTracking(); //we have enough dht nodes and can stop bootstrap tracker
                        }
                    }
                    else
                    {
                        _ipv4DhtBootstrapTracker.LookupOnly = false;

                        if (!_ipv4DhtBootstrapTracker.IsTrackerRunning)
                            _ipv4DhtBootstrapTracker.StartTracking(); //keep bootstrap tracker running for other peers to find dht bootstrap nodes
                    }

                    //update dht with local node external ep
                    if (!_ipv4DhtNode.UpdateLocalNodeEP(this.IPv4ExternalEndPoint))
                    {
                        if (oldInternetStatus != newInternetStatus)
                        {
                            switch (oldInternetStatus)
                            {
                                case InternetConnectivityStatus.NoInternetConnection:
                                case InternetConnectivityStatus.NoProxyInternetConnection:
                                case InternetConnectivityStatus.ProxyConnectionFailed:
                                    //force DHT to do health check since internet connection is available
                                    _ipv4DhtNode.ForceHealthCheck();
                                    break;
                            }
                        }
                    }

                    if ((oldInternetStatus != newInternetStatus) || (_upnpDeviceStatus != newUPnPStatus))
                        IPv4InternetConnectivityStatusChanged?.Invoke(this, EventArgs.Empty);
                }
                catch
                { }
            }
        }

        private void IPv6ConnectivityCheck()
        {
            IPEndPoint oldExternalEP = this.IPv6ExternalEndPoint;
            InternetConnectivityStatus oldInternetStatus = _ipv6InternetStatus;
            InternetConnectivityStatus newInternetStatus = InternetConnectivityStatus.Identifying;
            NetworkInfo defaultNetworkInfo;

            try
            {
                if (_profile.Proxy != null)
                {
                    switch (_profile.Proxy.Type)
                    {
                        case NetProxyType.Http:
                            newInternetStatus = InternetConnectivityStatus.HttpProxyInternetConnection;
                            break;

                        case NetProxyType.Socks5:
                            newInternetStatus = InternetConnectivityStatus.Socks5ProxyInternetConnection;
                            break;

                        default:
                            throw new NotSupportedException("Proxy type not supported.");
                    }
                }
                else
                {
                    defaultNetworkInfo = NetUtilities.GetDefaultIPv6NetworkInfo();
                    if (defaultNetworkInfo == null)
                    {
                        //no internet available;
                        newInternetStatus = InternetConnectivityStatus.NoInternetConnection;
                    }
                    else
                    {
                        if (NetUtilities.IsPublicIPv6(defaultNetworkInfo.LocalIP))
                        {
                            //public ip so, direct internet connection available
                            newInternetStatus = InternetConnectivityStatus.DirectInternetConnection;
                            _ipv6LocalLiveIP = defaultNetworkInfo.LocalIP;
                        }
                        else
                        {
                            newInternetStatus = InternetConnectivityStatus.NoInternetConnection;
                            _ipv6LocalLiveIP = null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Write("ConnectionManager.IPv6ConnectivityCheck", ex);
            }
            finally
            {
                try
                {
                    //validate change in status by performing tests
                    if ((oldInternetStatus != newInternetStatus) || !Equals(oldExternalEP, this.IPv6ExternalEndPoint))
                    {
                        switch (newInternetStatus)
                        {
                            case InternetConnectivityStatus.NoInternetConnection:
                                _ipv6LocalLiveIP = null;
                                break;

                            case InternetConnectivityStatus.HttpProxyInternetConnection:
                            case InternetConnectivityStatus.Socks5ProxyInternetConnection:
                                if (!_profile.Proxy.IsProxyAvailable())
                                    newInternetStatus = InternetConnectivityStatus.ProxyConnectionFailed;
                                else if (!WebUtilities.IsWebAccessible(null, _profile.Proxy, WebClientExNetworkType.IPv6Only, 10000, false))
                                    newInternetStatus = InternetConnectivityStatus.NoProxyInternetConnection;

                                _ipv6LocalLiveIP = null;
                                break;

                            default:
                                if (!WebUtilities.IsWebAccessible(null, null, WebClientExNetworkType.IPv6Only, 10000, false))
                                {
                                    newInternetStatus = InternetConnectivityStatus.NoInternetConnection;
                                    _ipv6LocalLiveIP = null;
                                }
                                break;
                        }
                    }

                    //update status
                    _ipv6InternetStatus = newInternetStatus;

                    //do dht bootstrap via tracker
                    if (this.IPv6ExternalEndPoint == null)
                    {
                        //if no incoming connection possible
                        IPEndPoint[] dhtNodeEPs = _ipv6DhtNode.GetKClosestRandomNodeEPs();

                        if (dhtNodeEPs.Length == 0)
                        {
                            _ipv6DhtBootstrapTracker.LookupOnly = true;
                            _ipv6DhtBootstrapTracker.ScheduleUpdateNow(); //start finding dht nodes immediately
                        }
                        else
                        {
                            _ipv6DhtBootstrapTracker.StopTracking(); //we have enough dht nodes and can stop bootstrap tracker
                        }
                    }
                    else
                    {
                        _ipv6DhtBootstrapTracker.LookupOnly = false;

                        if (!_ipv6DhtBootstrapTracker.IsTrackerRunning)
                            _ipv6DhtBootstrapTracker.StartTracking(); //keep bootstrap tracker running for other peers to find dht bootstrap nodes
                    }

                    //update dht with local node external ep
                    if (!_ipv6DhtNode.UpdateLocalNodeEP(this.IPv6ExternalEndPoint))
                    {
                        if (oldInternetStatus != newInternetStatus)
                        {
                            switch (oldInternetStatus)
                            {
                                case InternetConnectivityStatus.NoInternetConnection:
                                case InternetConnectivityStatus.NoProxyInternetConnection:
                                case InternetConnectivityStatus.ProxyConnectionFailed:
                                    //force DHT to do health check since internet connection is available
                                    _ipv6DhtNode.ForceHealthCheck();
                                    break;
                            }
                        }
                    }

                    if (oldInternetStatus != newInternetStatus)
                        IPv6InternetConnectivityStatusChanged?.Invoke(this, EventArgs.Empty);
                }
                catch
                { }
            }
        }

        private bool DoWebCheckIncomingConnection(int externalPort)
        {
            bool _webCheckError = false;
            bool _webCheckSuccess = false;

            try
            {
                using (WebClientEx client = new WebClientEx())
                {
                    client.NetworkType = WebClientExNetworkType.IPv4Only;
                    client.Proxy = _profile.Proxy;
                    client.QueryString.Add("port", externalPort.ToString());
                    client.Timeout = 10000;

                    using (Stream s = client.OpenRead(IPv4_CONNECTIVITY_CHECK_WEB_SERVICE))
                    {
                        _webCheckError = false;
                        _webCheckSuccess = (s.ReadByte() == 1);

                        if (_webCheckSuccess)
                            _ipv4ConnectivityCheckExternalEP = IPEndPointParser.Parse(s);
                        else
                            _ipv4ConnectivityCheckExternalEP = null;
                    }
                }
            }
            catch
            {
                _webCheckError = true;
                _webCheckSuccess = false;
                _ipv4ConnectivityCheckExternalEP = null;
            }

            return _webCheckSuccess || _webCheckError;
        }

        public void ReCheckConnectivity()
        {
            if (_ipv4InternetStatus != InternetConnectivityStatus.Identifying)
            {
                _ipv4InternetStatus = InternetConnectivityStatus.Identifying;
                _upnpDeviceStatus = UPnPDeviceStatus.Identifying;
                _ipv6InternetStatus = InternetConnectivityStatus.Identifying;

                _connectivityCheckTimer.Change(1000, Timeout.Infinite);
            }
        }

        #endregion

        #region public

        public Stream GetConnectionStream(IPEndPoint remoteNodeEP)
        {
            if (NetUtilities.IsIPv4MappedIPv6Address(remoteNodeEP.Address))
                remoteNodeEP = new IPEndPoint(NetUtilities.ConvertFromIPv4MappedIPv6Address(remoteNodeEP.Address), remoteNodeEP.Port);

            Stream s = new WriteBufferedStream(new NetworkStream(MakeTcpConnection(remoteNodeEP), true), 1024);

            MakeDecoyHttpConnection(s, remoteNodeEP);

            s.WriteByte(0); //DHT TCP support switch

            return s;
        }

        public Connection MakeConnection(IPEndPoint remotePeerEP)
        {
            if (NetUtilities.IsIPv4MappedIPv6Address(remotePeerEP.Address))
                remotePeerEP = new IPEndPoint(NetUtilities.ConvertFromIPv4MappedIPv6Address(remotePeerEP.Address), remotePeerEP.Port);

            //prevent multiple connection requests to same remote end-point
            lock (_makeConnectionList)
            {
                if (_makeConnectionList.ContainsKey(remotePeerEP))
                    throw new BitChatException("Connection attempt for end-point already in progress.");

                _makeConnectionList.Add(remotePeerEP, null);
            }

            try
            {
                //check if self
                switch (remotePeerEP.AddressFamily)
                {
                    case AddressFamily.InterNetwork:
                        if (remotePeerEP.Equals(this.IPv4ExternalEndPoint))
                            throw new IOException("Cannot connect to remote port: self connection.");

                        break;

                    case AddressFamily.InterNetworkV6:
                        if (remotePeerEP.Equals(this.IPv6ExternalEndPoint))
                            throw new IOException("Cannot connect to remote port: self connection.");

                        break;
                }

                //check existing connection
                Connection existingConnection = GetExistingConnection(remotePeerEP);
                if (existingConnection != null)
                    return existingConnection;

                try
                {
                    Socket socket = MakeTcpConnection(remotePeerEP);

                    try
                    {
                        Stream s = new WriteBufferedStream(new NetworkStream(socket, true), WRITE_BUFFERED_STREAM_SIZE);

                        MakeDecoyHttpConnection(s, remotePeerEP);

                        //set socket options
                        socket.SendBufferSize = SOCKET_SEND_BUFFER_SIZE;
                        socket.ReceiveBufferSize = SOCKET_RECV_BUFFER_SIZE;

                        return MakeConnectionInitiateProtocol(s, remotePeerEP);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
                catch (SocketException)
                {
                    //try virtual connection
                    return MakeVirtualConnection(remotePeerEP);
                }
            }
            finally
            {
                lock (_makeConnectionList)
                {
                    _makeConnectionList.Remove(remotePeerEP);
                }
            }
        }

        public Connection MakeVirtualConnection(Connection viaConnection, IPEndPoint remotePeerEP)
        {
            if (NetUtilities.IsIPv4MappedIPv6Address(remotePeerEP.Address))
                remotePeerEP = new IPEndPoint(NetUtilities.ConvertFromIPv4MappedIPv6Address(remotePeerEP.Address), remotePeerEP.Port);

            //prevent multiple virtual connection requests to same remote end-point
            lock (_makeVirtualConnectionList)
            {
                if (_makeVirtualConnectionList.ContainsKey(remotePeerEP))
                    throw new BitChatException("Connection attempt for end-point already in progress.");

                _makeVirtualConnectionList.Add(remotePeerEP, null);
            }

            try
            {
                //check if self
                switch (remotePeerEP.AddressFamily)
                {
                    case AddressFamily.InterNetwork:
                        if (remotePeerEP.Equals(this.IPv4ExternalEndPoint))
                            throw new IOException("Cannot connect to remote port: self connection.");

                        break;

                    case AddressFamily.InterNetworkV6:
                        if (remotePeerEP.Equals(this.IPv6ExternalEndPoint))
                            throw new IOException("Cannot connect to remote port: self connection.");

                        break;
                }

                //check existing connection
                Connection existingConnection = GetExistingConnection(remotePeerEP);
                if (existingConnection != null)
                    return existingConnection;

                //create tunnel via proxy peer
                Stream s = viaConnection.RequestProxyTunnel(remotePeerEP);

                //make new connection protocol begins
                return MakeConnectionInitiateProtocol(s, remotePeerEP);
            }
            finally
            {
                lock (_makeVirtualConnectionList)
                {
                    _makeVirtualConnectionList.Remove(remotePeerEP);
                }
            }
        }

        #endregion

        #region properties

        public BinaryNumber LocalPeerID
        { get { return _localPeerID; } }

        public int LocalPort
        { get { return _localPort; } }

        public InternetConnectivityStatus IPv4InternetStatus
        { get { return _ipv4InternetStatus; } }

        public InternetConnectivityStatus IPv6InternetStatus
        { get { return _ipv6InternetStatus; } }

        public UPnPDeviceStatus UPnPStatus
        { get { return _upnpDeviceStatus; } }

        public IPAddress UPnPDeviceIP
        {
            get
            {
                if (_upnpDevice == null)
                    return null;
                else
                    return _upnpDevice.DeviceIP;
            }
        }

        public IPAddress UPnPExternalIP
        {
            get
            {
                if (_ipv4InternetStatus == InternetConnectivityStatus.NatInternetConnectionViaUPnPRouter)
                    return _upnpExternalIP;
                else
                    return null;
            }
        }

        public IPEndPoint IPv4ExternalEndPoint
        {
            get
            {
                switch (_ipv4InternetStatus)
                {
                    case InternetConnectivityStatus.DirectInternetConnection:
                        if (_ipv4LocalLiveIP == null)
                            return null;
                        else
                            return new IPEndPoint(_ipv4LocalLiveIP, _localPort);

                    case InternetConnectivityStatus.NatInternetConnectionViaUPnPRouter:
                        switch (_upnpDeviceStatus)
                        {
                            case UPnPDeviceStatus.PortForwarded:
                                if (_upnpExternalIP == null)
                                    return null;
                                else
                                    return new IPEndPoint(_upnpExternalIP, _localPort);

                            default:
                                return null;
                        }

                    case InternetConnectivityStatus.Identifying:
                        return null;

                    default:
                        return _ipv4ConnectivityCheckExternalEP;
                }
            }
        }

        public IPEndPoint IPv6ExternalEndPoint
        {
            get
            {
                switch (_ipv6InternetStatus)
                {
                    case InternetConnectivityStatus.DirectInternetConnection:
                        if (_ipv6LocalLiveIP == null)
                            return null;
                        else
                            return new IPEndPoint(_ipv6LocalLiveIP, _localPort);

                    default:
                        return null;
                }
            }
        }

        public DhtNode IPv4DhtNode
        { get { return _ipv4DhtNode; } }

        public DhtNode IPv6DhtNode
        { get { return _ipv6DhtNode; } }

        public BitChatProfile Profile
        { get { return _profile; } }

        #endregion
    }
}
