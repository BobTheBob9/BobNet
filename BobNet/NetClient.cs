using System;
using System.Net;
using System.Net.Sockets;

namespace BobNet
{
	public class NetClient
	{
		public bool IsLocal { get; internal set; } = false;
		public NetManager Manager { get; internal set; }
		public DateTime LastHeartbeat { get; internal set; } = DateTime.Now;

		internal bool PollingNet = true;
		internal bool HasCompletedUdpHandshake = false;
		internal Guid UdpHandshakeGuid;

		internal NetManager LocalManager;
		internal IPEndPoint EP;
		internal TcpClient Tcp;
		internal int InitialisationCount = 0;
		
		internal NetClient() { }
		
		public void Disconnect(string reason)
		{
			Manager.DisconnectClient(this, reason);
		}
	}
}