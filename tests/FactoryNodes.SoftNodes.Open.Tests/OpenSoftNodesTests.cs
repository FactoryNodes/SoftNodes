using System.Globalization;
using FactoryNodes.Iec61131.Emission;
using FactoryNodes.Monitoring;
using FactoryNodes.Monitoring.Cip;
using FactoryNodes.SoftNodes.Instances;
using FactoryNodes.SoftNodes.Open.Presentation;

namespace FactoryNodes.SoftNodes.Open.Tests;

/// <summary>
/// El SoftNodes abierto (PENDIENTES 159) con un programa de verdad (el de <c>samples/validation/topology-program</c>): crear la instancia, encenderla
/// (OPC UA, EtherNet/IP), RUN, ver y escribir variables, recargar y apagar guardando la retentiva; y que respeta lo que SoftNodes Pro guarda en la
/// misma carpeta.
/// </summary>
public sealed class OpenSoftNodesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "softnodes-open-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class Store(string root) : IInstanceStore
    {
        private string FileOf(string i, string p) => Path.Combine(root, i, p.Replace('/', Path.DirectorySeparatorChar));
        public IReadOnlyList<string> ListInstances() => Directory.Exists(root) ? Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, SimulatedPlc.SettingsFile))).Select(Path.GetFileName).ToList()! : [];
        public string? Read(string i, string p) => File.Exists(FileOf(i, p)) ? File.ReadAllText(FileOf(i, p)) : null;
        public void Write(string i, string p, string t) { Directory.CreateDirectory(Path.GetDirectoryName(FileOf(i, p))!); File.WriteAllText(FileOf(i, p), t); }
        public byte[]? ReadBytes(string i, string p) => File.Exists(FileOf(i, p)) ? File.ReadAllBytes(FileOf(i, p)) : null;
        public void WriteBytes(string i, string p, byte[] b) { Directory.CreateDirectory(Path.GetDirectoryName(FileOf(i, p))!); File.WriteAllBytes(FileOf(i, p), b); }
        public DateTime? LastWrite(string i, string p) => File.Exists(FileOf(i, p)) ? File.GetLastWriteTime(FileOf(i, p)) : null;
        public void DeleteInstance(string i) => Directory.Delete(Path.Combine(root, i), true);
        public string PkiFolder(string i) => Path.Combine(root, i, "pki");
    }

    private sealed class Services(string root) : IOpenSoftNodesServices
    {
        public IInstanceStore Store { get; } = new Store(root);
        public (byte[] Bytes, string FileName)? ChooseProgram() => null;
        public bool Confirm(string message) => true;
        public void OnUiThread(Action action) => action();
        public string? SavedLanguage { get; set; } = "es";
        public CultureInfo SystemCulture => CultureInfo.InvariantCulture;
        public void Restart() { }
    }

    /// <summary>
    /// Un programa sin bloques (solo señales y una global), sin la librería maestra de FactoryNodes: lo que SoftNodes abierto necesita para probarse
    /// sin ella. Con un programa de FactoryNodes de verdad se prueba igual en el repositorio de FactoryNodes.
    /// </summary>
    private static byte[] Program()
    {
        var model = new ProgramModel("Prueba", "hw_Prueba", "io_Prueba",
            [
                new ProgramVariable("I_Marcha", ProgramVariableKind.PhysicalInput, "marcha"),
                new ProgramVariable("Q_Motor", ProgramVariableKind.PhysicalOutput, "motor"),
                new ProgramVariable("Consigna", ProgramVariableKind.Other, "consigna", "REAL", Published: true)
            ],
            [], new ProgramSection("999", "Outputs", []), []);
        return new SoftPackage("Prueba", [], new Dictionary<string, int>(), model).ToZip();
    }

    [Fact]
    public async Task An_instance_runs_the_program_serves_it_and_keeps_its_memory()
    {
        var vm = new SoftNodesOpenViewModel(new Services(_root));
        var item = vm.NewInstance(Program(), "Prueba");
        Assert.Equal("Prueba", item.Name);
        // puertos de prueba libres, para no chocar con un SoftNodes que esté abierto en este PC
        item.Port = 48420 + Random.Shared.Next(500);
        item.EipPort = 0;
        item.ModbusPort = 0;
        Assert.True(item.Variables.Count > 0);

        item.PowerCommand.Execute(null);
        await WaitAsync(() => item.IsOn && item.IsRunning);
        Assert.Contains("opc.tcp://localhost:", item.Endpoints);

        // escribir a mano una variable (un BOOL de las señales) y leerla de vuelta
        item.Filter = "io_";
        var signal = item.Variables.First(v => v.Type == "BOOL");
        signal.Value = "TRUE";
        Assert.Equal("", signal.Problem);
        vm.Tick();
        signal.Value = "quizás";
        Assert.Contains("no vale", signal.Problem);

        // apagar guarda la retentiva; se vuelve a abrir igual
        item.PowerCommand.Execute(null);
        await WaitAsync(() => !item.IsOn);
        Assert.NotNull(new Store(_root).Read("Prueba", SimulatedPlc.RetentiveFile));
        Assert.Contains("RUN.", string.Join("\n", item.Log));
        await vm.ShutdownAsync();

        var again = new SoftNodesOpenViewModel(new Services(_root));
        Assert.Equal("Prueba", Assert.Single(again.Instances).Name);
        await again.ShutdownAsync();
    }

    [Fact]
    public async Task Other_plcs_read_it_over_ethernet_ip()
    {
        var vm = new SoftNodesOpenViewModel(new Services(_root));
        var item = vm.NewInstance(Program(), "Eip");
        item.Port = 48920 + Random.Shared.Next(500);
        item.EipPort = 45000 + Random.Shared.Next(500);
        item.ModbusPort = 0;
        item.PowerCommand.Execute(null);
        await WaitAsync(() => item.IsOn);
        Assert.Contains("eip://localhost:", item.Endpoints);

        var client = new CipPlcConnection(new TiaAddressing());
        await client.ConnectAsync(new PlcConnectionSettings($"eip://127.0.0.1:{item.EipPort}", Protocol: PlcProtocol.EtherNetIp));
        item.Filter = "io_";
        var path = item.Variables.First(v => v.Type == "BOOL").Path;
        var read = (await client.ReadAsync([PlcPath.Parse(path)])).Single();
        Assert.True(read.IsGood, read.ToString());
        await client.DisconnectAsync();
        await vm.ShutdownAsync();
    }

    [Fact]
    public void What_softnodes_pro_keeps_in_the_folder_is_not_lost()
    {
        var store = new Store(_root);
        var vm = new SoftNodesOpenViewModel(new Services(_root));
        var item = vm.NewInstance(Program(), "Compartida");
        // lo que guardaría SoftNodes Pro en el mismo softnode.json
        var json = store.Read("Compartida", SimulatedPlc.SettingsFile)!.TrimEnd().TrimEnd('}') + ",\n  \"Plant\": true,\n  \"ExternalPlc\": \"opc.tcp://10.0.0.1:4840\"\n}";
        store.Write("Compartida", SimulatedPlc.SettingsFile, json);
        var again = new SoftNodesOpenViewModel(new Services(_root));
        again.Instances.Single().CycleMs = 50;
        var saved = store.Read("Compartida", SimulatedPlc.SettingsFile)!;
        Assert.Contains("\"ExternalPlc\": \"opc.tcp://10.0.0.1:4840\"", saved);
        Assert.Contains("\"CycleMs\": 50", saved);
        Assert.Equal(50, SimulatedPlc.ReadSettings(store, "Compartida").CycleMs);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(100);
        Assert.True(condition());
    }
}
