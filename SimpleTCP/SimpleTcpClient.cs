using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SimpleTCP
{
	/// <summary>
	/// Simple Client Socket
	/// </summary>
	public class SimpleTcpClient : IDisposable
	{
		private Thread _rxThread = null;
		private List<byte> _queuedMsg = new List<byte>();
		private Socket _client = null;

		/// <summary>
		/// Delimiter send text data
		/// </summary>
		public byte Delimiter { get; set; }
		/// <summary>
		/// Delimiter between internal text data (ex: cmd|data1|data2)
		/// </summary>
		public char Commandlimiter { get; set; }
		/// <summary>
		/// Encoding for Data receive/sending (UTF8, ASCII)
		/// </summary>
		public Encoding StringEncoder { get; set; }

		/// <summary>
		/// Event Receive Data (Server send any data)
		/// </summary>
		public event EventHandler<Message> ClientDataReceived;
		/// <summary>
		/// Event Client Disconnected (Connection lost / Disconnect)
		/// </summary>
		public event EventHandler<Socket> ClientDisconnected;

		// Thread signal.  
		private ManualResetEvent sendDone = new ManualResetEvent(true);

		internal bool QueueStop { get; set; }
		internal int ReadLoopIntervalMs { get; set; }

		/// <summary>
		/// Socket Client
		/// </summary>
		public Socket Client { get { return _client; } }

		/// <summary>
		/// Declare Client and Default Data (Encoder|Delimiters)
		/// </summary>
		public SimpleTcpClient()
		{
			StringEncoder = Encoding.UTF8;
			ReadLoopIntervalMs = 10;
			Delimiter = 0x0A;
			Commandlimiter = '|';
			QueueStop = false;
		}

		#region Connections Section

		/// <summary>
		/// Connect Client to Server
		/// </summary>
		/// <param name="hostNameOrIpAddress">Host</param>
		/// <param name="port">Port</param>
		/// <param name="timeout">Timeout</param>
		/// <returns>SimpleTCPClient</returns>
		public void Connect(string hostNameOrIpAddress, int port, int timeout)
		{
			// Connect
			QueueStop = false;
			_client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			_client.ReceiveTimeout = timeout * 1000;
			_client.SendTimeout = timeout * 1000;
			_client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
			SetSocketKeepAliveValues(timeout * 1000, 1000);

			// Try to connect.
			Stopwatch sw = new Stopwatch();
			TimeSpan timeOut = TimeSpan.FromSeconds(timeout);
			sw.Start();
			while (sw.Elapsed < timeOut)
			{
				try { _client.Connect(hostNameOrIpAddress, port); } catch { }
				if (IsSocketConnected(_client)) break;
				Thread.Sleep(500);
			}
			if (sw.Elapsed >= timeOut)
			{
				_client = null; return;
			}
			// Send Mac
			_client.Send(Encoding.UTF8.GetBytes(GetMacAddress()));

			// Start Receive
			StartRxThread();
		}

		/// <summary>
		/// Disconnect Client from Server
		/// </summary>
		/// <returns>SimpleTCPClient</returns>
		public void Disconnect()
		{
			if (_client == null) { return; }
			QueueStop = true;
			try
			{
				_client.Disconnect(false); // Disconnect Client
				_client.Shutdown(SocketShutdown.Both); // Shutdown Connection
				_client.Close(); // Close Objet
			}
			catch (SocketException) { }
			_client.Dispose();
			_client = null; // Reset Object
		}

		// Receive Data and Detect Disconnect Client
		private void StartRxThread()
		{
			if (_rxThread != null) { return; }

			_rxThread = new Thread(() => {
				try
				{
					// Listener Loop (Get data from Server)
					while (!QueueStop)
					{
						if (_client == null) { return; }
						try { RunLoopStep(); }
						catch { }
						Thread.Sleep(ReadLoopIntervalMs);
					}
					_rxThread = null;
				}
				catch (ThreadInterruptedException) { }
			});
			_rxThread.IsBackground = true;
			_rxThread.Start();
		}

		private void RunLoopStep()
		{
			if (_client == null) { return; }
			if (!IsSocketConnected(_client)) {
				NotifyClientDisconnected(_client);
				return;
			}

			var delimiter = this.Delimiter;
			var c = _client;

			int bytesAvailable = c.Available;
			if (bytesAvailable == 0)
			{
				Thread.Sleep(10);
				return;
			}

			List<byte> bytesReceived = new List<byte>();

			while (c.Available > 0 && c.Connected)
			{
				byte[] nextByte = new byte[1];
				c.Receive(nextByte, 0, 1, SocketFlags.None);
				bytesReceived.AddRange(nextByte);
				if (nextByte[0] == delimiter)
				{
					byte[] msg = _queuedMsg.ToArray();
					_queuedMsg.Clear();
					NotifyDelimiterMessage(c, msg);
				}
				else
				{
					_queuedMsg.AddRange(nextByte);
				}
			}
		}

		#endregion

		#region Send Data Section

		/// <summary>
		/// Send Image to Server
		/// </summary>
		/// <param name="path">File Path</param>
		public void WriteFile(string path)
		{
			if (_client == null) { return; }
			if (!IsSocketConnected(_client)) return;
			sendDone.WaitOne();  // Wait until all data is sending before continuing. 
			// Send Image
			_client.SendFile(path, null, StringEncoder.GetBytes("<CLOSED>"), TransmitFileOptions.UseDefaultWorkerThread);
			Thread.Sleep(250);
			sendDone.Set();  // Signal the main thread to continue.
		}

		private void Write(byte[] data)
		{
			if (_client == null) { return; }
			if (!IsSocketConnected(_client)) return;
			sendDone.WaitOne();  // Wait until all data is sending before continuing. 
			try
			{
				if (data.LastOrDefault() != Delimiter)
				{
					// Add Delimiter
					byte[] recBuf = new byte[data.Length + 1];
					Array.Copy(data, recBuf, data.Length);
					recBuf[data.Length] = Delimiter;
					_client.Send(recBuf, 0, recBuf.Length, SocketFlags.None);
				}
				else
				{
					_client.Send(data, 0, data.Length, SocketFlags.None);
				}
				Thread.Sleep(250);
			}
			catch (System.IO.IOException) { NotifyClientDisconnected(_client);  }
			catch (InvalidOperationException) { NotifyClientDisconnected(_client); }
			catch (SocketException) { NotifyClientDisconnected(_client); }
			sendDone.Set(); // Signal the main thread to continue.  
		}

		private void Write(string data)
		{
			if (string.IsNullOrEmpty(data)) { return; }
			Write(StringEncoder.GetBytes(data));
		}

		/// <summary>
		/// Send Text to Server
		/// </summary>
		/// <param name="data">Text to Send</param>
		public void WriteLine(string data)
		{
			if (string.IsNullOrEmpty(data)) { return; }
			// Encode Data
			string base64 = Convert.ToBase64String(StringEncoder.GetBytes(data));
			if (data.LastOrDefault() != Delimiter)
				Write(base64 + StringEncoder.GetString(new byte[] { Delimiter }));
			else
				Write(base64);
		}

		/// <summary>
		/// Send Command to Server
		/// </summary>
		/// <param name="cmd">Command</param>
		/// <param name="args">Data with Command</param>
		public void WriteCommand(string cmd, string[] args = null)
		{
			StringBuilder request = new StringBuilder();
			if (args != null)
				for (int i = 0; i < args.Length; i++)
					request.Append(Commandlimiter.ToString() + args[i]);
			WriteLine((string.IsNullOrEmpty(cmd) || cmd.Equals("")) ? request.ToString().Substring(1, request.Length) : cmd + request);
		}

		/// <summary>
		/// Send Text to Server and Wait Reply
		/// </summary>
		/// <param name="data">Text to Send</param>
		/// <param name="timeout">Timeout</param>
		/// <returns>Message</returns>
		public Message WriteLineAndGetReply(string data, TimeSpan timeout)
		{
			Message mReply = null;
			if (_client == null) return mReply;
			if (!IsSocketConnected(_client)) return mReply;
			this.ClientDataReceived += (s, m) => { mReply = m; };
			WriteLine(data);

			Stopwatch sw = new Stopwatch();
			sw.Start();

			while (mReply == null && sw.Elapsed < timeout)
				Thread.Sleep(10);

			this.ClientDataReceived -= (s, m) => { mReply = m; };
			return mReply;
		}

		/// <summary>
		/// Send Command to Server and Wait Reply
		/// </summary>
		/// <param name="cmd">Command</param>
		/// <param name="timeout">Timeout</param>
		/// <param name="args">Data with Command</param>
		/// <returns>Message</returns>
		public Message WriteCommandAndGetReply(string cmd, TimeSpan timeout, string[] args = null)
		{
			StringBuilder request = new StringBuilder();
			if (args != null)
				for (int i = 0; i < args.Length; i++)
					request.Append(Commandlimiter.ToString() + args[i]);
			string data = (string.IsNullOrEmpty(cmd) || cmd.Equals("")) ? request.ToString().Substring(1, request.Length) : cmd + request;
			return WriteLineAndGetReply(data, timeout);
		}

		#endregion
		
		#region Utils Methods

		internal bool IsSocketConnected(Socket client)
		{
			if (client == null) return false;
			if (!client.Connected) return false;
			if (client.Poll(0, SelectMode.SelectRead))
			{
				if (!client.Connected)
					return false;
				else
				{
					byte[] b = new byte[] { 0 };
					try
					{
						client.Send(b, 1, 0);
						if (client.Receive(b, SocketFlags.Peek) == 0)
							return false;
					}
					catch
					{
						return false;
					}
				}
			}
			return true;
		}

		internal void SetSocketKeepAliveValues(int KeepAliveTime, int KeepAliveInterval)
		{
			int size = Marshal.SizeOf(new uint());
			byte[] inOptionValues = new byte[size * 3]; // 4 * 3 = 12
			bool OnOff = true;

			BitConverter.GetBytes((uint)(OnOff ? 1 : 0)).CopyTo(inOptionValues, 0);
			BitConverter.GetBytes((uint)KeepAliveTime).CopyTo(inOptionValues, size);
			BitConverter.GetBytes((uint)KeepAliveInterval).CopyTo(inOptionValues, size * 2);

			_client.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null);
		}

		/// <summary>
		/// Get Mac Address to Client
		/// </summary>
		/// <returns>Mac Address</returns>
		public static string GetMacAddress()
		{
			try
			{
				char[] macAddr = (from nic in NetworkInterface.GetAllNetworkInterfaces()
							   where nic.OperationalStatus == OperationalStatus.Up
							   select nic.GetPhysicalAddress().ToString()).FirstOrDefault().ToCharArray();
				StringBuilder build = new StringBuilder();
				for(int i = 0; i<macAddr.Length; i++)
				{
					if(i != 0 && i%2 == 0)
						build.Append(':');
					build.Append(macAddr[i]);
				}
				return build.ToString();
			}
			catch { return string.Empty; }
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

		#endregion

		#region Notify Section

		internal void NotifyClientDisconnected(Socket disconnectedClient)
		{
			Disconnect();
			if (ClientDisconnected != null)
				ClientDisconnected(this, disconnectedClient);
		}

		internal void NotifyDelimiterMessage(Socket client, byte[] msg)
		{
			if (ClientDataReceived != null)
				ClientDataReceived(this, new Message(msg, new SocketConnection() { connection = client, macAddress = string.Empty }, StringEncoder, Delimiter, Commandlimiter));
		}

        #endregion

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

		/// <summary>
		/// Clear Object
		/// </summary>
		protected virtual void Dispose(bool disposing)
		{
			if (!disposedValue)
			{
				if (disposing)
				{
					// TODO: dispose managed state (managed objects).

				}

				// TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
				// TODO: set large fields to null.
				QueueStop = true;
				if (_client != null)
				{
					try
					{
						_client.Close();
					}
					catch { }
					_client = null;
				}

				disposedValue = true;
			}
		}

		/// <summary>
		/// Clear Object
		/// </summary>
		public void Dispose()
		{
			// Do not change this code. Put cleanup code in Dispose(bool disposing) above.
			Dispose(true);
			// TODO: uncomment the following line if the finalizer is overridden above.
			// GC.SuppressFinalize(this);
		}
		#endregion
	}
}