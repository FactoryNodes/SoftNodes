using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FactoryNodes.Iec61131;
using FactoryNodes.Iec61131.Emission;
using FactoryNodes.Iec61131.Model;

namespace FactoryNodes.SoftNodes;

/// <summary>Un valor inicial distinto del de la librería (los ajustes de la empresa a las estructuras de los bloques).</summary>
public sealed record MemberDefault(string Type, string Member, string Value);

/// <summary>
/// La ficha del autómata del que es el programa (PENDIENTES 166; autor: «al transferir junto el programa, SoftNodes ya sabe una IP
/// predeterminada, porque le ha llegado junto al proyecto»): nombre, plataforma (<c>TIA</c>/<c>Sysmac</c>; vacío = no dicha) e IP (vacío =
/// no dicha). Va en el <c>manifest.json</c>; SoftNodes la propone como perfil y «Dirección del PLC» de la instancia. Opcional.
/// </summary>
public sealed record SoftPlcCard(string Name, string Platform, string Ip);

/// <summary>El paquete no se puede leer o no es de esta versión.</summary>
public sealed class SoftPackageException(string message) : Exception(message);

/// <summary>
/// Lo que FactoryNodes entrega a SoftNodes («Transferir a esta sesión de SoftNodes»): la librería maestra completa ya
/// generada (la de <c>library/master</c> más los bloques de los loops y la topology del proyecto, en ST estándar), los
/// tamaños de los arrays del proyecto y el programa en su forma neutra (<see cref="ProgramModel"/>, la misma que reciben los
/// emisores de TIA y Sysmac). SoftNodes solo lo lee: nunca lo edita. Se guarda como ficheros de texto
/// (<see cref="ToFiles"/>), uno por parte, para que una carpeta o un zip lo lleve de una máquina a otra.
/// </summary>
public sealed record SoftPackage(
    string ProjectName,
    IReadOnlyList<(string Name, string Text)> LibraryFiles,
    IReadOnlyDictionary<string, int> Sizes,
    ProgramModel Program,
    IReadOnlyList<MemberDefault>? Defaults = null,
    IReadOnlyList<SoftSheet>? Sheets = null,
    IReadOnlyList<SoftProcess>? Processes = null,
    string? ProjectText = null,
    IReadOnlyList<SoftPidLoop>? PidLoops = null,
    SoftPlcCard? Plc = null)
{
    public const int FormatVersion = 1;
    public const string ManifestFile = "manifest.json";
    public const string ProgramFile = "program.json";

    /// <summary>Las hojas del sinóptico con sus entradas físicas (opcional: un paquete sin ellas sigue valiendo).</summary>
    public const string SheetsFile = "sheets.json";

    /// <summary>Los procesos simulados de la instalación: silos, carros… (opcional; PENDIENTES 73).</summary>
    public const string ProcessesFile = "processes.json";

    /// <summary>Los lazos PID que se simulan (opcional; PENDIENTES 83, docs/design/PID.md §10).</summary>
    public const string PidLoopsFile = "pidloops.json";

    /// <summary>Los depósitos de los paquetes de antes de los procesos: se leen como procesos.</summary>
    public const string TanksFile = "tanks.json";

    /// <summary>
    /// El proyecto <c>.nodes</c> del que salió el programa (opcional): el operario de SoftNodes lo necesita para vigilar los loops, las topologies y
    /// las alarmas de la instancia que corre. SoftNodes no lo edita.
    /// </summary>
    public const string ProjectFile = "project.nodes";
    public const string LibraryFolder = "library/";

    private sealed record Manifest(int Version, string ProjectName, IReadOnlyList<string> Library, IReadOnlyDictionary<string, int> Sizes, IReadOnlyList<MemberDefault>? Defaults,
        SoftPlcCard? Plc = null);

    private static readonly JsonSerializerOptions Json = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                // las propiedades calculadas del modelo (Elements, DataVariables, Instance…) no se guardan: se recalculan
                Modifiers = { info => { foreach (var property in info.Properties.Where(p => p.Set is null).ToList()) info.Properties.Remove(property); } }
            }
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Los ficheros del paquete por su ruta relativa (<c>manifest.json</c>, <c>program.json</c>, <c>library/Motor.st</c>…).</summary>
    public IReadOnlyDictionary<string, string> ToFiles()
    {
        var files = new Dictionary<string, string>
        {
            [ManifestFile] = JsonSerializer.Serialize(new Manifest(FormatVersion, ProjectName, LibraryFiles.Select(f => f.Name).ToList(), Sizes, Defaults, Plc), Json),
            // la explicación en palabras es para el emisor de IA; SoftNodes no la necesita
            [ProgramFile] = JsonSerializer.Serialize(Program with { Documentation = null }, Json)
        };
        if (Sheets is { Count: > 0 }) files[SheetsFile] = JsonSerializer.Serialize(Sheets, Json);
        if (Processes is { Count: > 0 }) files[ProcessesFile] = JsonSerializer.Serialize(Processes, Json);
        if (PidLoops is { Count: > 0 }) files[PidLoopsFile] = JsonSerializer.Serialize(PidLoops, Json);
        if (!string.IsNullOrEmpty(ProjectText)) files[ProjectFile] = ProjectText;
        foreach (var (name, text) in LibraryFiles) files[LibraryFolder + name + ".st"] = text;
        return files;
    }

    /// <summary>Todo el paquete en un solo fichero (<c>.softnodes</c>, un zip): lo que FactoryNodes guarda al transferir.</summary>
    public byte[] ToZip()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, text) in ToFiles())
            {
                using var entry = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
                entry.Write(new UTF8Encoding(false).GetBytes(text));
            }

        return stream.ToArray();
    }

    public static SoftPackage FromZip(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var files = new Dictionary<string, string>();
            foreach (var entry in zip.Entries.Where(e => e.Length > 0))
            {
                using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false));
                files[entry.FullName.Replace('\\', '/')] = reader.ReadToEnd();
            }

            return FromFiles(files);
        }
        catch (InvalidDataException)
        {
            throw new SoftPackageException("El fichero no es un paquete de SoftNodes (no es un zip).");
        }
    }

    /// <summary>
    /// Solo el <c>project.nodes</c> de un <c>.softnodes</c>, sin leer el resto (FactoryNodes ▸ «Abrir proyecto desde un paquete SoftNodes»,
    /// PENDIENTES 74): null si el paquete es de antes de que lo llevara (2026-10-19).
    /// </summary>
    public static string? ReadProjectText(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.GetEntry(ProjectFile) is not { Length: > 0 } entry) return null;
            using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false));
            return reader.ReadToEnd();
        }
        catch (InvalidDataException)
        {
            throw new SoftPackageException("El fichero no es un paquete de SoftNodes (no es un zip).");
        }
    }

    public static SoftPackage FromFiles(IReadOnlyDictionary<string, string> files)
    {
        string File(string path) => files.TryGetValue(path, out var text) ? text : throw new SoftPackageException($"Falta {path} en el paquete.");

        Manifest? manifest;
        ProgramModel? program;
        IReadOnlyList<SoftSheet>? sheets = null;
        IReadOnlyList<SoftProcess>? processes = null;
        IReadOnlyList<SoftPidLoop>? pidLoops = null;
        try
        {
            if (files.TryGetValue(PidLoopsFile, out var pidText)) pidLoops = JsonSerializer.Deserialize<List<SoftPidLoop>>(pidText, Json);
            manifest = JsonSerializer.Deserialize<Manifest>(File(ManifestFile), Json);
            program = JsonSerializer.Deserialize<ProgramModel>(File(ProgramFile), Json);
            if (files.TryGetValue(SheetsFile, out var sheetsText)) sheets = JsonSerializer.Deserialize<List<SoftSheet>>(sheetsText, Json);
            if (files.TryGetValue(ProcessesFile, out var processesText)) processes = JsonSerializer.Deserialize<List<SoftProcess>>(processesText, Json);
            else if (files.TryGetValue(TanksFile, out var tanksText)) processes = JsonSerializer.Deserialize<List<SoftTank>>(tanksText, Json)?.Select(t => t.ToProcess()).ToList();
        }
        catch (JsonException e)
        {
            throw new SoftPackageException("El paquete está dañado: " + e.Message);
        }

        if (manifest is null || program is null) throw new SoftPackageException("El paquete está vacío.");
        if (manifest.Version != FormatVersion)
            throw new SoftPackageException($"El paquete es de la versión {manifest.Version}; este SoftNodes lee la {FormatVersion}.");

        var library = manifest.Library.Select(name => (name, File(LibraryFolder + name + ".st"))).ToList();
        return new SoftPackage(manifest.ProjectName, library, manifest.Sizes, program, manifest.Defaults, sheets, processes, files.GetValueOrDefault(ProjectFile), pidLoops,
            manifest.Plc);
    }

    /// <summary>Lee la librería (sin validar sus convenciones de portabilidad: el paquete ya salió de FactoryNodes), con las funciones `/// COMPAT` del <paramref name="profile"/> (sin él, las de referencia) y los tamaños.</summary>
    public MasterLibrary LoadLibrary(SoftProfile? profile = null)
    {
        var result = MasterLibraryLoader.Load(profile is null ? LibraryFiles : profile.Apply(LibraryFiles));
        if (result.Library is null || result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new SoftPackageException("La librería del paquete tiene errores:\n" +
                string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(10)));
        var library = Sizes.Count == 0 ? result.Library : result.Library.WithSizes(Sizes);
        return Defaults is { Count: > 0 } ? ApplyDefaults(library, Defaults) : library;
    }

    /// <summary>Los valores iniciales que cambian respecto a la librería, para llevarlos en el paquete.</summary>
    public static IReadOnlyList<MemberDefault> DefaultsBetween(MasterLibrary original, MasterLibrary adjusted)
    {
        var list = new List<MemberDefault>();
        foreach (var type in adjusted.Types)
        {
            if (original.Type(type.Name) is not { } before) continue;
            foreach (var member in type.Members)
                if (before.Member(member.Name) is { } old && old.InitialValue != member.InitialValue && member.InitialValue is not null)
                    list.Add(new MemberDefault(type.Name, member.Name, member.InitialValue));
        }

        return list;
    }

    private static MasterLibrary ApplyDefaults(MasterLibrary library, IReadOnlyList<MemberDefault> defaults)
    {
        var byType = defaults.GroupBy(d => d.Type, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        StructType Rewrite(StructType type) => !byType.TryGetValue(type.Name, out var changes) ? type : type with
        {
            Items = type.Items.Select(i => i is MemberDeclaration m && changes.FirstOrDefault(c => c.Member.Equals(m.Name, StringComparison.OrdinalIgnoreCase)) is { } c
                ? m with { InitialValue = c.Value } : i).ToList()
        };
        return new MasterLibrary(library.Sources.Select(src => src with { Types = src.Types.Select(Rewrite).ToList() }).ToList());
    }

    /// <summary>La memoria del programa listo para ejecutar.</summary>
    public SoftProgram CreateProgram(SoftProfile? profile = null) => new(LoadLibrary(profile), Program);
}
