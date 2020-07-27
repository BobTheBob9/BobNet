using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BobNet
{
	public struct SerializationResult<T>
	{
		public T Data;
		public bool ShouldSend;

		public SerializationResult(bool shouldSend, T data = default)
		{
			Data = data;
			ShouldSend = shouldSend;
		}
	}
}
