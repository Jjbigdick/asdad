using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

public class NetworkManager : MonoBehaviour
{
    #region Configuration
    public int maxConnections = 10000;
    public int tcpPort = 10005;
    public int udpPort = 10006;
    public int bufferSize = 4096;
    public float updateInterval = 0.1f;
    #endregion

    #region Core Components
    private Socket tcpListener;
    private Socket udpListener;
    private Thread tcpAcceptThread;
    private Thread udpReceiveThread;
    private Thread processingThread;
    #endregion

    #region Network Queues
    private ConcurrentQueue<TcpConnection> newConnections = new();
    private ConcurrentQueue<NetworkMessage> receivedMessages = new();
    private ConcurrentQueue<NetworkMessage> sendQueue = new();
    private ConcurrentDictionary<IPEndPoint, UdpConnection> udpConnections = new();
    #endregion

    #region Connection Tracking
    private ConcurrentDictionary<Guid, TcpConnection> tcpConnections = new();
    private ConcurrentDictionary<IPEndPoint, Guid> endpointToGuid = new();
    #endregion

    void Start()
    {
        InitializeSockets();
        StartNetworkThreads();
        StartCoroutine(NetworkUpdateLoop());
    }

    private void InitializeSockets()
    {
        // TCP Listener
        tcpListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ReceiveBufferSize = bufferSize,
            SendBufferSize = bufferSize,
            Blocking = false
        };
        tcpListener.Bind(new IPEndPoint(IPAddress.Any, tcpPort));
        tcpListener.Listen(maxConnections);

        // UDP Listener
        udpListener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = bufferSize,
            SendBufferSize = bufferSize,
            Blocking = false
        };
        udpListener.Bind(new IPEndPoint(IPAddress.Any, udpPort));
    }

    private void StartNetworkThreads()
    {
        tcpAcceptThread = new Thread(TcpAcceptLoop)
        {
            Priority = System.Threading.ThreadPriority.Highest,
            IsBackground = true
        };
        tcpAcceptThread.Start();

        udpReceiveThread = new Thread(UdpReceiveLoop)
        {
            Priority = System.Threading.ThreadPriority.AboveNormal,
            IsBackground = true
        };
        udpReceiveThread.Start();

        processingThread = new Thread(ProcessNetworkEvents)
        {
            IsBackground = true
        };
        processingThread.Start();
    }

    private void TcpAcceptLoop()
    {
        while (true)
        {
            try
            {
                var client = tcpListener.Accept();
                var connection = new TcpConnection(client);
                newConnections.Enqueue(connection);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
            {
                Thread.Sleep(1);
            }
        }
    }

    private void UdpReceiveLoop()
    {
        byte[] buffer = new byte[bufferSize];
        EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

        while (true)
        {
            try
            {
                int received = udpListener.ReceiveFrom(buffer, ref remoteEP);
                var message = new NetworkMessage
                {
                    Endpoint = (IPEndPoint)remoteEP,
                    Data = new byte[received]
                };
                Buffer.BlockCopy(buffer, 0, message.Data, 0, received);
                receivedMessages.Enqueue(message);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
            {
                Thread.Sleep(1);
            }
        }
    }

    private System.Collections.IEnumerator NetworkUpdateLoop()
    {
        var wait = new WaitForSeconds(updateInterval);
        while (true)
        {
            ProcessIncomingTCP();
            ProcessUDPAssociations();
            SendBatchedMessages();
            yield return wait;
        }
    }

    private void ProcessNetworkEvents()
    {
        while (true)
        {
            // Process 1000 events per frame
            for (int i = 0; i < 1000; i++)
            {
                if (!receivedMessages.TryDequeue(out var message)) break;
                HandleNetworkMessage(message);
            }
            Thread.Sleep(1);
        }
    }

    private void HandleNetworkMessage(NetworkMessage message)
    {
        // Handle different message types
        switch (message.Type)
        {
            case MessageType.Heartbeat:
                HandleHeartbeat(message.Endpoint);
                break;
            case MessageType.PositionUpdate:
                HandlePositionUpdate(message);
                break;
            case MessageType.PlayerAction:
                HandlePlayerAction(message);
                break;
        }
    }

    private void SendBatchedMessages()
    {
        // Batch send position updates
        var batch = new BatchPositionUpdate();
        foreach (var connection in tcpConnections.Values)
        {
            if (connection.NeedsUpdate)
            {
                batch.Add(connection.PlayerData);
                connection.ResetUpdateFlag();
            }
        }
        SendUdpBroadcast(batch.Serialize());
    }

    public void SendUdpMessage(IPEndPoint endpoint, byte[] data)
    {
        sendQueue.Enqueue(new NetworkMessage
        {
            Endpoint = endpoint,
            Data = data
        });
    }

    private class TcpConnection
    {
        public Socket Socket { get; }
        public Guid Guid { get; } = Guid.NewGuid();
        public ServerPlayerData PlayerData { get; set; }
        public bool NeedsUpdate { get; private set; }

        public TcpConnection(Socket socket)
        {
            Socket = socket;
            BeginReceive();
        }

        private void BeginReceive()
        {
            var buffer = new byte[bufferSize];
            Socket.BeginReceive(buffer, 0, bufferSize, SocketFlags.None, ReceiveCallback, buffer);
        }

        private void ReceiveCallback(IAsyncResult ar)
        {
            int received = Socket.EndReceive(ar);
            if (received > 0)
            {
                ProcessReceivedData((byte[])ar.AsyncState, received);
                BeginReceive();
            }
        }

        private void ProcessReceivedData(byte[] buffer, int length)
        {
            // Process protocol buffers
            var message = MessageParser.ParseFrom(buffer, length);
            NeedsUpdate = true;
        }
    }

    private struct NetworkMessage
    {
        public IPEndPoint Endpoint;
        public byte[] Data;
        public MessageType Type;
    }

    private enum MessageType : byte
    {
        Heartbeat,
        PositionUpdate,
        PlayerAction
    }
}
