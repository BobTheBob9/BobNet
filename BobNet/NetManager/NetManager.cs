using System;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace BobNet
{
	public partial class NetManager
	{
		internal const int INITIALISATION_COUNT_MAX = 2;

		private const int UDP_CHANNEL_SKIP_AMOUNT = 2; //skip processing handshake and unknownchannel channels
		private const int TCP_CHANNEL_SKIP_AMOUNT = 1; //skip processing unknown channels
		private const int UNKNOWN_CHANNEL_CHANNEL_INDEX = 0;
		private const int UDP_HANDSHAKE_CHANNEL_INDEX = 1;
		private const int DISCONNECT_CHANNEL_INDEX = 2;
		private const int HEARTBEAT_CHANNEL_INDEX = 3;
		private const int CHANNEL_INDEX_SYNC_CHANNEL_INDEX = 4;

		public Side Side { get; private set; }

		private ConcurrentQueue<ManagerEvent> EventQueue;
		private List<NetChannel> Channels;
		private List<NetClient> _Clients;
		private UdpClient Udp;

		private bool PollingNet;

		private NetManager()
		{
			ConnectionState = ConnectionState.Unconnected;
			EventQueue = new ConcurrentQueue<ManagerEvent>();
			Channels = new List<NetChannel>();
			_Clients = new List<NetClient>();
			PollingNet = false;

			//create hardcoded channels
			//we have to set the ids manually here so they function properly on clients
			CreateChannel("BobNet::UnknownChannel").ID = UNKNOWN_CHANNEL_CHANNEL_INDEX;
			CreateChannel("BobNet::UdpHandshake").ID = UDP_HANDSHAKE_CHANNEL_INDEX;
			CreateChannel("BobNet::Disconnect").ID = DISCONNECT_CHANNEL_INDEX;
			CreateChannel("BobNet::Heartbeat").ID = HEARTBEAT_CHANNEL_INDEX;
			CreateChannel("BobNet::ChannelIndexSyncronisation", new SyncableStringArrayChannel()).ID = CHANNEL_INDEX_SYNC_CHANNEL_INDEX;
		}

		public NetChannel CreateChannel(string id, NetChannel channel, bool failIfAlreadyExists = true)
		{
			if (Side == Side.Client && ConnectionState != ConnectionState.Unconnected)
				throw new IncorrectManagerStateException("Channels can only be created while not connected to a remote host");

			foreach (NetChannel c in Channels)
			{
				if (c.StringID == id)
				{
					if (failIfAlreadyExists)
						throw new IncorrectManagerStateException($"Channel \"{id}\" already exists and failIfAlreadyExists is true");
					
					return c;
				}
			}

			channel.Manager = this;
			channel.StringID = id;

			if (Side == Side.Server) //server ids should never change serverside and matter a good bit for channel sync, don't matter pre-connection clientside doe
				channel.ID = (ushort)Channels.Count;

			Channels.Add(channel);

			return channel;
		}

		public NetChannel CreateChannel(string id)
		{
			return CreateChannel(id, new NetChannel(), false);
		}

		public void SendRaw(SendMode sendMode, byte[] data, NetChannel channel, NetClient reciever)
		{
			if (reciever.IsLocal)
			{
				//faking a send to a local client by just directly queueing the event
				byte[] id = BitConverter.GetBytes(channel.ID);
				byte[] sentData = new byte[sizeof(ushort) + data.Length];

				Array.Copy(id, 0, sentData, 0, id.Length);
				Array.Copy(data, 0, sentData, 2, data.Length);

				//create recieve event for other manager
				Packet packet = new Packet
				{
					SendMode = SendMode.Tcp,
					Data = sentData,
				};

				if (Side == Side.Client)
					packet.Client = LocalServersideClient;

				ManagerEvent recieveEvent = new ManagerEvent(ManagerEventType.RecievedData, packet);

				//enqueue
				reciever.LocalManager.EventQueue.Enqueue(recieveEvent);
				return;
			}
			else if (sendMode == SendMode.Tcp)
			{
				//packet length first, then channel id, then data
				int length = sizeof(ushort) + data.Length;
				byte[] lengthEncoded = BitConverter.GetBytes((ushort)length);
				byte[] id = BitConverter.GetBytes(channel.ID);

				byte[] sentData = new byte[length + sizeof(ushort)];
				Array.Copy(lengthEncoded, 0, sentData, 0, lengthEncoded.Length);
				Array.Copy(id, 0, sentData, 2, id.Length);
				Array.Copy(data, 0, sentData, 4, data.Length);

				NetworkStream stream;
				if (Side == Side.Server)
					stream = reciever.Tcp.GetStream();
				else
					stream = Tcp.GetStream();

				stream.Write(sentData, 0, sentData.Length);
				stream.Flush();
			}
			else
			{
				//channel id, then data
				byte[] id = BitConverter.GetBytes(channel.ID);
				byte[] sentData = new byte[sizeof(ushort) + data.Length];

				Array.Copy(id, 0, sentData, 0, id.Length);
				Array.Copy(data, 0, sentData, 2, data.Length);

				Udp.Send(sentData, sentData.Length, reciever.EP);
			}
		}

		public NetEvent[] PollEvents()
		{
			List<NetEvent> events = new List<NetEvent>();

			#region Client Udp Handshake
			if (_ConnectionState == ConnectionState.Initialising && Side == Side.Client && UdpHandshakeGuid != null && !IsConnectedToLocalServer)
				SendRaw(SendMode.Udp, UdpHandshakeGuid, Channels[UDP_HANDSHAKE_CHANNEL_INDEX], Server);
			#endregion
			#region Client Heartbeat Sending
			if (_HeartbeatSendRate >= 0 && Side == Side.Client && DateTime.Now.Subtract(TimeSpan.FromMilliseconds(_HeartbeatSendRate)) > HeartbeatLastSend)
			{
				HeartbeatLastSend = DateTime.Now;
				SendRaw(SendMode.Udp, new byte[0], Channels[HEARTBEAT_CHANNEL_INDEX], Server);
			}
			#endregion

			#region Server Heartbeat Checking
			if (Side == Side.Server && _ClientTimeoutTime >= 0)
			{
				DateTime lastValidTime = DateTime.Now.Subtract(TimeSpan.FromMilliseconds(_ClientTimeoutTime));
				foreach (NetClient client in _Clients)
					if (lastValidTime > client.LastHeartbeat)
						DisconnectClient(client, "Client timed out");
			}
			#endregion

			while (EventQueue.TryDequeue(out ManagerEvent managerEvent))
			{
				switch (managerEvent.Type)
				{
					#region Client-only Events
					case ManagerEventType.TcpConnectionComplete:
						{
							var connectionEvent = (TcpConnectionCompleteEvent)managerEvent.Data;

							if (connectionEvent.Type == ConnectionResult.Success)
							{
								ConnectionState = ConnectionState.Initialising;

								//create channel index sync packet
								var channels = new SyncableStringArray.OptionalString[Channels.Count];
								for (int i = 0; i < Channels.Count; i++)
									channels[i] = new SyncableStringArray.OptionalString(Channels[i].StringID, Channels[i].Necessary);

								ChannelSyncGuid = Guid.NewGuid();

								PollingNet = true;
								Task.Run(ClientTcpListen);
								Task.Run(ClientUdpListen);

								//send
								SyncableStringArray syncArray = new SyncableStringArray(ChannelSyncGuid, channels);
								Channels[CHANNEL_INDEX_SYNC_CHANNEL_INDEX].SendSerialized(SendMode.Tcp, syncArray, Server);
							}
							else
							{
								ConnectionCompleteEvent failureEvent = new ConnectionCompleteEvent
								{
									Success = false,
									SocketErrorCode = 10060 //WSAETIMEDOUT
								};

								ConnectionState = ConnectionState.Unconnected;

								if (connectionEvent.Exception != null)
									failureEvent.SocketErrorCode = connectionEvent.Exception.ErrorCode;

								events.Add(failureEvent);

								CleanupAfterDisconnect();
							}

							break;
						}

					case ManagerEventType.UdpHandshakeUpdate:
						{
							if (ConnectionState != ConnectionState.Initialising)
								continue;

							var handshakeEvent = (UdpHandshakeUpdateEvent)managerEvent.Data;
							if (handshakeEvent.Complete)
							{
								//udp handshake successful, increment connection state
								IncrementInitialisationState();
								break;
							}

							//got the guid
							if (UdpHandshakeGuid == null)
								UdpHandshakeGuid = handshakeEvent.GuidData;

							break;
						}

					case ManagerEventType.ConnectionComplete:
						{
							ConnectionState = ConnectionState.Connected;

							ConnectionCompleteEvent successEvent = new ConnectionCompleteEvent
							{
								Success = true
							};
							events.Add(successEvent);
							break;
						}

					case ManagerEventType.DisconnectedSelf:
						{
							events.Add(new DisconnectedSelfEvent());
							break;
						}
					#endregion

					#region Server-only Events
					case ManagerEventType.RecievedTcpConnection:
						{
							TcpClient newTcp = (TcpClient)managerEvent.Data;

							//create udp handshake guid
							Guid handshakeGuid = Guid.NewGuid();

							NetClient newClient = new NetClient
							{
								Tcp = newTcp,
								UdpHandshakeGuid = handshakeGuid,
								Manager = this
							};
							_Clients.Add(newClient);

							byte[] handshakeGuidPacket = new byte[17]; // 1 + sizeof(Guid)
							handshakeGuidPacket[0] = 0;
							Array.Copy(handshakeGuid.ToByteArray(), 0, handshakeGuidPacket, 1, 16);

							SendRaw(SendMode.Tcp, handshakeGuidPacket, Channels[UDP_HANDSHAKE_CHANNEL_INDEX], newClient);

							Task.Run(() => ServerTcpListenToClient(newClient)); //start listening to client

							break;
						}

					case ManagerEventType.RecievedLocalConnection:
						{
							NetManager manager = (NetManager)managerEvent.Data;

							NetClient newClient = new NetClient
							{
								IsLocal = true,
								HasCompletedUdpHandshake = true,
								LocalManager = manager,
								InitialisationCount = 1,
								Manager = this
							};
							_Clients.Add(newClient);

							manager.LocalServersideClient = newClient;

							break;
						}

					case ManagerEventType.RecievedUdpHandshakeAttempt:
						{
							var handshakeAttempt = (UdpHandshakeAttemptEvent)managerEvent.Data;

							foreach (NetClient client in Clients)
							{
								if (!client.HasCompletedUdpHandshake && handshakeAttempt.Guid == client.UdpHandshakeGuid)
								{
									client.HasCompletedUdpHandshake = true;
									client.EP = handshakeAttempt.SenderEP;

									IncrementClientInitialisationCount(client);

									//create success packet
									byte[] successPacket = new byte[17]; //gotta pad to 17 or the client doesn't accept it
									successPacket[0] = 1;
									Array.Copy(new Guid().ToByteArray(), 0, successPacket, 1, 16);
									
									//notify client of success
									SendRaw(SendMode.Tcp, successPacket, Channels[UDP_HANDSHAKE_CHANNEL_INDEX], client);

									break;
								}
							}

							break;
						}

					case ManagerEventType.ClientConnectionComplete:
						{
							var connectedClient = (NetClient)managerEvent.Data;
							ClientConnectedEvent connectedEvent = new ClientConnectedEvent
							{
								ConnectedClient = connectedClient
							};
							events.Add(connectedEvent);
							break;
						}

					case ManagerEventType.ClientDisconnected:
						{
							var disconnectEvent = (TcpClientDisconnectEvent)managerEvent.Data;

							ClientDisconnectedEvent clientDisconnectedEvent = new ClientDisconnectedEvent
							{
								DisconnectedClient = disconnectEvent.Client,
								DisconnectReason = disconnectEvent.Reason
							};
							events.Add(clientDisconnectedEvent);
							break;
						}
					#endregion

					#region Shared Events
					case ManagerEventType.RecievedData:
						{
							Packet packet = (Packet)managerEvent.Data;
							byte[] data = packet.Data;
							ushort channelID = BitConverter.ToUInt16(data, 0);

							//todo: depending on how the order of channels lines up i might be able to do a direct lookup here
							NetChannel channel = null;
							for (int i = (packet.SendMode == SendMode.Tcp ? TCP_CHANNEL_SKIP_AMOUNT : UDP_CHANNEL_SKIP_AMOUNT); i < Channels.Count; i++) //skip any reserved channel indexes
							{
								if (Channels[i].ID == channelID)
								{
									channel = Channels[i];
									break;
								}
							}

							if (channel == null)
								break;

							byte[] headerless = new byte[data.Length - sizeof(ushort)];
							Array.Copy(data, 2, headerless, 0, headerless.Length);

							NetClient client = null;
							if (Side == Side.Client)
								//only possible sender is the server if we're a client
								client = Server;
							else
							{
								if (packet.SendMode == SendMode.Tcp)
									client = packet.Client;
								else
								{
									foreach (NetClient possibleClient in Clients)
									{
										if (possibleClient.EP?.Equals(packet.EP) ?? false) //null check to prevent nullrefs
										{
											client = possibleClient;
											break;
										}
									}
								}
							}

							if (client != null)
								channel.RecieveData(headerless, client);

							break;
						}
					#endregion
				}
			}

			return events.ToArray();
		}


	}
}