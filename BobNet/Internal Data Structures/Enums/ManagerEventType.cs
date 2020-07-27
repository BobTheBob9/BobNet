namespace BobNet
{
	internal enum ManagerEventType
	{
		//client ones
		TcpConnectionComplete,
		UdpHandshakeUpdate,
		DisconnectedSelf,
		ConnectionComplete,

		//server ones
		RecievedTcpConnection,
		RecievedLocalConnection,
		RecievedUdpHandshakeAttempt,
		ClientConnectionComplete,
		ClientDisconnected,

		//shared ones
		RecievedData
	}
}
