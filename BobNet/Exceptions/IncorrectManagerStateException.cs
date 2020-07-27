using System;

namespace BobNet
{
	public class IncorrectManagerStateException : Exception
	{
		public IncorrectManagerStateException() : base() { }
		public IncorrectManagerStateException(string message) : base(message) { }
		public IncorrectManagerStateException(string message, Exception innerException) : base(message, innerException) { }
	}
}
