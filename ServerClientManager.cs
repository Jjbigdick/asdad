using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

public class NetworkManager
{
    public class TCPConnector
    {
        public Guid TCPServerGUID { get; } = Guid.NewGuid();
        public IPEndPoint RemoteEndPoint { get; }
        public string Session { get; set; }
        public IPEndPoint UdpEndpoint { get; set; }
        public Socket Socket { get; }
        public ServerPlayerData ServerPlayerData { get; set; }

        private readonly object bufferLock = new object();
        private readonly byte[] receiveBuffer = new byte[4096];
        private readonly List<byte> messageBuffer = new List<byte>();

        public TCPConnector(Socket socket)
        {
            Socket = socket;
            RemoteEndPoint = (IPEndPoint)socket.RemoteEndPoint;
        }

        public void AppendData(byte[] data)
        {
            lock (bufferLock) { messageBuffer.AddRange(data); }
        }

        public byte[] GetMessageData()
        {
            lock (bufferLock)
            {
                var data = messageBuffer.ToArray();
                messageBuffer.Clear();
                return data;
            }
        }
    }

    // Configuration
    private const int UdpBufferPoolSize = 200;
    private const int ConcurrentUdpReceives = 50;
    private const int SocketBufferSize = 4 * 1024 * 1024;

    // TCP components
    private readonly Socket tcpSocket;
    private readonly Semaphore tcpSemaphore;

    // UDP components
    private readonly Socket udpSocket;
    private readonly ConcurrentStack<byte[]> udpBufferPool = new ConcurrentStack<byte[]>();

    // Network events
    public event Action<TCPConnector> TCPConnected = delegate { };
    public event Action<TCPConnector> TCPDisconnected = delegate { };
    public event Action<TCPConnector, byte[]> TCPMessageReceived = delegate { };
    public event Action<IPEndPoint, byte[]> UDPMessageReceived = delegate { };

    public NetworkManager(int tcpPort, int udpPort, int maxConnections)
    {
        // TCP initialization
        tcpSemaphore = new Semaphore(maxConnections, maxConnections);
        tcpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        tcpSocket.Bind(new IPEndPoint(IPAddress.Any, tcpPort));
        tcpSocket.Listen(1000);
        tcpSocket.BeginAccept(TcpAcceptCallback, null);

        // UDP initialization
        udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udpSocket.Bind(new IPEndPoint(IPAddress.Any, udpPort));
        udpSocket.ReceiveBufferSize = SocketBufferSize;
        udpSocket.SendBufferSize = SocketBufferSize;

        // Initialize buffer pool
        for (int i = 0; i < UdpBufferPoolSize; i++)
            udpBufferPool.Push(new byte[4096]);

        // Start multiple concurrent receives
        for (int i = 0; i < ConcurrentUdpReceives; i++)
            StartUdpReceive();
    }

    #region TCP Implementation
    private void TcpAcceptCallback(IAsyncResult asyncResult)
    {
        tcpSemaphore.WaitOne();
        try
        {
            Socket clientSocket = tcpSocket.EndAccept(asyncResult);
            var connector = new TCPConnector(clientSocket);
            TCPConnected(connector);
            clientSocket.BeginReceive(connector.receiveBuffer, 0, connector.receiveBuffer.Length,
                SocketFlags.None, TcpReceiveCallback, connector);
        }
        finally
        {
            tcpSocket.BeginAccept(TcpAcceptCallback, null);
        }
    }

    private void TcpReceiveCallback(IAsyncResult asyncResult)
    {
        var TCPconnector = (TCPConnector)asyncResult.AsyncState;
        try
        {
            int bytesRead = TCPconnector.Socket.EndReceive(asyncResult);
            if (bytesRead > 0)
            {
                TCPconnector.AppendData(TCPconnector.receiveBuffer[..bytesRead]);
                ProcessTcpBuffer(TCPconnector);
                TCPconnector.Socket.BeginReceive(TCPconnector.receiveBuffer, 0, TCPconnector.receiveBuffer.Length,
                    SocketFlags.None, TcpReceiveCallback, TCPconnector);
            }
            else
            {
                TCPDisconnectClient(TCPconnector);
            }
        }
        catch
        {
            TCPDisconnectClient(TCPconnector);
        }
    }

    private void ProcessTcpBuffer(TCPConnector TCPconnector)
    {
        var data = TCPconnector.GetMessageData();
        TCPMessageReceived(TCPconnector, data);
    }

    public void TCPSend(TCPConnector TCPconnector, byte[] data)
    {
        try
        {
            TCPconnector.Socket.BeginSend(data, 0, data.Length,
                SocketFlags.None, asyncResult =>
                {
                    try { TCPconnector.Socket.EndSend(asyncResult); }
                    catch { TCPDisconnectClient(TCPconnector); }
                }, null);
        }
        catch
        {
            TCPDisconnectClient(TCPconnector);
        }
    }

    public void TCPDisconnectClient(TCPConnector TCPconnector)
    {
        try
        {
            TCPconnector.Socket.Shutdown(SocketShutdown.Both);
            TCPconnector.Socket.Close();
        }
        catch { }
        tcpSemaphore.Release();
        TCPDisconnected(TCPconnector);
    }
    #endregion

    #region UDP Implementation
    private void StartUdpReceive()
    {
        if (!udpBufferPool.TryPop(out byte[] buffer))
            buffer = new byte[4096];

        EndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            udpSocket.BeginReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None,
                ref remoteEndpoint, UdpReceiveCallback, buffer);
        }
        catch
        {
            udpBufferPool.Push(buffer);
            Thread.Sleep(10);
            StartUdpReceive();
        }
    }

    private void UdpReceiveCallback(IAsyncResult asyncResult)
    {
        EndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
        byte[] buffer = (byte[])asyncResult.AsyncState;

        try
        {
            int bytesRead = udpSocket.EndReceiveFrom(asyncResult, ref remoteEndpoint);
            var ipEndpoint = (IPEndPoint)remoteEndpoint;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (bytesRead > 0)
                    {
                        var data = new byte[bytesRead];
                        Buffer.BlockCopy(buffer, 0, data, 0, bytesRead);
                        UDPMessageReceived(ipEndpoint, data);
                    }
                }
                finally
                {
                    udpBufferPool.Push(buffer);
                }
            });

            StartUdpReceive();
        }
        catch
        {
            udpBufferPool.Push(buffer);
            StartUdpReceive();
        }
    }

    public void UDPSend(IPEndPoint endpoint, byte[] data)
    {
        if (!udpBufferPool.TryPop(out byte[] buffer))
            buffer = new byte[data.Length];

        Buffer.BlockCopy(data, 0, buffer, 0, data.Length);

        try
        {
            udpSocket.BeginSendTo(buffer, 0, data.Length, SocketFlags.None, endpoint, asyncResult =>
            {
                try { udpSocket.EndSend(asyncResult); }
                catch { /* Handle send errors */ }
                finally { udpBufferPool.Push(buffer); }
            }, null);
        }
        catch
        {
            udpBufferPool.Push(buffer);
        }
    }
    #endregion

    public void Stop()
    {
        tcpSocket.Close();
        udpSocket.Close();
    }
}
