using FactoryNodes.Iec61131.Emission;
using FactoryNodes.SoftNodes.Runtime;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace FactoryNodes.SoftNodes.OpcUa;

/// <summary>
/// Servidor OPC UA de una instancia de SoftNodes: publica la memoria del programa (<c>hw_P</c>, <c>io_P</c>) con el mismo
/// direccionamiento que el PLC de la plataforma elegida (<see cref="IPlcAddressing"/>: TIA <c>"hw_P"."X"."Y"</c>, Sysmac
/// <c>X.Y</c>), así que FactoryNodes (módulo Simulación) se conecta como a un PLC real. Sin seguridad y con usuario
/// anónimo, como el simulador de Omron y PLCSIM. Los certificados van en <c>pkiRoot</c>.
/// </summary>
public sealed class SoftOpcUaServer : IDisposable
{
    private readonly SoftProgram _program;
    private readonly IPlcAddressing _addressing;
    private readonly string _name;
    private readonly string _pkiRoot;
    private ApplicationInstance? _application;
    private SoftStandardServer? _server;

    /// <param name="name">Nombre de la instancia (da el nombre de la aplicación OPC UA).</param>
    /// <param name="port">Puerto TCP; 0 = elige uno libre (ver <see cref="Endpoint"/>).</param>
    public SoftOpcUaServer(SoftProgram program, IPlcAddressing addressing, string name, int port, string pkiRoot)
    {
        _program = program;
        _addressing = addressing;
        _name = name;
        _pkiRoot = pkiRoot;
        Port = port;
    }

    public int Port { get; private set; }

    /// <summary>La dirección a la que se conecta un cliente.</summary>
    public string Endpoint => $"opc.tcp://localhost:{Port}/SoftNodes";

    public bool IsRunning => _server is not null;

    public async Task StartAsync()
    {
        if (_server is not null) return;
        if (Port == 0) Port = FreePort();

        var configuration = await CreateConfigurationAsync();
        _application = new ApplicationInstance(configuration) { ApplicationName = _name, ApplicationType = ApplicationType.Server };
        await _application.CheckApplicationInstanceCertificatesAsync(true, 0);

        _server = new SoftStandardServer((server, config) => new SoftNodeManager(server, config, _addressing, _program));
        await _application.StartAsync(_server);
    }

    public async Task StopAsync()
    {
        var server = _server;
        _server = null;
        if (server is not null) await server.StopAsync();
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<ApplicationConfiguration> CreateConfigurationAsync()
    {
        string Store(string kind) => Path.Combine(_pkiRoot, kind);
        var configuration = new ApplicationConfiguration
        {
            ApplicationName = _name,
            ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:FactoryNodes:SoftNodes:{_name}",
            ProductUri = "urn:factorynodes:softnodes",
            ApplicationType = ApplicationType.Server,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Store("own"),
                    SubjectName = $"CN=SoftNodes {_name}, O=FactoryNodes"
                },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Store("issuer") },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Store("trusted") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Store("rejected") },
                AutoAcceptUntrustedCertificates = true
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = { $"opc.tcp://localhost:{Port}/SoftNodes" },
                SecurityPolicies =
                {
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None }
                },
                UserTokenPolicies = { new UserTokenPolicy(UserTokenType.Anonymous) },
                MinRequestThreadCount = 5,
                MaxRequestThreadCount = 100,
                MaxQueuedRequestCount = 200
            }
        };

        await configuration.ValidateAsync(ApplicationType.Server);
        return configuration;
    }
}

internal sealed class SoftStandardServer(Func<IServerInternal, ApplicationConfiguration, INodeManager> nodeManager) : StandardServer
{
    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration) =>
        new(server, configuration, null, nodeManager(server, configuration));

    protected override ServerProperties LoadServerProperties() => new()
    {
        ManufacturerName = "FactoryNodes",
        ProductName = "SoftNodes",
        ProductUri = "urn:factorynodes:softnodes",
        SoftwareVersion = "1.0",
        BuildNumber = "1",
        BuildDate = DateTime.UtcNow
    };
}
