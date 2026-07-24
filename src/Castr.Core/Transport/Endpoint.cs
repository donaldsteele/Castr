using System.Net;

namespace Castr.Core.Transport;

public sealed record Endpoint(string Host, int Port)
{
    public static Endpoint FromIPEndPoint(IPEndPoint endpoint) => new(endpoint.Address.ToString(), endpoint.Port);

    public override string ToString() => $"{Host}:{Port}";
}

public sealed record ReceivedPacket(byte[] Payload, Endpoint From);
