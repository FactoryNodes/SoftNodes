using FactoryNodes.Iec61131.Model;

namespace FactoryNodes.SoftNodes.Runtime;

public enum StTypeKind
{
    Bool,
    Int,
    Real,
    Time,
    String,
    Struct,
    Array,
    FunctionBlock
}

/// <summary>
/// Un tipo de dato en tiempo de ejecución. Los enteros llevan su ancho y signo (el valor se guarda en un <c>long</c> y
/// se envuelve al asignar, como hace un PLC); TIME va en milisegundos.
/// </summary>
public sealed class StType
{
    public required StTypeKind Kind { get; init; }
    public string Name { get; init; } = "";
    public int Bits { get; init; }
    public bool Signed { get; init; }
    public bool Is32BitReal { get; init; }
    public StructType? Struct { get; init; }
    public StType? Element { get; init; }
    public int Lower { get; init; }
    public int Upper { get; init; }

    /// <summary>Bloque de función: el de la librería, o null si es uno estándar (<see cref="Name"/>: TON, R_TRIG…).</summary>
    public Pou? Pou { get; init; }

    public override string ToString() => Kind == StTypeKind.Array ? $"ARRAY[{Lower}..{Upper}] OF {Element}" : Name;

    public static readonly StType Bool = new() { Kind = StTypeKind.Bool, Name = "BOOL" };
    public static readonly StType Time = new() { Kind = StTypeKind.Time, Name = "TIME" };
    public static readonly StType String = new() { Kind = StTypeKind.String, Name = "STRING" };
    public static readonly StType Real = new() { Kind = StTypeKind.Real, Name = "REAL", Is32BitReal = true };
    public static readonly StType LReal = new() { Kind = StTypeKind.Real, Name = "LREAL" };

    private static StType IntType(string name, int bits, bool signed) =>
        new() { Kind = StTypeKind.Int, Name = name, Bits = bits, Signed = signed };

    /// <summary>Los tipos elementales por nombre (sin distinguir mayúsculas).</summary>
    public static readonly IReadOnlyDictionary<string, StType> Elementary = new Dictionary<string, StType>(StringComparer.OrdinalIgnoreCase)
    {
        ["BOOL"] = Bool,
        ["SINT"] = IntType("SINT", 8, true),
        ["INT"] = IntType("INT", 16, true),
        ["DINT"] = IntType("DINT", 32, true),
        ["LINT"] = IntType("LINT", 64, true),
        ["USINT"] = IntType("USINT", 8, false),
        ["UINT"] = IntType("UINT", 16, false),
        ["UDINT"] = IntType("UDINT", 32, false),
        ["ULINT"] = IntType("ULINT", 64, false),
        ["BYTE"] = IntType("BYTE", 8, false),
        ["WORD"] = IntType("WORD", 16, false),
        ["DWORD"] = IntType("DWORD", 32, false),
        ["LWORD"] = IntType("LWORD", 64, false),
        ["REAL"] = Real,
        ["LREAL"] = LReal,
        ["TIME"] = Time,
        ["STRING"] = String
    };

    /// <summary>El valor inicial de un tipo elemental.</summary>
    public object DefaultElementary() => Kind switch
    {
        StTypeKind.Bool => false,
        StTypeKind.Int or StTypeKind.Time => 0L,
        StTypeKind.Real => 0.0,
        StTypeKind.String => "",
        _ => throw new InvalidOperationException("No es un tipo elemental: " + this)
    };
}

/// <summary>
/// Una variable: su valor y su tipo. Todo lo que se puede leer o escribir (variable global, miembro de una estructura,
/// elemento de un array, variable de un bloque) es una casilla; un parámetro VAR_IN_OUT es la misma casilla del
/// llamador.
/// </summary>
public sealed class Slot(StType type, object value)
{
    public StType Type { get; } = type;
    public object Value { get; set; } = value;
}

/// <summary>Una estructura: sus miembros por nombre (sin distinguir mayúsculas), en el orden de la declaración.</summary>
public sealed class StructValue
{
    public Dictionary<string, Slot> Members { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> Names => Members.Keys;
}

public sealed class ArrayValue(int lower, Slot[] items)
{
    public int Lower { get; } = lower;
    public Slot[] Items { get; } = items;
    public int Upper => Lower + Items.Length - 1;
}

/// <summary>
/// La instancia de un bloque de función: sus variables (entradas, salidas, internas) y, si es uno estándar, su estado.
/// Las internas sobreviven entre ciclos, como en el PLC.
/// </summary>
public sealed class FbInstance(StType type)
{
    public StType Type { get; } = type;
    public Dictionary<string, Slot> Vars { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Estado oculto de los bloques estándar (el instante en que empezó un TON…).</summary>
    public Dictionary<string, object> State { get; } = [];
}

/// <summary>Un fallo de ejecución (división por 0, índice fuera, nombre que no existe): la CPU pasaría a STOP.</summary>
public sealed class StRuntimeException(string message, string file, int line) : Exception($"{file}:{line}: {message}")
{
    public string File { get; } = file;
    public int Line { get; } = line;

    /// <summary>El motivo sin el fichero y la línea delante («Índice 7 fuera de Items[1..5].»).</summary>
    public string InnerMessage() => message;
}
