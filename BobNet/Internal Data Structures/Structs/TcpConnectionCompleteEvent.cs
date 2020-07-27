using System.Net.Sockets;

namespace BobNet
{
	internal struct TcpConnectionCompleteEvent
	{
		public ConnectionResult Type;
		public SocketException Exception;
	}
}
