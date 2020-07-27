using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BobNet
{
	public class ClientDisconnectedEvent : NetEvent
	{
		public string DisconnectReason;
		public NetClient DisconnectedClient;
	}
}
