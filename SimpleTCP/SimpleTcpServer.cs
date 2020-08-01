using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SimpleTCP
{
    /// <summary>
    /// Simple Server Socket
    /// </summary>
    public class SimpleTcpServer
    {
        private Socket _listener;
        private byte _delimiter;
        private Encoding _encoder;
        private List<SocketConnection> _connectedClients = new List<SocketConnection>();

        /// <summary>
        /// Is Server Started
        /// </summary>
        /// <returns>Server Started</returns>
        public bool IsStarted { private set; get; } = false;

        /// <summary>
		/// Event Client Cconnected
        /// </summary>
        public event EventHandler<SocketConnection> ClientConnected;
		/// <summary>
		/// Event Client Disconnected (Connection lost / Disconnect)
		/// </summary>
        public event EventHandler<SocketConnection> ClientDisconnected;
		/// <summary>
		/// Event Receive Data with Delimiter from Client
		/// </summary>
        public event EventHandler<Message> DelimiterDataReceived;
		/// <summary>
		/// Event Receive Image Data from Client
		/// </summary>
        public event EventHandler<Message> ImageReceived;

        // Thread signal.  
        private ManualResetEvent acceptDone = new ManualResetEvent(false);

        internal bool QueueStop { get; set; }
        internal IPAddress IPAddress { get; private set; }
        internal int Port { get; private set; }
        internal int ConnectionTimeout { get; private set; }

        /// <summary>
        /// Get Count Clients Connected to Server
        /// </summary>
        /// <returns>Count Clients Connected</returns>
        public int ConnectedClientsCount { get { return _connectedClients.Count; } }
        /// <summary>
        /// Get List Clientes Connected to Server
        /// </summary>
        /// <returns>List of Clients</returns>
        public IEnumerable<SocketConnection> ConnectedClients { get { return _connectedClients; } }

        /// <summary>
        /// Delimiter send text data
        /// </summary>
        public byte LineDelimiter { get { return _delimiter; } }
        /// <summary>
        /// Delimiter between internal text data (ex: cmd|data1|data2)
        /// </summary>
        public char Commandlimiter { get; set; }
        /// <summary>
        /// Encoding for Data receive/sending (UTF8, ASCII)
        /// </summary>
        public Encoding StringEncoder { get { return _encoder; } }

        /// <summary>
        /// Create Server ready for Start/Stop
        /// </summary>
        /// <param name="ipAddress">IP Address</param>
        /// <param name="port">Port</param>
        /// <param name="timeout">Timeout</param>
        public SimpleTcpServer(IPAddress ipAddress, int port, int timeout)
        {
            _delimiter = 0x0A;
            Commandlimiter = '|';
            _encoder = System.Text.Encoding.UTF8;
            QueueStop = false;
            IPAddress = ipAddress;
            Port = port;
            ConnectionTimeout = timeout;
            IsStarted = false;
        }

        /// <summary>
        /// Create Server with auto-IP ready for Start/Stop
        /// </summary>
        /// <param name="port">Port</param>
        /// <param name="timeout">Timeout</param>
        public SimpleTcpServer(int port, int timeout)
        {
            _delimiter = 0x0A;
            Commandlimiter = '|';
            _encoder = System.Text.Encoding.UTF8;
            QueueStop = false;
            IPAddress = IPAddress.Any;
            Port = port;
            ConnectionTimeout = timeout;
            IsStarted = false;
        }

        #region Control Server

        /// <summary>
        /// Start Server
        /// </summary>
        public void Start()
        {
            if (QueueStop)
                Stop();
            // Start Server
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.ReceiveTimeout = ConnectionTimeout * 1000;
            _listener.SendTimeout = ConnectionTimeout * 1000;
            _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            SetSocketKeepAliveValues(_listener, ConnectionTimeout * 1000);
            _listener.Bind(new IPEndPoint(IPAddress, Port));
            _listener.Listen(0);
            IsStarted = true;

            AcceptConnections(); // Start to accept Clients
        }

        /// <summary>
        /// Stop Server
        /// </summary>
        public void Stop()
        {
            QueueStop = true; // Stop Accept Clients
            if (_listener != null)
            {
                foreach (SocketConnection client in ConnectedClients.ToArray())
                    Kick(client.connection); // Kick all Users
                _listener.Close(); // Close Connection
            }
            QueueStop = false; // Start Accept Clients
            IsStarted = false;
        }

        /// <summary>
        /// Kick All Innactive Client from the server
        /// </summary>
        public void KickInactivity()
        {
            foreach (SocketConnection client in ConnectedClients.ToArray())
                if (!IsClientConnected(client))
                    Kick(client.connection);
        }


        /// <summary>
        /// Kick Client IP from the server
        /// </summary>
        /// <param name="ip">IP Address String</param>
        public void Kick(string ip)
        {
            foreach (SocketConnection client in ConnectedClients.ToArray())
                if (((IPEndPoint)client.connection.RemoteEndPoint).Address.ToString() == ip)
                    Kick(client.connection);
        }

        /// <summary>
        /// Kick Client Socket from the server
        /// </summary>
        /// <param name="client">Client Socket</param>
        public void Kick(Socket client)
        {
            if (client == null) return;
            if (!client.Connected) return;
            if (ConnectedClients.SingleOrDefault(x => x.connection == client) != null)
            {
                client.Disconnect(false);
                NotifyClientDisconnected(client);
            }
        }

        #endregion

        #region Send Data

        // Send Data to Client
        private void Send(Socket client, byte[] data)
        {
            // Add Delimiter
            byte[] recBuf = new byte[data.Length + 1];
            Array.Copy(data, recBuf, data.Length);
            recBuf[data.Length] = LineDelimiter;
            // Send data
            client.Send(recBuf, 0, recBuf.Length, SocketFlags.None);
        }

        private void Send(string ip, byte[] data)
        {
            foreach (SocketConnection client in ConnectedClients.ToArray())
                if (((IPEndPoint)client.connection.RemoteEndPoint).Address.ToString() == ip)
                    Send(client.connection, data); // Send to ip
        }

        /// <summary>
        /// Send String Data to IP Client
        /// </summary>
        /// <param name="ip">IP Address String</param>
        /// <param name="data">Text Data</param>
        public void Send(string ip, string data)
        {
            if (string.IsNullOrEmpty(data)) { return; }
            // Encode Data
            Send(ip, StringEncoder.GetBytes(Convert.ToBase64String(_encoder.GetBytes(data))));
        }

        /// <summary>
        /// Send Command Text to IP Client
        /// </summary>
        /// <param name="ip">IP Address String</param>
		/// <param name="cmd">Command</param>
		/// <param name="args">Data with Command</param>
        public void SendCommand(string ip, string cmd, string[] args = null)
        {
            StringBuilder request = new StringBuilder();
            if (args != null)
                for (int i = 0; i < args.Length; i++)
                    request.Append(Commandlimiter.ToString() + args[i]);
            Send(ip, (cmd == string.Empty || cmd.Equals("")) ? request.ToString().Substring(1, request.Length) : cmd + request);
        }

        /// <summary>
        /// Send String Text to All Clients
        /// </summary>
        /// <param name="data">IP Address String</param>
        // Broadcast all Clients
        public void Broadcast(string data)
        {
            foreach (SocketConnection client in ConnectedClients.ToArray())
            {
                if (string.IsNullOrEmpty(data)) { return; }
                // Encode Data
                string base64 = Convert.ToBase64String(_encoder.GetBytes(data));
                if (data.LastOrDefault() != LineDelimiter)
                    Send(client.connection, StringEncoder.GetBytes(base64 + _encoder.GetString(new byte[] { LineDelimiter })));
                else
                    Send(client.connection, StringEncoder.GetBytes(base64));
            }
        }

        /// <summary>
        /// Send Command Text to All Clients
        /// </summary>
        /// <param name="cmd">Command</param>
		/// <param name="args">Data with Command</param>
        public void BroadcastCommand(string cmd, string[] args = null)
        {
            StringBuilder request = new StringBuilder();
            if (args != null)
                for (int i = 0; i < args.Length; i++)
                    request.Append(Commandlimiter.ToString() + args[i]);
            Broadcast((cmd == string.Empty || cmd.Equals("")) ? request.ToString().Substring(1, request.Length) : cmd + request);
        }

        #endregion

        #region Connection Callbacks

        private void AcceptConnections()
        {
            while (!QueueStop)
            {
                try
                {
                    acceptDone.Reset(); // Set the event to nonsignaled state.
                    // Start an asynchronous socket to listen for connections. 
                    _listener.BeginAccept(new AsyncCallback(AcceptCallback), _listener);
                    acceptDone.WaitOne();  // Wait until a connection is made before continuing.  
                }
                catch (ThreadInterruptedException) { break; }
            }
        }

        private void AcceptCallback(IAsyncResult ar)
        {
            acceptDone.Set(); // Signal the main thread to continue.  

            Socket handler = null;
            try
            {
                // Get the socket that handles the client request.
                handler = ((Socket)ar.AsyncState).EndAccept(ar);

                // Add new Client
                if (handler != null)
                {
                    // Set Keep Live
                    handler.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, 1);
                    SetSocketKeepAliveValues(handler, 20 * 1000);
                    /// Notify
                    NotifyClientConnected(handler);
                }

                // Start Receive Data
                SocketConnection connected = _connectedClients.SingleOrDefault(x => x.connection == handler);
                Message message = new Message(connected, StringEncoder, LineDelimiter, Commandlimiter);  // Create the Message object.
                handler.BeginReceive(message.Data, 0, Message.DataSize, SocketFlags.None, new AsyncCallback(ReadCallback), message);
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { NotifyClientDisconnected(handler); return; }
        }

        private void ReadCallback(IAsyncResult ar)
        {
            List<byte> bytesReceived = new List<byte>();

            // Retrieve the state object and the handler socket from the asynchronous state object.  
            Message message = (Message) ar.AsyncState;
            Socket handler = message.Client.connection;

            // Detect Disconnect Client
            if (handler == null) { NotifyClientDisconnected(handler); return; }
            if (!IsSocketConnected(handler)) {  NotifyClientDisconnected(handler); return; }

            try
            {
                int bytesRead = handler.EndReceive(ar); // Read data from the client socket.
                if (bytesRead > 0)
                {
                    // There  might be more data, so store the data received so far.  
                    byte[] recBuf = new byte[bytesRead];
                    Array.Copy(message.Data, recBuf, bytesRead);

                    if (IsImage(StringEncoder.GetString(recBuf)))
                    {
                        // Check for end-of-file tag. If it is not there, read more data.
                        if (message.MessageString.Contains("<CLOSED>"))
                        {
                            message.bytesReceived.AddRange(recBuf);
                            // Send Data to Action
                            byte[] msg = message.bytesReceived.ToArray();
                            message.bytesReceived.Clear();
                            NotifyImageMessageRx(handler, msg); // All the data has been read from the client. Call the action.
                            message.Data = new byte[Message.DataSize]; // Clear Trash data

                        } else
                        {
                            for (int i = 0; i < bytesRead; i++)
                            {
                                message.bytesReceived.AddRange(new byte[] { recBuf[i] });
                            }
                        }
                    }
                    else
                    {
                        // Clear Image data
                        if(IsImage(StringEncoder.GetString(message.bytesReceived.ToArray())))
                            message.bytesReceived.Clear();

                        // Extract Data
                        for (int i = 0; i < bytesRead; i++)
                        {
                            // Check for end-of-file tag. If it is not there, read more data.
                            if (recBuf[i] == _delimiter)
                            {
                                byte[] msg = message.bytesReceived.ToArray();
                                message.bytesReceived.Clear();
                                NotifyDelimiterMessageRx(handler, msg); // All the data has been read from the client. Call the action.
                                message.Data = new byte[Message.DataSize]; // Clear Trash data
                            }
                            else
                            {
                                if (recBuf[i] != 0)
                                    message.bytesReceived.AddRange(new byte[] { recBuf[i] });
                            }
                        }
                    }
                }
                // Continue receive data. Get more data.
                handler.BeginReceive(message.Data, 0, Message.DataSize, SocketFlags.None, new AsyncCallback(ReadCallback), message);
            }
            catch (ObjectDisposedException) { NotifyClientDisconnected(handler); return; }
            catch (SocketException) { NotifyClientDisconnected(handler); return; }
        }

        #endregion

        #region Util Methods

        internal bool IsSocketConnected(Socket client)
        {
            if (client == null) return false;
            try { return !((client.Poll(1000, SelectMode.SelectRead) && (client.Available == 0)) || !client.Connected); }
            catch { return false; }
        }

        internal bool IsClientConnected(SocketConnection client)
        {
            Socket socket = client.connection;
            try
            {
                socket.ReceiveTimeout = 15 * 1000; // Set 15seg Receive Timeout
                // Send Ping
                Send(socket, StringEncoder.GetBytes(Convert.ToBase64String(_encoder.GetBytes("ping"))));
                int bytesRead = socket.Receive(new byte[1], SocketFlags.Peek); // Receive Pong
                if (bytesRead == 0) return false;
            } catch { return false; }
            return true;
        }

        internal void SetSocketKeepAliveValues(Socket socket, int KeepAliveInterval)
        {
            int size = sizeof(UInt32);
            byte[] inArray = new byte[size * 3];  // 4 * 3 = 12
            Array.Copy(BitConverter.GetBytes(1), 0, inArray, 0, size);
            Array.Copy(BitConverter.GetBytes(KeepAliveInterval), 0, inArray, size, size);
            Array.Copy(BitConverter.GetBytes(1000), 0, inArray, size * 2, size);
            socket.IOControl(IOControlCode.KeepAliveValues, inArray, null);
        }

        internal bool IsImage(string str)
        {
            return str.Contains("�") || str.Contains("�") || str.Contains("�PNG");
        }

        /// <summary>
        /// Deserialize Command to receive from Server by commandDelimiter
        /// </summary>
        /// <param name="data">Data Text</param>
        /// <param name="split">Command Delimiter</param>
        /// <returns>String[] data</returns>
        public static string[] DeserializeCommand(string data, char split = '|')
        {
            return data.Split(split);
        }

        /// <summary>
        /// Get Mac from IPAddress
        /// </summary>
        /// <param name="address">IP Address</param>
        /// <returns>String Mac Address</returns>
        public string GetMacAddress(string address)
        {
            try
            {
                byte[] t = MacAddress(IPAddress.Parse(address));
                return string.Join(":", (from z in t select z.ToString("X2")).ToArray());
            }
            catch { }
            return string.Empty;
        }

        internal static byte[] MacAddress(IPAddress address)
        {
            byte[] mac = new byte[6];
            uint len = (uint)mac.Length;
            byte[] addressBytes = address.GetAddressBytes();
            uint dest = ((uint)addressBytes[3] << 24)
              + ((uint)addressBytes[2] << 16)
              + ((uint)addressBytes[1] << 8)
              + ((uint)addressBytes[0]);
            if (SendARP(dest, 0, mac, ref len) != 0)
            {
                throw new Exception("The ARP request failed.");
            }
            return mac;
        }

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        internal static extern int SendARP(uint destIP, uint srcIP, byte[] macAddress, ref uint macAddressLength);

        #endregion

        #region Notify Section

        internal void NotifyImageMessageRx(Socket client, byte[] msg)
        {
            if (ImageReceived != null)
            {
                ImageReceived(this, new Message(msg,
                    new SocketConnection() { connection = client, macAddress = _connectedClients.SingleOrDefault(x => x.connection == client).macAddress },
                    StringEncoder, LineDelimiter, Commandlimiter)
                );
            }
        }

        internal void NotifyDelimiterMessageRx(Socket client, byte[] msg)
        {
            // Detect Ping Command
            if (StringEncoder.GetString(Convert.FromBase64String(StringEncoder.GetString(msg))).Equals("pong"))
                return;

            if (DelimiterDataReceived != null)
            {
                DelimiterDataReceived(this, new Message(msg,
                    new SocketConnection() { connection = client, macAddress = _connectedClients.SingleOrDefault(x => x.connection == client).macAddress },
                    StringEncoder, LineDelimiter, Commandlimiter)
                );
            }
        }

        internal void NotifyClientConnected(Socket newClient)
        {
            string message;
            try
            {
                // Extract Mac Address
                byte[] buffer = new byte[17];
                newClient.Receive(buffer, 0, buffer.Length, 0);
                message = Encoding.UTF8.GetString(buffer);
            }
            catch { return; }

            SocketConnection connected = new SocketConnection() { connection = newClient, macAddress = message };
            _connectedClients.Add(connected); // Add new Client
            if (ClientConnected != null)
                ClientConnected(this, connected);
        }

        internal void NotifyClientDisconnected(Socket disconnectedClient)
        {
            if (disconnectedClient == null) return;
            // Remove disconnect Client
            SocketConnection client = _connectedClients.SingleOrDefault(x => x.connection == disconnectedClient);
            if(client != null)
            {
                _connectedClients.Remove(client);
                // Notify
                if (ClientDisconnected != null)
                    ClientDisconnected(this, client);
                try { client.connection.Shutdown(SocketShutdown.Both); client.connection.Close(); }
                catch { }
            }
        }

        #endregion
    }
}
