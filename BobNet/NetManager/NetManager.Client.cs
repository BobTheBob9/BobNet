using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace BobNet
{
	//NetManager client stuff
	public partial class NetManager
	{
		public ConnectionState ConnectionState 
		{ 
			get
			{
				if (Side != Side.Client)
					throw new IncorrectManagerStateException("The current connection state can only be accessed by client NetManagers");

				return _ConnectionState;
			}
			private set
			{
				_ConnectionState = value;
			}
		}
		public NetClient Server 
		{
			get
			{
				if (Side != Side.Client)
					throw new IncorrectManagerStateException("The connected server can only be accessed by client NetManagers");

				if (ConnectionState == ConnectionState.Unconnected)
					throw new IncorrectManagerStateException("The connected server cannot be accessed while not connected to a remote host");

				return _Clients[0];
			}
		}
		public int ConnectionTimeout 
		{
			get
			{
				if (Side != Side.Client)
					throw new IncorrectManagerStateException("The current connection timeout can only be accessed by client NetManagers");

				return _ConnectionTimeout;
			}
			set
			{
				if (Side != Side.Client)
					throw new IncorrectManagerStateException("The current connection timeout can only be accessedd by client NetManagers");

				if (ConnectionState != ConnectionState.Unconnected)
					throw new IncorrectManagerStateException("The current connection timeout can only be changed while not connected to a remote host");

				_ConnectionTimeout = value;
			}
		}
		public int HeartbeatSendRate 
		{ 
			get
			{
				if (Side != Side.Client)
					throw new IncorrectManagerStateException("The current heartbeat send rate can only be accessed by client NetManagers");

				return _HeartbeatSendRate;
			}
			set
			{
				if (Side != Side.Client)
					throw new IncorrectManagerStateException("The current heartbeat send rate can only be accessed by client NetManagers");

				_HeartbeatSendRate = value;
			}
		}

		public bool IsConnectedToLocalServer { get; internal set; }

		private TcpClient Tcp;
		private Guid ChannelSyncGuid;
		private byte[] UdpHandshakeGuid = null;
		private DateTime HeartbeatLastSend = DateTime.Now;
		private int InitialisationCount = 0;
		private NetClient LocalServersideClient;

		private ConnectionState _ConnectionState;
		private int _ConnectionTimeout = 5000;
		private int _HeartbeatSendRate = -1;

		public static NetManager CreateClient()
		{
			NetManager createdManager = new NetManager();
			createdManager.Udp = new UdpClient();
			createdManager.Side = Side.Client;

			//this could be done with a custom channel for serialization and validation and shit but what's even the point lol
			createdManager.Channels[UDP_HANDSHAKE_CHANNEL_INDEX].OnRecieveRaw += (byte[] data, NetClient sender) =>
				{
					if (data.Length != sizeof(byte) + 16)
						return;

					UdpHandshakeUpdateEvent updateEvent;
					if (data[0] == 0) //not completed
					{
						byte[] guidData = new byte[16];
						Array.Copy(data, 1, guidData, 0, guidData.Length);

						updateEvent = new UdpHandshakeUpdateEvent
						{
							Complete = false,
							GuidData = guidData
						};
					}
					else
						updateEvent = new UdpHandshakeUpdateEvent
						{
							Complete = true
						};

					ManagerEvent handshakeEvent = new ManagerEvent(ManagerEventType.UdpHandshakeUpdate, updateEvent);
					createdManager.EventQueue.Enqueue(handshakeEvent);
				};

			createdManager.Channels[DISCONNECT_CHANNEL_INDEX].OnRecieveRaw += 
				(byte[] data, NetClient sender) => createdManager.Disconnect(Encoding.Unicode.GetString(data));

			createdManager.Channels[CHANNEL_INDEX_SYNC_CHANNEL_INDEX].OnRecieveSerialized += (object objData, NetClient sender) =>
				{
					SyncableStringArray data = (SyncableStringArray)objData;

					if (data.ID != createdManager.ChannelSyncGuid)
						return;

					if (data.SendingIndexes)
					{
						for (int i = 0; i < createdManager.Channels.Count; i++)
							createdManager.Channels[i].ID = (ushort)data.Indexes[i];

						createdManager.IncrementInitialisationState();
					}
					else
					{
						foreach (SyncableStringArray.OptionalString channel in data.Items)
							createdManager.CreateChannel(channel.String);
					}
				};

			return createdManager;
		}

		public void Connect(IPEndPoint remoteEp)
		{
			if (Side != Side.Client)
				throw new IncorrectManagerStateException("Only Client NetManagers can connect to remote hosts");

			if (ConnectionState != ConnectionState.Unconnected)
				throw new IncorrectManagerStateException("Clients cannot connect to multiple hosts at once");

			Tcp = new TcpClient();

			IsConnectedToLocalServer = false;
			ConnectionState = ConnectionState.Connecting;
			UdpHandshakeGuid = null;
			InitialisationCount = 0;
			_Clients.Add(new NetClient
			{
				EP = remoteEp,
				Manager = this
			});

			Task.Run(() => AttemptAsyncConnection(remoteEp));
		}

		public void ConnectLocal(NetManager localServer)
		{
			if (Side != Side.Client)
				throw new IncorrectManagerStateException("Only Client NetManagers can connect to local hosts");

			if (ConnectionState != ConnectionState.Unconnected)
				throw new IncorrectManagerStateException("Clients cannot connect to multiple hosts at once");

			IsConnectedToLocalServer = true;
			ConnectionState = ConnectionState.Connecting;
			InitialisationCount = 1; //udp handshake already done so initcount is 1
			_Clients.Add(new NetClient
			{
				Manager = this,
				IsLocal = true,
				LocalManager = localServer
			});

			ManagerEvent serverConnectionEvent = new ManagerEvent(ManagerEventType.RecievedLocalConnection, this);
			ManagerEvent clientConnectionEvent = new ManagerEvent(
				ManagerEventType.TcpConnectionComplete,
				new TcpConnectionCompleteEvent
				{
					Type = ConnectionResult.Success,
					Exception = null
				}
			);

			localServer.EventQueue.Enqueue(serverConnectionEvent);
			EventQueue.Enqueue(clientConnectionEvent);
		}

		public void Disconnect(string reason = "")
		{
			if (Side != Side.Client)
				throw new IncorrectManagerStateException("Only client NetManagers can disconnect from remote hosts");

			//tell server that we're disconnecting
			byte[] encodedReason = Encoding.Unicode.GetBytes(reason);
			SendRaw(SendMode.Tcp, encodedReason, Channels[DISCONNECT_CHANNEL_INDEX], Server);

			CleanupAfterDisconnect();
		}

		private void CleanupAfterDisconnect()
		{
			ConnectionState = ConnectionState.Unconnected;
			IsConnectedToLocalServer = false;
			UdpHandshakeGuid = null;

			if (!Server.IsLocal)
			{
				Tcp.GetStream().Close();
				Tcp.Close();
			}

			_Clients.Clear();

			ManagerEvent disconnectEvent = new ManagerEvent(ManagerEventType.DisconnectedSelf);
			EventQueue.Enqueue(disconnectEvent);
		}

		private void IncrementInitialisationState()
		{
			if (InitialisationCount == INITIALISATION_COUNT_MAX)
				return;

			InitialisationCount++;
			if (InitialisationCount == INITIALISATION_COUNT_MAX)
				EventQueue.Enqueue(new ManagerEvent(ManagerEventType.ConnectionComplete));
		}

		private async void AttemptAsyncConnection(IPEndPoint remoteEp)
		{
			ManagerEvent? connectionEvent = null;

			try
			{
				Task connectionAttempt = Tcp.ConnectAsync(remoteEp.Address, remoteEp.Port);
				Task timeout = Task.Delay(ConnectionTimeout);

				if (await Task.WhenAny(connectionAttempt, timeout) == timeout)
				{
					//connection timed out
					//todo: check what happens if the connection succeeds post-timeout
					connectionEvent = new ManagerEvent(
						ManagerEventType.TcpConnectionComplete,
						new TcpConnectionCompleteEvent
						{
							Type = ConnectionResult.Timeout,
							Exception = null
						}
					);
				}
			}
			catch (SocketException sockEx)
			{
				connectionEvent = new ManagerEvent(
					ManagerEventType.TcpConnectionComplete,
					new TcpConnectionCompleteEvent
					{
						Type = ConnectionResult.Errored,
						Exception = sockEx
					}
				);
			}

			if (connectionEvent == null)
				connectionEvent = new ManagerEvent(
					ManagerEventType.TcpConnectionComplete,
					new TcpConnectionCompleteEvent
					{
						Type = ConnectionResult.Success,
						Exception = null
					}
				);

			EventQueue.Enqueue(connectionEvent.Value);
			return;
		}

		private async void ClientTcpListen()
		{
			NetworkStream clientStream = Tcp.GetStream();
			while (PollingNet)
			{
				await Task.Delay(50);
				if (!clientStream.DataAvailable)
					continue;

				byte[] lenData = new byte[sizeof(ushort)];
				int lenBytesRead = await clientStream.ReadAsync(lenData, 0, sizeof(ushort));

				if (lenBytesRead < sizeof(ushort)) //bad data
					continue;

				ushort len = BitConverter.ToUInt16(lenData, 0);

				byte[] data = new byte[len];
				int dataBytesRead = await clientStream.ReadAsync(data, 0, data.Length);

				if (dataBytesRead != len) //bad data
					continue;

				//data is fine! move it to the main thread
				Packet packet = new Packet
				{
					SendMode = SendMode.Tcp,
					Data = data
				};
				ManagerEvent dataEvent = new ManagerEvent(ManagerEventType.RecievedData, packet);
				EventQueue.Enqueue(dataEvent);
			}
		}

		private async void ClientUdpListen()
		{
			while (PollingNet)
			{
				await Task.Delay(50);
				if (Udp.Available == 0)
					continue;

				byte[] data = new byte[0];
				try
				{
					//todo: possibly check the ep here to see if it matches the host ep? unsure if necessary
					data = (await Udp.ReceiveAsync()).Buffer; 
				}
				catch (SocketException ex)
				{
					//deal with this later
				}

				if (data.Length < sizeof(ushort)) //bad data
					continue;

				//send data to main thread
				Packet packet = new Packet
				{
					SendMode = SendMode.Udp,
					Data = data
				};
				ManagerEvent dataEvent = new ManagerEvent(ManagerEventType.RecievedData, packet);
				EventQueue.Enqueue(dataEvent);
			}
		}
	}
}