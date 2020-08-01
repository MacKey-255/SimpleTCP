using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SimpleTCP
{
    /// <summary>
    /// Message Communication
    /// </summary>
    public class Message
    {
        private Encoding _encoder = null;
        private byte _writeLineDelimiter;
        private char _commandDelimiter;

        /// <summary>
        /// Socket Client
        /// </summary>
        public SocketConnection Client { get; private set; }
        /// <summary>
        /// Data Receive on the Message
        /// </summary>
        public byte[] Data { get; set; }
        private ManualResetEvent sendDone = new ManualResetEvent(true);

        internal List<byte> bytesReceived { get; private set; }
        /// <summary>
        /// Data Size on the Message
        /// </summary>
        public const int DataSize = 1024;
        /// <summary>
        /// Data Receive in String Format
        /// </summary>
        public string MessageString {
            get
            {
                try {
                    return _encoder.GetString(Convert.FromBase64String(_encoder.GetString(Data)));
                } catch (FormatException)
                {
                    return _encoder.GetString(Data);
                }
                
            }
        }
        /// <summary>
        /// Data Receive in Command Deserializate Format
        /// </summary>
        public string[] MessageCommand {
            get
            {
                try {
                    return SimpleTcpClient.DeserializeCommand(_encoder.GetString(Convert.FromBase64String(_encoder.GetString(Data))), _commandDelimiter);
                } catch (Exception) {}
                return new string[] { MessageString };
            }
        }

        #region Initialize

        internal Message(SocketConnection tcpClient, Encoding stringEncoder, byte lineDelimiter, char cmdDelimiter)
        {
            Data = new byte[DataSize];
            Client = tcpClient;
            _encoder = stringEncoder;
            _writeLineDelimiter = lineDelimiter;
            _commandDelimiter = cmdDelimiter;
            bytesReceived = new List<byte>();
        }

        internal Message(byte[] data, SocketConnection tcpClient, Encoding stringEncoder, byte lineDelimiter, char cmdDelimiter)
        {
            Data = data;
            Client = tcpClient;
            _encoder = stringEncoder;
            _writeLineDelimiter = lineDelimiter;
            _commandDelimiter = cmdDelimiter;
            bytesReceived = new List<byte>();
        }

        #endregion

        #region Reply Section

        private void Reply(byte[] data)
        {
            if (Client == null) return;
            if (!Client.connection.Connected) return;
            sendDone.WaitOne();  // Wait until all data is sending before continuing. 
            Client.connection.Send(data, 0, data.Length, SocketFlags.None);
            Thread.Sleep(250);
            sendDone.Set();  // Signal the main thread to continue.
        }

        private void Reply(string data)
        {
            if (string.IsNullOrEmpty(data)) { return; }
            Reply(_encoder.GetBytes(data));
        }

        /// <summary>
        /// Reply Data Text
        /// </summary>
        /// <param name="data">Data Text</param>
        public void ReplyLine(string data)
        {
            if (string.IsNullOrEmpty(data)) { return; }
            // Encode Data
            string base64 = Convert.ToBase64String(_encoder.GetBytes(data));
            if (data.LastOrDefault() != _writeLineDelimiter)
                Reply(base64 + _encoder.GetString(new byte[] { _writeLineDelimiter }));
            else
                Reply(base64);
        }

        /// <summary>
        /// Reply Command Text
        /// </summary>
        /// <param name="cmd">Command</param>
        /// <param name="args">Data with Command</param>
        public void ReplyCommand(string cmd, string[] args = null)
        {
            StringBuilder request = new StringBuilder();
            if (args != null)
                for (int i = 0; i < args.Length; i++)
                    request.Append(_commandDelimiter.ToString() + args[i]);
            ReplyLine((cmd == string.Empty || cmd.Equals("")) ? request.ToString().Substring(1, request.Length) : cmd + request);
        }

        #endregion
    }
}
