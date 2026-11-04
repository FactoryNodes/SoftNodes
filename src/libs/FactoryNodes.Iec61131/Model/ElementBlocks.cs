using FactoryNodes.Iec61131.Validation;

namespace FactoryNodes.Iec61131.Model;

/// <summary>
/// Un punto físico de un bloque de elemento: un miembro de su estructura marcado con <c>/// IO</c>.
/// <see cref="Path"/> es la ruta desde la estructura raíz (<c>Extern.KM_OK</c>, <c>Status.Active_FW</c>): es lo que guardan
/// el catálogo y el sinóptico como destino de una señal, y lo que permite trasladar el mapeo de un bloque a otro por
/// nombre de miembro.
/// </summary>
/// <param name="DataType">Tipo del miembro (<c>BOOL</c>, <c>INT</c>, <c>REAL</c>): el de la variable física que se le conecta.</param>
public sealed record BlockIoPoint(string Path, string IoType, IoDirection Direction, bool Required, string? Default, string? Comment,
    string DataType = "BOOL")
{
    /// <summary>Nombre del miembro sin la sección (<c>KM_OK</c>).</summary>
    public string Member => Path[(Path.LastIndexOf('.') + 1)..];

    /// <summary>
    /// Miembros del núcleo común que no se conectan señal a señal sino por asignación (selector y reset de la zona,
    /// sección 9.3 de las convenciones).
    /// </summary>
    public bool IsAssignedByZone => CoreAssignments.Contains(Path);

    public static readonly IReadOnlyList<string> CoreAssignments = ["Extern.Sel_Auto", "Extern.Sel_Man", "Extern.Pul_ResetAlarm"];
}

/// <summary>
/// Un miembro configurable de un bloque de elemento: de tipo elemental, en la raíz o en <c>Control</c> (sin los
/// modos y órdenes del núcleo común). Es parámetro si lleva <c>/// PARAM</c> (<see cref="IsParameter"/>); la empresa
/// puede marcar o desmarcar cualquiera de ellos (docs/design/BLOCK_PARAMETERS.md). <see cref="Path"/> es la ruta desde
/// la estructura raíz (<c>SGDelay</c>, <c>Control.Enable_Smooth</c>): es la clave con la que la empresa, el catálogo y
/// la instancia guardan su valor y la que permite conservarlo al cambiar de bloque. <see cref="Comment"/> es su texto
/// descriptivo.
/// </summary>
/// <param name="Default">El <c>:=</c> de la librería, o null (el cero de su tipo).</param>
public sealed record BlockParameter(string Path, string DataType, string? Default, string? Comment, bool IsParameter = true)
{
    /// <summary>Nombre del miembro sin la sección (<c>Enable_Smooth</c>).</summary>
    public string Member => Path[(Path.LastIndexOf('.') + 1)..];
}

/// <summary>
/// Interfaz física de un bloque de elemento (<c>UDT_&lt;Tipo&gt;</c> + <c>&lt;Tipo&gt;_Block</c>): se identifica por
/// <see cref="Type"/> (<c>Motor</c>), no por fichero. Dos versiones de la librería con el mismo <see cref="Signature"/>
/// ofrecen los mismos puntos físicos (base para la comprobación de compatibilidad de docs/design/LIBRARIES.md).
/// </summary>
public sealed record ElementBlockInterface(string Type, string Description, IReadOnlyList<BlockIoPoint> IoPoints)
{
    /// <summary>
    /// Miembros configurables (ver <see cref="BlockParameter"/>), sean o no parámetros en la librería, en orden de
    /// declaración: primero los de la raíz, luego los de Control.
    /// </summary>
    public IReadOnlyList<BlockParameter> Members { get; init; } = [];

    /// <summary>Parámetros del bloque en la librería (<c>/// PARAM</c>).</summary>
    public IReadOnlyList<BlockParameter> Parameters => Members.Where(m => m.IsParameter).ToList();

    public BlockParameter? Parameter(string path) =>
        Parameters.FirstOrDefault(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    public BlockParameter? Member(string path) =>
        Members.FirstOrDefault(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    public string FunctionBlock => $"{Type}_Block";
    public string DataType => $"UDT_{Type}";

    public BlockIoPoint? Point(string path) =>
        IoPoints.FirstOrDefault(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Puntos que se conectan señal a señal (sin selector ni reset, que van por asignación).</summary>
    public IEnumerable<BlockIoPoint> SignalPoints => IoPoints.Where(p => !p.IsAssignedByZone);

    /// <summary>Texto estable con todos los puntos (ruta, tipo, REQUIRED/DEFAULT), en orden, para comparar versiones.</summary>
    public string Signature => string.Join(";", IoPoints.Select(p =>
        $"{p.Path}:{p.IoType}:{(p.Required ? "R" : "")}{(p.Default is { } d ? "=" + d : "")}"));
}

/// <summary>Lee de la librería los bloques de elemento y sus puntos físicos. Los FB auxiliares nunca aparecen.</summary>
public static class ElementBlocks
{
    /// <summary>
    /// Bloques de elemento de la librería, por orden de <see cref="ElementBlockInterface.Type"/>. Un bloque de
    /// elemento es la pareja <c>&lt;Tipo&gt;_Block</c> + <c>UDT_&lt;Tipo&gt;</c>; sus puntos son los miembros con
    /// <c>/// IO</c> de las subestructuras de <c>UDT_&lt;Tipo&gt;</c> (Control, Extern, Status), en orden de declaración;
    /// sus parámetros, los miembros con <c>/// PARAM</c>.
    /// </summary>
    public static IReadOnlyList<ElementBlockInterface> Of(MasterLibrary library)
    {
        var blocks = new List<ElementBlockInterface>();
        foreach (var fb in library.Pous.Where(p => p.Kind == PouKind.FunctionBlock && p.Name.EndsWith("_Block", StringComparison.Ordinal)))
        {
            var type = fb.Name[..^"_Block".Length];
            if (library.Type($"UDT_{type}") is not { } udt) continue;

            var points = new List<BlockIoPoint>();
            var members = udt.Members.Where(IsConfigurable).Select(m => ToMember(m.Name, m)).ToList();
            foreach (var section in udt.Members)
            {
                if (library.Type(section.TypeName) is not { } sub) continue;
                if (section.Name == "Control")
                    members.AddRange(sub.Members.Where(m => IsConfigurable(m) && !CoreMembers.Control.Any(c => c.Name == m.Name))
                        .Select(m => ToMember($"Control.{m.Name}", m)));
                foreach (var m in sub.Members.Where(m => m.Io is not null))
                    points.Add(new BlockIoPoint($"{section.Name}.{m.Name}", m.Io!.IoType, m.Io.Direction, m.Io.Required, m.Io.Default, m.Comment,
                        m.TypeName.ToUpperInvariant()));
            }

            var description = fb.VariablesOf(VarSectionKind.InOut).FirstOrDefault(v => v.Name == "Data")?.Comment ?? "";
            blocks.Add(new ElementBlockInterface(type, description, points) { Members = members });
        }

        return blocks.OrderBy(b => b.Type, StringComparer.Ordinal).ToList();
    }

    private static bool IsConfigurable(MemberDeclaration m) => ElementaryTypes.IsElementary(m.TypeName);

    private static BlockParameter ToMember(string path, MemberDeclaration m) =>
        new(path, m.TypeName.ToUpperInvariant(), m.InitialValue, m.Comment, m.IsParameter);
}
