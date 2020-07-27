using System.Net;
using System.Net.Sockets;

namespace BobNet
{
	internal struct Packet
	{
		public SendMode SendMode;

		public byte[] Data;
		public IPEndPoint EP;
		public NetClient Client;
	}
}
