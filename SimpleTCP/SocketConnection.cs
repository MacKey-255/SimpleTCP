using System.Net;
using System.Net.Sockets;

namespace SimpleTCP
{
    /// <summary>
    /// Extra Socket Connection Class
    /// </summary>
    public class SocketConnection
    {
        /// <summary>
        /// Socket Connection
        /// </summary>
        public Socket connection { get; set; }
        /// <summary>
        /// Last 
        /// </summary>
        public long lastPing { get; set; }
        /// <summary>
        /// MacAddress of Client
        /// </summary>
        public string macAddress { get; set; }
        /// <summary>
        /// IP of Client
        /// </summary>
        public string ip { get { return ((IPEndPoint)connection.RemoteEndPoint).Address.ToString(); } }
    }
}
