namespace BobNet
{
	internal struct ManagerEvent
	{
		public object Data;
		public ManagerEventType Type;

		public ManagerEvent(ManagerEventType type, object data = null)
		{
			Type = type;
			Data = data;
		}
	}
}