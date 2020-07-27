using System;
using System.Net;

namespace BobNet
{
	internal struct UdpHandshakeAttemptEvent
	{
		public Guid Guid;
		public IPEndPoint SenderEP;
	}
}
