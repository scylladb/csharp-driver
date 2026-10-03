using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

static class PortAllocator
{
    private static int lastPort = -1;

    public static int GetNextAvailablePort(
        int shardCount,
        int shardId,
        int lowPort,
        int highPort,
        AddressFamily addressFamily)
    {
        int foundPort = -1;
        int lastPortValue;

        do
        {
            lastPortValue = Volatile.Read(ref lastPort);

            int scanStart = lastPortValue == -1 ? lowPort : lastPortValue;
            if (scanStart < lowPort)
            {
                scanStart = lowPort;
            }

            scanStart += (shardCount - scanStart % shardCount) + shardId;

            for (int port = scanStart; port <= highPort; port += shardCount)
            {
                if (IsTcpPortAvailable(port, addressFamily))
                {
                    foundPort = port;
                    break;
                }
            }

            if (foundPort == -1)
            {
                scanStart = lowPort + (shardCount - lowPort % shardCount) + shardId;

                for (int port = scanStart; port <= highPort; port += shardCount)
                {
                    if (IsTcpPortAvailable(port, addressFamily))
                    {
                        foundPort = port;
                        break;
                    }
                }
            }

            if (foundPort == -1)
            {
                return -1;
            }
        }
        while (Interlocked.CompareExchange(ref lastPort, foundPort, lastPortValue) != lastPortValue);

        return foundPort;
    }

    public static bool IsTcpPortAvailable(int port, AddressFamily addressFamily)
    {
        Socket socket = null;
        try
        {
            socket = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp);
            var localAddress = addressFamily == AddressFamily.InterNetworkV6
                ? IPAddress.IPv6Any
                : IPAddress.Any;
            socket.Bind(new IPEndPoint(localAddress, port));
            socket.Listen(1);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            socket?.Dispose();
        }
    }
}
