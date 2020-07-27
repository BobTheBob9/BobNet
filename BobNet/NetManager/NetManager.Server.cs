using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Collections.Generic;

namespace BobNet
{
	//NetManager server stuff
	public partial class NetManager
	{
		public ReadOnlyCollection<NetClient> Clients 
		{
			get
			{
				if (Side != Side.Server)
					throw new IncorrectManagerStateException("Clients can only be accessed by server NetManagers");

				return _Clients.AsReadOnly();
			}
		}
		public int ClientTimeoutTime 
		{
			get
			{
				if (Side != Side.Server)
					throw new IncorrectManagerStateException("Only server NetManagers can access the current client timeout time");

				return _ClientTimeoutTime;
			}
			set
			{
				if (Side != Side.Server)
					throw new IncorrectManagerStateException("Only server NetManagers can access the current client timeout time");

				_ClientTimeoutTime = value;
			}
		}
		public int ListenPort 
		{
			get
			{
				if (Side != Side.Server)
					throw new IncorrectManagerStateException("The server's listening port can only be accessed by server NetMangers");

				return _ListenPort;
			}
			private set
			{
				_ListenPort = value;
			}
		}

		private TcpListener TcpListener;
		private int _ListenPort;
		private int _ClientTimeoutTime = 10000;

		public static NetManager CreateServer(int port)
		{
			NetManager createdManager = new NetManager();
			createdManager.Side = Side.Server;
			createdManager.ListenPort = port;
			createdManager.Udp = new UdpClient(port);

			IPEndPoint localEP = new IPEndPoint(IPAddress.Parse("127.0.0.1"), port);
			createdManager.TcpListener = new TcpListener(localEP);

			//this could be done with a custom channel for serialization and shit but what's even the point lol
			createdManager.Channels[DISCONNECT_CHANNEL_INDEX].OnRecieveRaw +=
				(byte[] data, NetClient sender) => createdManager.DisconnectClient(sender, Encoding.Unicode.GetString(data));

			createdManager.Channels[HEARTBEAT_CHANNEL_INDEX].OnRecieveRaw += (byte[] data, NetClient sender) => 
				{
					sender.LastHeartbeat = DateTime.Now;
				};

			createdManager.Channels[CHANNEL_INDEX_SYNC_CHANNEL_INDEX].OnRecieveSerialized += (object objData, NetClient sender) =>
				{
					SyncableStringArray data = (SyncableStringArray)objData;

					if (data.SendingIndexes)
						return;

					int[] indexes = new int[data.Items.Length];
					for (int i = 0; i < data.Items.Length; i++)
					{
						NetChannel foundChannel = null;
						foreach (NetChannel channel in createdManager.Channels)
							if (channel.StringID == data.Items[i].String)
							{
								foundChannel = channel;
								break;
							}

						if (foundChannel == null)
							if (data.Items[i].Necessary)
							{
								createdManager.DisconnectClient(sender, $"Server is missing client's required channel: {data.Items[i].String}");
								return;
							}
							else
							{
								indexes[i] = UNKNOWN_CHANNEL_CHANNEL_INDEX; 
								continue;
							}

						//put index of channel into index array
						indexes[i] = foundChannel.ID;
					}

					List<NetChannel> serverChannelsNotOnClient = new List<NetChannel>();
					foreach (NetChannel channel in createdManager.Channels)
						if (Array.IndexOf(indexes, channel.ID) == -1)
						{
							if (channel.Necessary)
							{
								createdManager.DisconnectClient(sender, $"Client is missing server's required channel: {channel.StringID}");
								return;
							}
							else
								serverChannelsNotOnClient.Add(channel);
						}

					if (serverChannelsNotOnClient.Count != 0)
					{
						//create packet containing new channels
						var channelsNotOnClientNames = new SyncableStringArray.OptionalString[serverChannelsNotOnClient.Count];
						for (int i = 0; i < serverChannelsNotOnClient.Count; i++)
							channelsNotOnClientNames[i] = new SyncableStringArray.OptionalString(serverChannelsNotOnClient[i].StringID, true);

						SyncableStringArray missingChannels = new SyncableStringArray(data.ID, channelsNotOnClientNames);
						createdManager.Channels[CHANNEL_INDEX_SYNC_CHANNEL_INDEX].SendSerialized(SendMode.Tcp, missingChannels, sender);

						int oldLength = indexes.Length;
						Array.Resize(ref indexes, indexes.Length + serverChannelsNotOnClient.Count);
						for (int i = oldLength; i < indexes.Length; i++)
							indexes[i] = serverChannelsNotOnClient[i - oldLength].ID;
					}

					var indexPacket = new SyncableStringArray(data.ID, indexes);
					createdManager.Channels[CHANNEL_INDEX_SYNC_CHANNEL_INDEX].SendSerialized(SendMode.Tcp, indexPacket, sender);

					createdManager.IncrementClientInitialisationCount(sender);
				};


			return createdManager;
		}

		public void StartServer()
		{
			PollingNet = true;

			TcpListener.Start();

			Task.Run(ServerTcpListenForClients);
			Task.Run(ServerUdpListen);
		}

		public void DisconnectClient(NetClient client, string reason)
		{
			if (Side != Side.Server)
				throw new IncorrectManagerStateException("Only server NetManagers can disconnect clients");

			//this packet should cause a client to disconnect voluntarily, but CleanupDisconnectedClient will forcibly do it if they don't
			byte[] encodedReason = Encoding.Unicode.GetBytes(reason);
			SendRaw(SendMode.Tcp, encodedReason, Channels[DISCONNECT_CHANNEL_INDEX], client);

			CleanupDisconnectedClient(client, reason);
		}

		private void CleanupDisconnectedClient(NetClient client, string reason)
		{
			_Clients.Remove(client);
			client.PollingNet = false;

			if (!client.IsLocal)
			{
				client.Tcp.GetStream().Close();
				client.Tcp.Close();
			}

			TcpClientDisconnectEvent tcpDisconnectEvent = new TcpClientDisconnectEvent
			{
				Client = client,
				Reason = reason
			};

			ManagerEvent disconnectEvent = new ManagerEvent(ManagerEventType.ClientDisconnected, tcpDisconnectEvent);
			EventQueue.Enqueue(disconnectEvent);
		}

		private void IncrementClientInitialisationCount(NetClient client)
		{
			if (client.InitialisationCount == INITIALISATION_COUNT_MAX)
				return;

			client.InitialisationCount++;
			if (client.InitialisationCount == INITIALISATION_COUNT_MAX)
				EventQueue.Enqueue(new ManagerEvent(ManagerEventType.ClientConnectionComplete, client));
		}

		private async void ServerTcpListenForClients()
		{
			while (PollingNet)
			{
				await Task.Delay(250);
				if (!TcpListener.Pending())
					continue;
				
				//todo: make this cancel properly
				TcpClient newClient = await TcpListener.AcceptTcpClientAsync();

				ManagerEvent connectionEvent = new ManagerEvent(ManagerEventType.RecievedTcpConnection, newClient);
				EventQueue.Enqueue(connectionEvent);
			}
		}

		private async void ServerTcpListenToClient(NetClient client)
		{
			NetworkStream clientStream = client.Tcp.GetStream();
			while (client.PollingNet) //todo: make sure this dies right
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
					Data = data,
					Client = client
				};
				ManagerEvent dataEvent = new ManagerEvent(ManagerEventType.RecievedData, packet);
				EventQueue.Enqueue(dataEvent);
			}
		}
		
		private async void ServerUdpListen()
		{
			while (PollingNet)
			{
				//await Task.Delay(50);
				//if (Udp.Available == 0)
				//	continue;

				IPEndPoint ep = null;
				byte[] data = new byte[0];

				try
				{
					UdpReceiveResult asyncResult = await Udp.ReceiveAsync();
					ep = asyncResult.RemoteEndPoint;
					data = asyncResult.Buffer;
				}
				catch (SocketException ex)
				{
					//todo deal with this
				}

				if (data.Length < sizeof(ushort)) //bad data
					continue;

				//check if it's a udp handshake packet and emit the necessary event if it is
				//this has to be done here since we need the client's EP before it's already been resolved
				ushort channel = BitConverter.ToUInt16(data, 0);
				if (channel == UDP_HANDSHAKE_CHANNEL_INDEX)
				{
					if (data.Length != sizeof(ushort) + 16 /*sizeof(Guid) isn't possible*/)
						continue;

					//create guid from bytes
					byte[] guidArray = new byte[16];
					Array.Copy(data, 2, guidArray, 0, guidArray.Length);

					Guid guid = new Guid(guidArray);

					//create event
					UdpHandshakeAttemptEvent attemptEvent = new UdpHandshakeAttemptEvent
					{ 
						Guid = guid,
						SenderEP = ep
					};

					ManagerEvent guidEvent = new ManagerEvent(ManagerEventType.RecievedUdpHandshakeAttempt, attemptEvent);
					EventQueue.Enqueue(guidEvent);

					continue;
				}

				Packet packet = new Packet
				{
					SendMode = SendMode.Udp,
					Data = data,
					EP = ep
				};
				ManagerEvent dataEvent = new ManagerEvent(ManagerEventType.RecievedData, packet);
				EventQueue.Enqueue(dataEvent);
			}
		}
	}
}