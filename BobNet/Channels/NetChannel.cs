namespace BobNet
{
	public class NetChannel
	{
		public delegate void OnRecieveRawHandler(byte[] data, NetClient sender);
		public delegate void OnRecieveSerializedHandler(object data, NetClient sender);

		public event OnRecieveRawHandler OnRecieveRaw;
		public event OnRecieveSerializedHandler OnRecieveSerialized;

		public ushort ID { get; internal set; }
		public string StringID { get; internal set; }
		public NetManager Manager { get; internal set; }

		public bool Necessary = false;

		public void RecieveData(byte[] data, NetClient sender)
		{
			var result = DeserializeData(data, sender);
			if (result.ShouldSend)
			{
				OnRecieveRaw?.Invoke(data, sender);
				OnRecieveSerialized?.Invoke(result.Data, sender);
			}
		}

		public void SendRaw(SendMode sendMode, byte[] data, NetClient reciever)
		{
			Manager.SendRaw(sendMode, data, this, reciever);
		}

		public void SendSerialized(SendMode sendMode, object data, NetClient reciever)
		{
			var result = SerializeData(data, reciever);
			if (result.ShouldSend)
				Manager.SendRaw(sendMode, result.Data, this, reciever);
		}

		public virtual SerializationResult<byte[]> SerializeData(object data, NetClient client) 
		 => new SerializationResult<byte[]>(true, new byte[0]);
		public virtual SerializationResult<object> DeserializeData(byte[] data, NetClient client)
		 => new SerializationResult<object>(true, null);
	}
}
