using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BobNet
{
	public class ConnectionCompleteEvent : NetEvent
	{
		public bool Success;
		public int SocketErrorCode = -1;
	}
}
