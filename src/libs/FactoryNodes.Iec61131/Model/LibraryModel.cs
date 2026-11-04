namespace FactoryNodes.Iec61131.Model;

/// <summary>Posición en el fuente, para los diagnósticos: fichero y línea (1 = primera).</summary>
public readonly record struct SourceLocation(string File, int Line)
{
    public override string ToString() => $"{File}:{Line}";
}

// ---------------------------------------------------------------- Declaraciones

/// <summary>Una línea de una lista de declaraciones (miembros de un STRUCT o variables de una sección VAR).</summary>
public abstract record DeclarationItem(SourceLocation Location);

/// <summary>Comentario de persona en su propia línea (<c>// texto</c>); <see cref="Text"/> sin las barras ni espacios de los extremos.</summary>
public sealed record CommentLine(string Text, SourceLocation Location) : DeclarationItem(Location);

/// <summary>
/// Un miembro o variable: <c>Name : TypeName [:= InitialValue]; [/// directiva] [// comentario]</c>. Una línea con
/// varios nombres (<c>a, b : BOOL;</c>) da un <see cref="MemberDeclaration"/> por nombre, con el mismo tipo y comentario.
/// <see cref="IsParameter"/>: lleva <c>/// PARAM</c>, un parámetro que se puede dar por elemento
/// (docs/design/BLOCK_PARAMETERS.md); su texto descriptivo es <see cref="Comment"/>.
/// </summary>
public sealed record MemberDeclaration(
    string Name,
    string TypeName,
    string? InitialValue,
    IoDirective? Io,
    string? Comment,
    SourceLocation Location,
    bool IsParameter = false,
    ArrayBounds? Array = null) : DeclarationItem(Location)
{
    /// <summary>Es un array de una dimensión de <see cref="TypeName"/> (que es entonces el tipo de cada elemento).</summary>
    public bool IsArray => Array is not null;

    /// <summary>
    /// Texto de la directiva <c>IO</c> tal cual se escribió, sin <c>///</c> (p. ej. "IO DI DEFAULT FALSE"), o null. Los
    /// emisores lo copian al comentario del miembro; <c>/// PARAM</c> no, para no cambiar la salida validada.
    /// </summary>
    public string? DirectiveText => Io?.Text;
}

/// <summary>
/// Límites de un <c>ARRAY[Lower..Upper] OF T</c> (solo una dimensión: el perfil validado de Sysmac no admite más). Cada
/// límite es un entero o el nombre de una constante de tamaño (<c>/// SIZE</c>, ver <see cref="SizeConstant"/>) que el
/// emisor sustituye por su número.
/// </summary>
public sealed record ArrayBounds(string Lower, string Upper);

/// <summary>
/// <c>/// SIZE &lt;NOMBRE&gt; &lt;valor&gt;</c> fuera de los bloques: una constante de tamaño (límite de arrays y de bucles). El
/// código maestro la usa por su nombre; al generar, el emisor pone el número (el del proyecto si lo da, si no
/// <see cref="Value"/>). Así el algoritmo es el mismo para todos los proyectos y cada uno tiene sus tamaños, y Sysmac,
/// que solo admite números en los límites de un array, recibe números.
/// </summary>
public sealed record SizeConstant(string Name, int Value, SourceLocation Location);

/// <summary>Sentido de una señal física, según su tipo de E/S.</summary>
public enum IoDirection
{
    Input,
    Output
}

/// <summary>
/// <c>/// IO &lt;tipo&gt; [REQUIRED | DEFAULT &lt;valor&gt;]</c>: el miembro es un punto físico. <see cref="Default"/> puede ser
/// una constante (<c>TRUE</c>) o un miembro de la propia estructura (<c>Status.Active_Q</c>).
/// </summary>
public sealed record IoDirective(string Text, string IoType, bool Required, string? Default)
{
    public IoDirection Direction => IoTypes.DirectionOf(IoType);

    /// <summary>El valor por defecto es un miembro de la estructura (contiene un punto o empieza por letra y no es TRUE/FALSE).</summary>
    public bool DefaultIsMemberPath =>
        Default is { } d && char.IsLetter(d[0]) && !d.Equals("TRUE", StringComparison.OrdinalIgnoreCase) &&
        !d.Equals("FALSE", StringComparison.OrdinalIgnoreCase) && !d.Contains('#');
}

/// <summary>
/// Tipos de E/S que admite <c>/// IO</c> en v0: los del catálogo que ya usan los bloques. Ampliar aquí (y en la
/// documentación) cuando un bloque necesite otro (contadores rápidos, pesaje…).
/// </summary>
public static class IoTypes
{
    public static readonly IReadOnlyList<string> Inputs = ["DI", "AI"];
    public static readonly IReadOnlyList<string> Outputs = ["DO", "AO"];

    public static bool IsKnown(string type) => Inputs.Contains(type) || Outputs.Contains(type);

    public static IoDirection DirectionOf(string type) => Outputs.Contains(type) ? IoDirection.Output : IoDirection.Input;
}

/// <summary><c>TYPE Name : STRUCT … END_STRUCT; END_TYPE</c>. Es el único tipo de usuario permitido en la librería.</summary>
public sealed record StructType(string Name, IReadOnlyList<DeclarationItem> Items, SourceLocation Location)
{
    public IEnumerable<MemberDeclaration> Members => Items.OfType<MemberDeclaration>();

    public MemberDeclaration? Member(string name) =>
        Members.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

// ---------------------------------------------------------------- POU

public enum PouKind
{
    Function,
    FunctionBlock
}

public enum VarSectionKind
{
    Input,
    Output,
    InOut,
    Var,
    VarConstant,

    /// <summary><c>VAR RETAIN</c>: se lee para poder dar el error de la regla 1 (las internas nunca son retentivas).</summary>
    VarRetain,

    /// <summary>
    /// <c>VAR_TEMP</c>, las <b>variables de ciclo</b> (convenciones §1, 2026-10-29): internas elementales que el bloque escribe antes de
    /// leer en cada ejecución, así que no importa si el PLC las conserva o no. TIA las declara en <c>VAR</c>; Sysmac, en <c>VAR</c>
    /// retentivas o no según <c>EmitContext.CycleVariablesRetain</c>; SoftNodes las pone a cero al entrar en el bloque.
    /// </summary>
    VarTemp
}

public sealed record VarSection(VarSectionKind Kind, IReadOnlyList<DeclarationItem> Items, SourceLocation Location)
{
    public IEnumerable<MemberDeclaration> Members => Items.OfType<MemberDeclaration>();

    /// <summary>Palabra clave IEC de la sección ("VAR_INPUT", "VAR CONSTANT"…).</summary>
    public string Keyword => KeywordOf(Kind);

    public static string KeywordOf(VarSectionKind kind) => kind switch
    {
        VarSectionKind.Input => "VAR_INPUT",
        VarSectionKind.Output => "VAR_OUTPUT",
        VarSectionKind.InOut => "VAR_IN_OUT",
        VarSectionKind.Var => "VAR",
        VarSectionKind.VarConstant => "VAR CONSTANT",
        VarSectionKind.VarRetain => "VAR RETAIN",
        VarSectionKind.VarTemp => "VAR_TEMP",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

/// <summary>Un FUNCTION o FUNCTION_BLOCK de la librería.</summary>
public sealed record Pou(
    PouKind Kind,
    string Name,
    string? ReturnType,
    bool IsCompat,
    IReadOnlyList<VarSection> Sections,
    StBody Body,
    SourceLocation Location)
{
    public IEnumerable<MemberDeclaration> Variables => Sections.SelectMany(s => s.Members);

    public IEnumerable<MemberDeclaration> VariablesOf(VarSectionKind kind) =>
        Sections.Where(s => s.Kind == kind).SelectMany(s => s.Members);

    /// <summary>
    /// Las secciones con las variables de ciclo (<see cref="VarSectionKind.VarTemp"/>) dentro de <c>VAR</c>, detrás de las internas: para
    /// los PLC que no tienen temporales en un FB o donde no se quieren (TIA, B&amp;R). Sin ninguna, las secciones tal cual.
    /// </summary>
    public IReadOnlyList<VarSection> SectionsWithCycleVariablesInVar()
    {
        var temps = Sections.Where(s => s.Kind == VarSectionKind.VarTemp).ToList();
        if (temps.Count == 0) return Sections;
        var items = temps.SelectMany(s => s.Items).ToList();
        var result = new List<VarSection>();
        var merged = false;
        foreach (var section in Sections)
        {
            if (section.Kind == VarSectionKind.VarTemp) continue;
            if (section.Kind == VarSectionKind.Var)
            {
                result.Add(section with { Items = [.. section.Items, .. items] });
                merged = true;
            }
            else result.Add(section);
        }

        if (!merged) result.Add(new VarSection(VarSectionKind.Var, items, temps[0].Location));
        return result;
    }

    /// <summary>Qué es cada bit de <c>Status.ALM_Word</c> (<c>/// ALARM</c> antes del FUNCTION_BLOCK), por bit.</summary>
    public IReadOnlyList<BlockAlarm> Alarms { get; init; } = [];

    /// <summary>
    /// <c>/// ONLY &lt;Plataforma&gt;</c> (PENDIENTES 165): el bloque es solo de esa plataforma («TIA», «Sysmac»): puede usar sus tipos e
    /// instrucciones de sistema (el validador no exige que existan en la librería), solo su emisor lo escribe y los demás lo saltan; SoftNodes no
    /// lo ejecuta (su trabajo lo hace de forma nativa). Null = portable, como todos los demás.
    /// </summary>
    public string? OnlyPlatform { get; init; }

    /// <summary>Si este bloque se emite para <paramref name="platform"/> (portable o de esa plataforma).</summary>
    public bool IsFor(string platform) => OnlyPlatform is null || OnlyPlatform.Equals(platform, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// <c>/// ALARM &lt;bit&gt; &lt;abreviatura&gt; "&lt;descripción&gt;" ["&lt;soluciones&gt;"]</c> (docs/design/ALARMS.md): el bit
/// de <c>Status.ALM_Word</c>, la abreviatura que sale en pantalla detrás del equipo («M101 KM») y la información del «?»
/// (qué es y qué revisar), común para todos los equipos con ese bloque.
/// </summary>
public sealed record BlockAlarm(int Bit, string Code, string Description, string Solutions, SourceLocation Location);

// ---------------------------------------------------------------- Cuerpo

/// <summary>
/// Cuerpo de una POU. Las regiones <c>/// REGION</c> son estructura; el código queda como texto, línea a línea y sin
/// pérdidas (espacios, alineación y comentarios), porque así se validó en los PLC y los emisores solo reescriben
/// identificadores, llamadas y literales sobre los tokens (ver <see cref="Syntax.StLexer"/> y WHY.md).
/// </summary>
public sealed record StBody(IReadOnlyList<BodyLine> Lines);

public abstract record BodyLine(SourceLocation Location);

/// <summary><c>/// REGION &lt;título&gt;</c>.</summary>
public sealed record RegionStart(string Title, SourceLocation Location) : BodyLine(Location);

/// <summary><c>/// END_REGION</c>.</summary>
public sealed record RegionEnd(SourceLocation Location) : BodyLine(Location);

/// <summary>Una línea de código o de comentario, tal cual (puede estar vacía).</summary>
public sealed record CodeLine(string Text, SourceLocation Location) : BodyLine(Location);

// ---------------------------------------------------------------- Librería

/// <summary>Un fichero <c>.st</c> de la librería maestra, ya leído.</summary>
public sealed record LibrarySource(string Name, IReadOnlyList<StructType> Types, IReadOnlyList<Pou> Pous, IReadOnlyList<SizeConstant>? SizeList = null)
{
    /// <summary>Constantes de tamaño (<c>/// SIZE</c>) declaradas en este fichero.</summary>
    public IReadOnlyList<SizeConstant> Sizes => SizeList ?? [];
}

/// <summary>La librería maestra completa, en el orden de los ficheros (el orden se conserva en el código generado).</summary>
public sealed record MasterLibrary(IReadOnlyList<LibrarySource> Sources)
{
    public IEnumerable<StructType> Types => Sources.SelectMany(s => s.Types);
    public IEnumerable<Pou> Pous => Sources.SelectMany(s => s.Pous);

    public StructType? Type(string name) =>
        Types.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public Pou? Pou(string name) =>
        Pous.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool IsUserType(string name) => Type(name) is not null;

    public bool IsUserFunctionBlock(string name) => Pou(name) is { Kind: PouKind.FunctionBlock };

    /// <summary>Constantes de tamaño (<c>/// SIZE</c>) de toda la librería, con su valor actual.</summary>
    public IEnumerable<SizeConstant> Sizes => Sources.SelectMany(s => s.Sizes);

    public SizeConstant? Size(string name) => Sizes.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Un límite de array como número: el entero tal cual o el valor de su constante de tamaño (null si no es ninguno).</summary>
    public int? BoundValue(string bound) =>
        int.TryParse(bound, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : Size(bound)?.Value;

    /// <summary>
    /// La misma librería con los tamaños de un proyecto (p. ej. los que da el grafo de una topology). Los nombres que no
    /// se dan conservan su valor; un nombre que la librería no declara es un error de quien llama.
    /// </summary>
    public MasterLibrary WithSizes(IReadOnlyDictionary<string, int> sizes)
    {
        foreach (var name in sizes.Keys.Where(k => Size(k) is null))
            throw new ArgumentException($"La librería maestra no declara la constante de tamaño «{name}».", nameof(sizes));
        foreach (var (name, value) in sizes.Where(p => p.Value < 1))
            throw new ArgumentException($"El tamaño «{name}» = {value} no vale: los arrays van de 1 a N, con N ≥ 1.", nameof(sizes));

        var byName = new Dictionary<string, int>(sizes, StringComparer.OrdinalIgnoreCase);
        return new MasterLibrary(Sources.Select(source => source with
        {
            SizeList = source.Sizes.Select(s => byName.TryGetValue(s.Name, out var v) ? s with { Value = v } : s).ToList()
        }).ToList());
    }
}

// ---------------------------------------------------------------- Diagnósticos

public enum DiagnosticSeverity
{
    Error,
    Warning
}

/// <summary>Error o aviso sobre la librería, con código estable (<c>ML0xx</c>) y posición.</summary>
public sealed record LibraryDiagnostic(DiagnosticSeverity Severity, string Code, string Message, SourceLocation? Location)
{
    public override string ToString() => Location is { } at ? $"{at}: {Code} {Message}" : $"{Code} {Message}";
}
