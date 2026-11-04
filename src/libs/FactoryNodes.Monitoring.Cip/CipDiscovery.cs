using System.Net;

namespace FactoryNodes.Monitoring.Cip;

/// <summary>Busca los equipos EtherNet/IP de la red (ListIdentity por difusión UDP 44818).</summary>
public static class CipDiscovery
{
    public static Task<IReadOnlyList<CipIdentity>> FindAsync(TimeSpan wait, IPAddress? broadcast = null, CancellationToken cancellation = default) =>
        CipSession.DiscoverAsync(wait, broadcast, cancellation);
}
