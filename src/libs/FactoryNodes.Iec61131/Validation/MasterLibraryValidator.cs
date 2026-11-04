using FactoryNodes.Iec61131.Model;
using FactoryNodes.Iec61131.Syntax;

namespace FactoryNodes.Iec61131.Validation;

/// <summary>
/// Comprueba que la librería maestra sigue sus convenciones (docs/MASTER_LIBRARY_CONVENTIONS.md): núcleo común en
/// cada bloque de elemento, secciones permitidas, directivas <c>IO</c> coherentes, tipos conocidos, nombres
/// reservados y construcciones prohibidas. Códigos <c>ML1xx</c>. Complementa al lector (<c>ML0xx</c>).
/// </summary>
public static class MasterLibraryValidator
{
    public static IReadOnlyList<LibraryDiagnostic> Validate(MasterLibrary library)
    {
        var d = new List<LibraryDiagnostic>();
        CheckDuplicates(library, d);
        CheckSizes(library, d);
        foreach (var type in library.Types) CheckType(library, type, d);
        foreach (var pou in library.Pous) CheckPou(library, pou, d);
        return d;
    }

    // ---------------------------------------------------------------- Arrays y tamaños

    /// <summary>Constantes de tamaño (<c>/// SIZE</c>): nombre en mayúsculas, único, que no choque con nada, y valor ≥ 1.</summary>
    private static void CheckSizes(MasterLibrary library, List<LibraryDiagnostic> d)
    {
        var sizes = library.Sizes.ToList();
        foreach (var group in sizes.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Error(d, "ML109", $"La constante de tamaño «{group.Key}» está declarada {group.Count()} veces.", group.Skip(1).First().Location);

        foreach (var size in sizes)
        {
            if (size.Name != size.Name.ToUpperInvariant())
                Error(d, "ML112", $"«{size.Name}»: una constante de tamaño se escribe en mayúsculas (así se distingue de las variables).", size.Location);
            if (size.Value < 1)
                Error(d, "ML112", $"«{size.Name}»: el valor por defecto de un tamaño es ≥ 1 (un array [1..0] no se admite).", size.Location);
            if (ReservedNames.ReasonForbidden(size.Name) is { } reason)
                Error(d, "ML105", $"«{size.Name}» no puede ser una constante de tamaño: {reason}.", size.Location);
            if (library.IsUserType(size.Name) || library.Pou(size.Name) is not null)
                Error(d, "ML109", $"«{size.Name}» es a la vez una constante de tamaño y un tipo o bloque.", size.Location);
        }
    }

    /// <summary>
    /// Un array: una dimensión, límites enteros o constantes de tamaño declaradas, inferior ≤ superior, sin valor inicial
    /// (los valores iniciales de arrays no están validados en ningún PLC), y ni señal física ni parámetro.
    /// </summary>
    private static void CheckArray(MasterLibrary library, MemberDeclaration member, List<LibraryDiagnostic> d)
    {
        if (member.Array is not { } bounds) return;

        foreach (var bound in new[] { bounds.Lower, bounds.Upper })
            if (library.BoundValue(bound) is null)
                Error(d, "ML112", $"«{member.Name}»: el límite «{bound}» no es un entero ni una constante de tamaño declarada con «/// SIZE».", member.Location);

        if (library.BoundValue(bounds.Lower) is { } lo && library.BoundValue(bounds.Upper) is { } hi && lo > hi)
            Error(d, "ML112", $"«{member.Name}»: ARRAY[{bounds.Lower}..{bounds.Upper}] tiene el límite inferior por encima del superior.", member.Location);

        if (member.InitialValue is not null)
            Error(d, "ML113", $"«{member.Name}»: un array no lleva valor inicial (sin validar en ningún PLC); se rellena desde el código.", member.Location);
        if (member.Io is not null)
            Error(d, "ML104", $"«{member.Name}»: /// IO no se usa en un array (cada señal física es un miembro).", member.Location);
        if (member.IsParameter)
            Error(d, "ML111", $"«{member.Name}»: /// PARAM no se usa en un array.", member.Location);
    }

    /// <summary>
    /// Un tipo que contiene arrays (directamente o en una subestructura) no puede tener valores iniciales en ningún
    /// miembro: en Sysmac van en la variable con todos los miembros (V14), y el valor de un array no está validado.
    /// </summary>
    private static void CheckNoDefaultsWithArrays(MasterLibrary library, StructType type, List<LibraryDiagnostic> d)
    {
        if (!ContainsArray(library, type, []) || library.Types.Any(t => t != type && t.Members.Any(m => m.TypeName.Equals(type.Name, StringComparison.OrdinalIgnoreCase))))
            return;   // se comprueba desde la raíz

        foreach (var (member, owner) in AllMembers(library, type, []))
            if (member.InitialValue is not null && !member.IsArray)
                Error(d, "ML113", $"«{owner}.{member.Name}»: {type.Name} contiene arrays y por eso ningún miembro suyo puede llevar valor inicial (sin validar en Sysmac, V14).", member.Location);
    }

    private static bool ContainsArray(MasterLibrary library, StructType type, HashSet<string> visiting)
    {
        if (!visiting.Add(type.Name)) return false;
        return type.Members.Any(m => m.IsArray || (library.Type(m.TypeName) is { } nested && ContainsArray(library, nested, visiting)));
    }

    private static IEnumerable<(MemberDeclaration Member, string Owner)> AllMembers(MasterLibrary library, StructType type, HashSet<string> visiting)
    {
        if (!visiting.Add(type.Name)) yield break;
        foreach (var member in type.Members)
        {
            yield return (member, type.Name);
            if (library.Type(member.TypeName) is { } nested)
                foreach (var inner in AllMembers(library, nested, visiting)) yield return inner;
        }
    }

    private static void Error(List<LibraryDiagnostic> d, string code, string message, SourceLocation? at) =>
        d.Add(new LibraryDiagnostic(DiagnosticSeverity.Error, code, message, at));

    // ---------------------------------------------------------------- Nombres

    private static void CheckDuplicates(MasterLibrary library, List<LibraryDiagnostic> d)
    {
        var names = library.Types.Select(t => (t.Name, t.Location)).Concat(library.Pous.Select(p => (p.Name, p.Location)));
        foreach (var group in names.GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Error(d, "ML109", $"«{group.Key}» está definido {group.Count()} veces en la librería.", group.Skip(1).First().Location);

        foreach (var (name, location) in names)
        {
            if (ReservedNames.ReasonForbidden(name) is { } reason)
                Error(d, "ML105", $"«{name}» no puede ser el nombre de un tipo o bloque: {reason}.", location);
            if (!ReservedNames.IsPortableIdentifier(name))
                Error(d, "ML105", $"«{name}» no es un identificador válido en todas las plataformas: {ReservedNames.PortableIdentifierProblem(name)}.", location);
        }
    }

    private static void CheckMemberNames(IEnumerable<MemberDeclaration> members, string owner, List<LibraryDiagnostic> d)
    {
        var list = members.ToList();
        foreach (var group in list.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Error(d, "ML109", $"«{group.Key}» está declarado dos veces en {owner}.", group.Skip(1).First().Location);

        foreach (var member in list)
        {
            if (ReservedNames.ReasonForbidden(member.Name) is { } reason)
                Error(d, "ML105", $"«{member.Name}» no puede ser un nombre de variable, miembro o parámetro: {reason}.", member.Location);
            if (!ReservedNames.IsPortableIdentifier(member.Name))
                Error(d, "ML105", $"«{member.Name}» no es un identificador válido en todas las plataformas: {ReservedNames.PortableIdentifierProblem(member.Name)}.", member.Location);
        }
    }

    // ---------------------------------------------------------------- Tipos

    private static void CheckType(MasterLibrary library, StructType type, List<LibraryDiagnostic> d)
    {
        CheckMemberNames(type.Members, type.Name, d);
        foreach (var member in type.Members)
        {
            CheckTypeName(library, member, allowFunctionBlocks: false, d);
            CheckArray(library, member, d);
            if (member.IsArray && library.BoundValue(member.Array!.Lower) is { } lower && lower != 0)
                Error(d, "ML114", $"«{type.Name}.{member.Name}»: un array de un tipo empieza en 0 (ARRAY[0..{member.Array.Upper}], el 0 sin usar si se cuenta desde 1): " +
                    "el servidor OPC UA de Omron no publica una variable con arrays que no empiecen en 0 (W588 §1-2-2).", member.Location);
            if (member.IsArray) continue;
            if (member.Io is { } io) CheckIo(library, type, member, io, d);
            if (member.IsParameter) CheckParameter(type, member, d);
        }

        CheckNoDefaultsWithArrays(library, type, d);
        if (Depth(library, type, []) > MaxDepth)
            Error(d, "ML115", $"«{type.Name}» anida más de {MaxDepth} estructuras (contándose él): el servidor OPC UA de Omron no publica " +
                "una variable así (W588 §1-2-2). Parte los datos en varias variables.", type.Location);
    }

    /// <summary>Niveles de estructura que publica el OPC UA de Omron (W588 §1-2-2: «nesting number exceeds three»).</summary>
    public const int MaxDepth = 3;

    /// <summary>Niveles de estructura de un tipo: 1 + el de su miembro estructura más hondo (un array de estructuras cuenta como ella).</summary>
    private static int Depth(MasterLibrary library, StructType type, HashSet<string> visiting)
    {
        if (!visiting.Add(type.Name)) return 1;
        var inner = type.Members.Select(m => library.Type(m.TypeName) is { } nested ? Depth(library, nested, new HashSet<string>(visiting)) : 0)
            .DefaultIfEmpty(0).Max();
        return 1 + inner;
    }

    /// <summary>
    /// <c>/// PARAM</c> (docs/design/BLOCK_PARAMETERS.md): solo en la raíz y en <c>Control</c>, y de tipo elemental. En
    /// <c>Extern</c> son señales (van por el mapeo) y en <c>Status</c> lo calcula el bloque.
    /// </summary>
    private static void CheckParameter(StructType owner, MemberDeclaration member, List<LibraryDiagnostic> d)
    {
        if (owner.Name.EndsWith("_Extern", StringComparison.Ordinal) || owner.Name.EndsWith("_Status", StringComparison.Ordinal))
            Error(d, "ML111", $"«{member.Name}»: /// PARAM solo va en la raíz o en Control, no en {owner.Name} (Extern son señales y Status lo calcula el bloque).", member.Location);
        if (!ElementaryTypes.IsElementary(member.TypeName))
            Error(d, "ML111", $"«{member.Name}»: /// PARAM solo va en miembros de tipo elemental, no en {member.TypeName}.", member.Location);
    }

    private static void CheckTypeName(MasterLibrary library, MemberDeclaration member, bool allowFunctionBlocks, List<LibraryDiagnostic> d)
    {
        var t = member.TypeName;
        if (ElementaryTypes.IsElementary(t) || library.IsUserType(t)) return;

        if (allowFunctionBlocks && (ElementaryTypes.IsAllowedStandardFunctionBlock(t) || library.IsUserFunctionBlock(t))) return;

        var reason = ElementaryTypes.IsStandardFunctionBlock(t)
            ? $"el bloque estándar {t} no está permitido (solo TON, TOF, R_TRIG, F_TRIG y RS)"
            : library.IsUserFunctionBlock(t) || ElementaryTypes.IsAllowedStandardFunctionBlock(t)
                ? $"{t} es un bloque de función y aquí solo caben datos"
                : $"el tipo {t} no existe (ni elemental ni definido en la librería)";
        Error(d, "ML107", $"«{member.Name}»: {reason}.", member.Location);
    }

    private static void CheckIo(MasterLibrary library, StructType owner, MemberDeclaration member, IoDirective io, List<LibraryDiagnostic> d)
    {
        var at = member.Location;
        if (!IoTypes.IsKnown(io.IoType))
        {
            Error(d, "ML104", $"«{member.Name}»: tipo de E/S «{io.IoType}» desconocido (v0: {string.Join(", ", IoTypes.Inputs.Concat(IoTypes.Outputs))}).", at);
            return;
        }

        var isBool = member.TypeName.Equals("BOOL", StringComparison.OrdinalIgnoreCase);
        if (io.IoType is "DI" or "DO" && !isBool)
            Error(d, "ML104", $"«{member.Name}»: una señal {io.IoType} debe ser BOOL, no {member.TypeName}.", at);
        if (io.IoType is "AI" or "AO" && (isBool || !ElementaryTypes.IsNumeric(member.TypeName)))
            Error(d, "ML104", $"«{member.Name}»: una señal {io.IoType} debe ser numérica, no {member.TypeName}.", at);

        if (io.Direction == IoDirection.Input)
        {
            if (!owner.Name.EndsWith("_Extern", StringComparison.Ordinal))
                Error(d, "ML104", $"«{member.Name}»: las entradas físicas van en la estructura Extern (UDT_<Tipo>_Extern), no en {owner.Name}.", at);
            if (!io.Required && io.Default is null)
                Error(d, "ML104", $"«{member.Name}»: una entrada IO necesita REQUIRED o DEFAULT <valor>.", at);
            if (io.Default is { } value) CheckDefault(library, owner, member, value, io, d);
        }
        else
        {
            if (!owner.Name.EndsWith("_Status", StringComparison.Ordinal))
                Error(d, "ML104", $"«{member.Name}»: las salidas físicas salen de Status (UDT_<Tipo>_Status), no de {owner.Name}.", at);
            if (io.Required || io.Default is not null)
                Error(d, "ML104", $"«{member.Name}»: una salida IO no lleva REQUIRED ni DEFAULT (si no se conecta, no se escribe).", at);
        }
    }

    /// <summary>DEFAULT constante del tipo del miembro, o ruta a un miembro de la estructura raíz del bloque (<c>Status.Active_Q</c>).</summary>
    private static void CheckDefault(MasterLibrary library, StructType owner, MemberDeclaration member, string value, IoDirective io, List<LibraryDiagnostic> d)
    {
        if (!io.DefaultIsMemberPath)
        {
            var isBool = member.TypeName.Equals("BOOL", StringComparison.OrdinalIgnoreCase);
            var ok = isBool ? value is "TRUE" or "FALSE" : double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
            if (!ok) Error(d, "ML104", $"«{member.Name}»: DEFAULT {value} no es un valor válido para {member.TypeName}.", member.Location);
            return;
        }

        var root = library.Types.FirstOrDefault(t => t.Members.Any(m => m.TypeName.Equals(owner.Name, StringComparison.OrdinalIgnoreCase)));
        if (root is null)
        {
            Error(d, "ML104", $"«{member.Name}»: DEFAULT {value} apunta a un miembro, pero {owner.Name} no está dentro de ninguna estructura raíz.", member.Location);
            return;
        }

        StructType? current = root;
        MemberDeclaration? target = null;
        foreach (var part in value.Split('.'))
        {
            target = current?.Member(part);
            if (target is null) break;
            current = library.Type(target.TypeName);
        }

        if (target is null)
            Error(d, "ML104", $"«{member.Name}»: DEFAULT {value} no existe en {root.Name}.", member.Location);
        else if (!target.TypeName.Equals(member.TypeName, StringComparison.OrdinalIgnoreCase))
            Error(d, "ML104", $"«{member.Name}»: DEFAULT {value} es {target.TypeName} y el miembro es {member.TypeName}.", member.Location);
    }

    // ---------------------------------------------------------------- POU

    private static void CheckPou(MasterLibrary library, Pou pou, List<LibraryDiagnostic> d)
    {
        CheckMemberNames(pou.Variables, pou.Name, d);

        foreach (var section in pou.Sections)
        {
            if (section.Kind == VarSectionKind.VarRetain)
                Error(d, "ML101", $"{pou.Name}: VAR RETAIN no está permitido. Las variables internas nunca son retentivas; lo que deba sobrevivir a un corte va en la estructura del bloque.", section.Location);
            else if (section.Kind == VarSectionKind.VarTemp)
            {
                // Variables de ciclo (convenciones §1): elementales y sin valor inicial, porque el bloque las escribe antes de leerlas.
                if (pou.Kind == PouKind.Function)
                    Error(d, "ML102", $"{pou.Name}: VAR_TEMP solo se usa en un FUNCTION_BLOCK (en una FUNCTION todas las variables son temporales: van en VAR).", section.Location);
                foreach (var variable in section.Members)
                {
                    if (variable.Array is not null || !ElementaryTypes.IsElementary(variable.TypeName))
                        Error(d, "ML102", $"«{variable.Name}»: una variable de ciclo (VAR_TEMP) es de tipo elemental; los bloques, arrays y estructuras van en VAR.", variable.Location);
                    else if (variable.InitialValue is not null)
                        Error(d, "ML102", $"«{variable.Name}»: una variable de ciclo (VAR_TEMP) no lleva valor inicial: el bloque la escribe antes de leerla en cada ejecución.", variable.Location);
                }
            }
        }

        foreach (var variable in pou.Variables)
        {
            // Un bloque /// ONLY usa los tipos y bloques de sistema de su plataforma (TCON, TCON_IP_v4…): aquí no se conocen y no se exigen
            if (pou.OnlyPlatform is null) CheckTypeName(library, variable, allowFunctionBlocks: true, d);
            CheckArray(library, variable, d);
            if (variable.Io is not null)
                Error(d, "ML104", $"«{variable.Name}»: /// IO solo se usa en los miembros de las estructuras, no en las variables del bloque.", variable.Location);
            if (variable.IsParameter)
                Error(d, "ML111", $"«{variable.Name}»: /// PARAM solo se usa en los miembros de las estructuras, no en las variables del bloque.", variable.Location);
        }

        if (pou.IsCompat && pou.Kind != PouKind.Function)
            Error(d, "ML108", $"{pou.Name}: /// COMPAT solo se aplica a una FUNCTION.", pou.Location);

        if (pou.Kind == PouKind.Function && pou.ReturnType is { } ret && !ElementaryTypes.IsElementary(ret) && !library.IsUserType(ret))
            Error(d, "ML107", $"{pou.Name}: el tipo devuelto {ret} no existe.", pou.Location);

        CheckBody(pou, d);
        if (pou.Kind == PouKind.FunctionBlock) CheckElementBlock(library, pou, d);
    }

    private static readonly HashSet<string> ForbiddenIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "EN_IN", "EN_OUT", "REGION", "END_REGION", "METHOD", "END_METHOD", "INTERFACE", "END_INTERFACE", "EXTENDS",
        "IMPLEMENTS", "PROPERTY", "END_PROPERTY", "THIS", "SUPER", "REF_TO", "POINTER"
    };

    /// <summary>
    /// Lo que añade el emisor de cada plataforma y no se escribe nunca en la librería: <c>#</c> de variable local,
    /// <c>REGION</c> nativo, <c>EN_IN</c>/<c>EN_OUT</c>; y lo que está fuera del perfil: orientación a objetos y punteros.
    /// </summary>
    private static void CheckBody(Pou pou, List<LibraryDiagnostic> d)
    {
        foreach (var line in pou.Body.Lines.OfType<CodeLine>())
        {
            foreach (var token in StLexer.Tokenize(line.Text))
            {
                if (token.Is("#"))
                    Error(d, "ML106", $"{pou.Name}: «#» (variable local de TIA) no se escribe en la librería; lo pone el emisor.", line.Location);
                else if (pou.OnlyPlatform is null && token.Kind == StTokenKind.Identifier && ForbiddenIdentifiers.Contains(token.Text))
                    Error(d, "ML106", $"{pou.Name}: «{token.Text}» no está permitido en la librería maestra (lo añade el emisor o está fuera del perfil IEC clásico).", line.Location);
            }
        }
    }

    /// <summary>
    /// Bloque de elemento: <c>&lt;Tipo&gt;_Block</c> con <c>Data : UDT_&lt;Tipo&gt;</c> por VAR_IN_OUT, entradas estándar y el
    /// núcleo común completo en Control, Extern y Status. Se reconoce solo por el sufijo <c>_Block</c> (traspaso 2): un
    /// FB auxiliar puede tener su propia estructura (<c>OnOffCycle</c> con <c>UDT_OnOffCycle</c>). Las salidas de
    /// comodidad (<c>Active_Q</c>, <c>Alarm</c>…) no se exigen: dependen del bloque (un Pilot no tiene alarmas).
    /// </summary>
    private static void CheckElementBlock(MasterLibrary library, Pou pou, List<LibraryDiagnostic> d)
    {
        if (!pou.Name.EndsWith("_Block", StringComparison.Ordinal)) return;
        var data = pou.VariablesOf(VarSectionKind.InOut).FirstOrDefault(v => v.Name == "Data");

        var kind = pou.Name[..^"_Block".Length];
        var udtName = $"UDT_{kind}";
        if (data is null || data.TypeName != udtName)
        {
            Error(d, "ML110", $"{pou.Name}: un bloque de elemento recibe «Data : {udtName}» por VAR_IN_OUT.", pou.Location);
            return;
        }

        RequireMembers(pou.VariablesOf(VarSectionKind.Input), CoreMembers.BlockInputs, $"{pou.Name} (VAR_INPUT)", pou.Location, d);

        if (library.Type(udtName) is not { } udt)
        {
            Error(d, "ML103", $"{pou.Name}: falta el tipo {udtName}.", pou.Location);
            return;
        }

        foreach (var (section, required) in CoreMembers.Sections)
        {
            var sectionType = $"{udtName}_{section}";
            var member = udt.Member(section);
            if (member is null || member.TypeName != sectionType)
            {
                Error(d, "ML103", $"{udtName} debe tener el miembro «{section} : {sectionType}».", udt.Location);
                continue;
            }

            if (library.Type(sectionType) is not { } sub)
            {
                Error(d, "ML103", $"Falta el tipo {sectionType}.", udt.Location);
                continue;
            }

            RequireMembers(sub.Members, required, sectionType, sub.Location, d);
        }
    }

    private static void RequireMembers(IEnumerable<MemberDeclaration> members, IReadOnlyList<(string Name, string Type)> required,
        string owner, SourceLocation at, List<LibraryDiagnostic> d)
    {
        var byName = members.ToDictionary(m => m.Name, StringComparer.Ordinal);
        foreach (var (name, type) in required)
        {
            if (!byName.TryGetValue(name, out var member))
                Error(d, "ML103", $"{owner}: falta el miembro del núcleo común «{name} : {type}».", at);
            else if (!member.TypeName.Equals(type, StringComparison.Ordinal))
                Error(d, "ML103", $"{owner}: «{name}» debe ser {type}, no {member.TypeName}.", member.Location);
        }
    }
}

/// <summary>Núcleo común obligatorio (sección 4 de las convenciones) y entradas/salidas estándar de los bloques de elemento.</summary>
public static class CoreMembers
{
    public static readonly IReadOnlyList<(string Name, string Type)> Control =
    [
        ("Auto_ON", "BOOL"), ("Man_ON", "BOOL"), ("Man_OFF", "BOOL"), ("Run_Conditions", "BOOL"), ("Lock_Conditions", "BOOL")
    ];

    public static readonly IReadOnlyList<(string Name, string Type)> Extern =
    [
        ("Sel_Auto", "BOOL"), ("Sel_Man", "BOOL"), ("Pul_ResetAlarm", "BOOL")
    ];

    public static readonly IReadOnlyList<(string Name, string Type)> Status =
    [
        ("Active_Q", "BOOL"), ("Run_OK", "BOOL"), ("Locked", "BOOL"), ("ALM_SUM", "BOOL"), ("ALM_Word", "WORD"),
        ("Siren", "BOOL"), ("HMI", "UINT")
    ];

    /// <summary>Sección 6: reloj de 1 s y modo simulación.</summary>
    public static readonly IReadOnlyList<(string Name, string Type)> BlockInputs = [("Pul_1Sec", "BOOL"), ("Debug", "BOOL")];

    public static readonly IReadOnlyList<(string Section, IReadOnlyList<(string Name, string Type)> Members)> Sections =
        [("Control", Control), ("Extern", Extern), ("Status", Status)];
}

/// <summary>Tipos elementales IEC y bloques estándar que admite la librería.</summary>
public static class ElementaryTypes
{
    private static readonly HashSet<string> Numeric = new(StringComparer.OrdinalIgnoreCase)
    {
        "SINT", "INT", "DINT", "LINT", "USINT", "UINT", "UDINT", "ULINT", "REAL", "LREAL"
    };

    private static readonly HashSet<string> Elementary = new(StringComparer.OrdinalIgnoreCase)
    {
        "BOOL", "BYTE", "WORD", "DWORD", "LWORD", "SINT", "INT", "DINT", "LINT", "USINT", "UINT", "UDINT", "ULINT",
        "REAL", "LREAL", "TIME", "LTIME", "DATE", "TIME_OF_DAY", "TOD", "DATE_AND_TIME", "DT", "STRING", "WSTRING"
    };

    /// <summary>Sección 1 de las convenciones.</summary>
    public static readonly IReadOnlyList<string> AllowedStandardFunctionBlocks = ["TON", "TOF", "R_TRIG", "F_TRIG", "RS"];

    private static readonly HashSet<string> StandardFunctionBlocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "TON", "TOF", "TP", "R_TRIG", "F_TRIG", "RS", "SR", "CTU", "CTD", "CTUD"
    };

    public static bool IsElementary(string name) => Elementary.Contains(name);
    public static bool IsNumeric(string name) => Numeric.Contains(name);
    public static bool IsStandardFunctionBlock(string name) => StandardFunctionBlocks.Contains(name);

    public static bool IsAllowedStandardFunctionBlock(string name) =>
        AllowedStandardFunctionBlocks.Contains(name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Nombres prohibidos en la librería (traspaso 2, regla 3.1; errores reales en docs/EMITTER_RULES.md), sin distinguir
/// mayúsculas, para cualquier variable, miembro, parámetro, tipo o bloque: palabras clave IEC (también las de SFC),
/// nombres de tipos (<c>DT</c>, <c>TOD</c>, <c>D</c>, <c>T</c>…), bloques y funciones estándar (<c>TON</c>, <c>EXP</c>…,
/// y las conversiones <c>X_TO_Y</c>) e instrucciones de las plataformas de destino que no son estándar (<c>Lock</c> en
/// Sysmac). Pruebas reales: <c>dt</c> (TIA), <c>tOn</c> y <c>Step</c> (Sysmac). Excepción validada en los dos PLC:
/// <c>Reset</c> (entrada de <c>AlarmManager</c>), por eso no está en la lista. Se amplía con cada error real del PLC.
/// </summary>
public static class ReservedNames
{
    /// <summary>Palabras clave IEC 61131-3, incluidas las de SFC (STEP, TRANSITION, ACTION…).</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACTION", "END_ACTION", "AND", "ARRAY", "AT", "BY", "CASE", "END_CASE", "CONFIGURATION", "END_CONFIGURATION",
        "CONSTANT", "CONTINUE", "DO", "ELSE", "ELSIF", "EXIT", "FALSE", "FOR", "END_FOR", "FROM", "FUNCTION", "END_FUNCTION",
        "FUNCTION_BLOCK", "END_FUNCTION_BLOCK", "IF", "END_IF", "INITIAL_STEP", "MOD", "NOT", "OF", "ON", "OR", "PROGRAM",
        "END_PROGRAM", "READ_ONLY", "READ_WRITE", "REPEAT", "END_REPEAT", "RESOURCE", "END_RESOURCE", "RETAIN", "NON_RETAIN",
        "RETURN", "STEP", "END_STEP", "STRUCT", "END_STRUCT", "TASK", "THEN", "TO", "TRANSITION", "END_TRANSITION", "TRUE",
        "TYPE", "END_TYPE", "UNTIL", "VAR", "END_VAR", "VAR_ACCESS", "VAR_CONFIG", "VAR_EXTERNAL", "VAR_GLOBAL", "VAR_INPUT",
        "VAR_IN_OUT", "VAR_OUTPUT", "VAR_TEMP", "WHILE", "END_WHILE", "WITH", "XOR", "REGION", "END_REGION", "PRIORITY",
        "SINGLE", "INTERVAL", "EN", "ENO", "R_EDGE", "F_EDGE"
    };

    /// <summary>Nombres de tipos elementales y genéricos, con sus abreviaturas (D, T, DT, TOD…).</summary>
    private static readonly HashSet<string> TypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "BOOL", "BYTE", "WORD", "DWORD", "LWORD", "SINT", "INT", "DINT", "LINT", "USINT", "UINT", "UDINT", "ULINT", "REAL",
        "LREAL", "TIME", "LTIME", "DATE", "LDATE", "D", "LD", "TIME_OF_DAY", "LTIME_OF_DAY", "TOD", "LTOD", "DATE_AND_TIME",
        "LDATE_AND_TIME", "DT", "LDT", "T", "LT", "STRING", "WSTRING", "CHAR", "WCHAR", "ANY", "ANY_DERIVED",
        "ANY_ELEMENTARY", "ANY_MAGNITUDE", "ANY_NUM", "ANY_REAL", "ANY_INT", "ANY_BIT", "ANY_STRING", "ANY_DATE"
    };

    /// <summary>Bloques y funciones estándar IEC.</summary>
    private static readonly HashSet<string> StandardNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "TON", "TOF", "TP", "CTU", "CTD", "CTUD", "R_TRIG", "F_TRIG", "RS", "SR",
        "ABS", "SQRT", "LN", "LOG", "EXP", "EXPT", "SIN", "COS", "TAN", "ASIN", "ACOS", "ATAN", "ADD", "SUB", "MUL", "DIV",
        "MOVE", "SHL", "SHR", "ROL", "ROR", "SEL", "MAX", "MIN", "LIMIT", "MUX", "GT", "GE", "EQ", "LE", "LT", "NE",
        "LEN", "LEFT", "RIGHT", "MID", "CONCAT", "INSERT", "DELETE", "REPLACE", "FIND", "TRUNC"
    };

    /// <summary>Instrucciones y tipos de sistema de TIA o Sysmac que no son estándar IEC.</summary>
    private static readonly HashSet<string> PlatformNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Lock", "Unlock", "Timer", "Counter", "TON_TIME", "TOF_TIME", "TP_TIME", "IEC_TIMER", "IEC_COUNTER",
        // TIA (2026-10-22, Natucer15): en una fuente SCL es la cabecera de cada bloque («VERSION : 0.1»); un miembro «Version» de un tipo da
        // «Error de sintaxis: el valor introducido "Version" no es válido» y rompe la lectura de los bloques de detrás
        "VERSION",
        // Sysmac (2026-10-23, Natucer18): instrucción básica; una variable interna «Inc» de un loop da «El nombre ya se ha utilizado para la
        // instrucción básica Inc.»
        "Inc",
        // TIA (2026-10-30, EipTagServer en un S7-1500 real): «name» es palabra clave de la fuente SCL («Error de sintaxis: el valor introducido
        // "name" no es válido», y el bloque no se crea); «seg» es la instrucción SEG (decodificador de 7 segmentos: «El bloque 'seg' no está
        // soportado por la CPU utilizada»)
        "NAME", "SEG"
    };

    /// <summary>Por qué no se puede usar el nombre, o null si se puede.</summary>
    public static string? ReasonForbidden(string name) =>
        Keywords.Contains(name) ? "es una palabra clave IEC"
        : TypeNames.Contains(name) ? "es el nombre de un tipo IEC"
        : StandardNames.Contains(name) ? "es el nombre de un bloque o función estándar IEC"
        : IsConversion(name) ? "es el nombre de una función de conversión estándar"
        : PlatformNames.Contains(name) ? "es una instrucción o tipo de sistema de TIA o Sysmac"
        : null;

    public static bool IsForbidden(string name) => ReasonForbidden(name) is not null;

    /// <summary><c>X_TO_Y</c> con dos tipos elementales (<c>REAL_TO_LREAL</c>) o <c>TO_Y</c> (<c>TO_LREAL</c>).</summary>
    private static bool IsConversion(string name)
    {
        var at = name.IndexOf("_TO_", StringComparison.OrdinalIgnoreCase);
        if (at > 0) return TypeNames.Contains(name[..at]) && TypeNames.Contains(name[(at + 4)..]);
        return name.StartsWith("TO_", StringComparison.OrdinalIgnoreCase) && TypeNames.Contains(name[3..]);
    }

    /// <summary>Máximo de Sysmac para el nombre de una variable (127 bytes; aquí solo hay ASCII, un byte por letra).</summary>
    public const int MaxIdentifierLength = 127;

    /// <summary>Letras, dígitos y '_', sin empezar por dígito ni por '_', sin terminar en '_', sin «__», sin empezar por «P_» y de
    /// 127 letras como mucho (reglas de Sysmac: «El nombre de variable no es válido»).</summary>
    public static bool IsPortableIdentifier(string name) => PortableIdentifierProblem(name) is null;

    /// <summary>Por qué <paramref name="name"/> no vale como identificador en todas las plataformas, o null si vale.</summary>
    public static string? PortableIdentifierProblem(string name) =>
        name.Length == 0 ? "está vacío"
        : name.Length > MaxIdentifierLength ? $"tiene más de {MaxIdentifierLength} letras"
        : !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? "solo puede tener letras sin acento, números y «_»"
        : char.IsAsciiDigit(name[0]) ? "no puede empezar por un número"
        : name[0] == '_' ? "no puede empezar por «_»"
        : name[^1] == '_' ? "no puede terminar en «_»"
        : name.Contains("__", StringComparison.Ordinal) ? "no puede tener dos «_» seguidos"
        : name.StartsWith("P_", StringComparison.OrdinalIgnoreCase) ? "no puede empezar por «P_» (Sysmac)"
        : null;
}
