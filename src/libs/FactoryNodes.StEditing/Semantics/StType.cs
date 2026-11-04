using FactoryNodes.Iec61131.Model;
using FactoryNodes.Iec61131.Validation;

namespace FactoryNodes.StEditing.Semantics;

public enum StTypeKind
{
    /// <summary>No se sabe (ya hay un error, o un genérico sin comprobar): no da más errores.</summary>
    Unknown,
    Bool,
    Integer,
    Real,
    Bits,
    Time,
    String,

    /// <summary>Entero sin tipo (<c>12</c>): vale para cualquier entero, real o cadena de bits.</summary>
    AnyInt,

    /// <summary>Real sin tipo (<c>1.5</c>): vale para REAL y LREAL.</summary>
    AnyReal,
    Struct,
    FunctionBlock,
    Array,

    /// <summary>Lo que no es un valor: una función, un tipo.</summary>
    None
}

/// <summary>Tipo de una expresión del cuerpo, con lo justo para comprobar asignaciones, condiciones y miembros.</summary>
public sealed record StType(StTypeKind Kind, string Name, StType? Element = null, ArrayBounds? Bounds = null)
{
    public static readonly StType Unknown = new(StTypeKind.Unknown, "?");
    public static readonly StType None = new(StTypeKind.None, "");
    public static readonly StType Bool = new(StTypeKind.Bool, "BOOL");
    public static readonly StType AnyInt = new(StTypeKind.AnyInt, "entero");
    public static readonly StType AnyReal = new(StTypeKind.AnyReal, "real");
    public static readonly StType Time = new(StTypeKind.Time, "TIME");
    public static readonly StType String = new(StTypeKind.String, "STRING");

    public bool IsUnknown => Kind == StTypeKind.Unknown;

    public bool IsNumeric => Kind is StTypeKind.Integer or StTypeKind.Real or StTypeKind.AnyInt or StTypeKind.AnyReal;

    public override string ToString() => Kind == StTypeKind.Array ? $"ARRAY[{Bounds?.Lower}..{Bounds?.Upper}] OF {Element}" : Name;

    private static readonly Dictionary<string, (int Bits, bool Signed)> Integers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SINT"] = (8, true), ["INT"] = (16, true), ["DINT"] = (32, true), ["LINT"] = (64, true),
        ["USINT"] = (8, false), ["UINT"] = (16, false), ["UDINT"] = (32, false), ["ULINT"] = (64, false)
    };

    private static readonly Dictionary<string, int> BitStrings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BYTE"] = 8, ["WORD"] = 16, ["DWORD"] = 32, ["LWORD"] = 64
    };

    /// <summary>Tipo de un nombre de la librería (elemental, STRUCT o FUNCTION_BLOCK); <see cref="Unknown"/> si no existe.</summary>
    public static StType Of(string name, MasterLibrary library)
    {
        var upper = name.ToUpperInvariant();
        if (upper == "BOOL") return Bool;
        if (Integers.ContainsKey(upper)) return new StType(StTypeKind.Integer, upper);
        if (BitStrings.ContainsKey(upper)) return new StType(StTypeKind.Bits, upper);
        if (upper is "REAL" or "LREAL") return new StType(StTypeKind.Real, upper);
        if (upper is "TIME" or "LTIME" or "DATE" or "TIME_OF_DAY" or "TOD" or "DATE_AND_TIME" or "DT")
            return new StType(StTypeKind.Time, upper);
        if (upper is "STRING" or "WSTRING") return new StType(StTypeKind.String, upper);
        if (library.Type(name) is { } type) return new StType(StTypeKind.Struct, type.Name);
        if (library.Pou(name) is { Kind: PouKind.FunctionBlock } fb) return new StType(StTypeKind.FunctionBlock, fb.Name);
        if (StandardBlocks.Get(name) is { } standard) return new StType(StTypeKind.FunctionBlock, standard.Name);
        return Unknown;
    }

    /// <summary>Tipo de una declaración (con su array, si lo es).</summary>
    public static StType Of(MemberDeclaration member, MasterLibrary library) =>
        member.Array is { } bounds
            ? new StType(StTypeKind.Array, "ARRAY", Of(member.TypeName, library), bounds)
            : Of(member.TypeName, library);

    /// <summary>Tipo de un literal: <c>TRUE</c>, <c>12</c>, <c>1.5</c>, <c>T#2s</c>, <c>WORD#16#0001</c>, <c>'x'</c>.</summary>
    public static StType OfLiteral(string text, MasterLibrary library)
    {
        if (text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || text.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return Bool;
        if (text.StartsWith('\'')) return String;
        var hash = text.IndexOf('#');
        if (hash > 0 && char.IsLetter(text[0]))
        {
            var prefix = text[..hash].ToUpperInvariant();
            if (prefix is "T" or "TIME" or "LT" or "LTIME") return Time;
            if (prefix is "D" or "DATE" or "TOD" or "TIME_OF_DAY" or "DT" or "DATE_AND_TIME") return new StType(StTypeKind.Time, prefix);
            return Of(prefix, library);
        }

        if (hash > 0) return AnyInt;   // 16#FF
        return text.Contains('.') || text.Contains('e') || text.Contains('E') ? AnyReal : AnyInt;
    }

    /// <summary>
    /// ¿Se puede asignar un valor de tipo <paramref name="source"/> a <paramref name="target"/> sin conversión? Null si sí;
    /// si no, por qué. Prudente: lo que el master validado hace con conversiones explícitas, aquí la pide.
    /// </summary>
    public static string? CannotAssign(StType target, StType source)
    {
        if (target.IsUnknown || source.IsUnknown) return null;
        if (source.Kind == StTypeKind.None) return $"«{source.Name}» no es un valor.";
        if (target.Kind == StTypeKind.Array || source.Kind == StTypeKind.Array)
            return target.Kind == source.Kind && Same(target.Element!, source.Element!) ? null : $"No se puede asignar {Describe(source)} a {Describe(target)}.";
        if (Same(target, source)) return null;

        switch (source.Kind)
        {
            case StTypeKind.AnyInt when target.Kind is StTypeKind.Integer or StTypeKind.Real or StTypeKind.Bits:
            case StTypeKind.AnyReal when target.Kind == StTypeKind.Real:
                return null;
            case StTypeKind.AnyReal when target.Kind == StTypeKind.Integer:
                return $"Un real no cabe en {target.Name} sin conversión (p. ej. REAL_TO_{target.Name}).";
            case StTypeKind.AnyInt when target.Kind == StTypeKind.Time:
                return "Un tiempo se escribe con su unidad: T#0s, T#500ms…";
        }

        if (target.Kind == StTypeKind.Integer && source.Kind == StTypeKind.Integer)
        {
            var (tb, ts) = Integers[target.Name];
            var (sb, ss) = Integers[source.Name];
            if (tb > sb && (ts == ss || ts)) return null;
            return $"{source.Name} a {target.Name} necesita conversión explícita ({source.Name}_TO_{target.Name}).";
        }

        if (target.Kind == StTypeKind.Real && source.Kind == StTypeKind.Real)
            return target.Name == "LREAL" ? null : $"LREAL a REAL necesita conversión explícita (LREAL_TO_REAL).";

        if (target.Kind == StTypeKind.Bits && source.Kind == StTypeKind.Bits)
            return BitStrings[target.Name] >= BitStrings[source.Name] ? null
                : $"{source.Name} a {target.Name} necesita conversión explícita ({source.Name}_TO_{target.Name}).";

        if (target.Kind is StTypeKind.Integer or StTypeKind.Real or StTypeKind.Bits &&
            source.Kind is StTypeKind.Integer or StTypeKind.Real or StTypeKind.Bits)
            return $"{source.Name} a {target.Name} necesita conversión explícita ({source.Name}_TO_{target.Name}).";

        return $"No se puede asignar {Describe(source)} a {Describe(target)}.";
    }

    /// <summary>¿Se pueden comparar (<c>=</c>, <c>&lt;</c>…)? Si uno se puede asignar al otro.</summary>
    public static bool Comparable(StType a, StType b) => CannotAssign(a, b) is null || CannotAssign(b, a) is null;

    public static bool Same(StType a, StType b) =>
        a.Kind == b.Kind && Normal(a.Name).Equals(Normal(b.Name), StringComparison.OrdinalIgnoreCase);

    private static string Normal(string name) => name.ToUpperInvariant() switch { "TOD" => "TIME_OF_DAY", "DT" => "DATE_AND_TIME", var n => n };

    public static string Describe(StType t) => t.Kind switch
    {
        StTypeKind.AnyInt => "un entero",
        StTypeKind.AnyReal => "un real",
        StTypeKind.Array => t.ToString(),
        _ => t.Name
    };

    /// <summary>Resultado de <c>a + b</c>, <c>a * b</c>…: el más ancho; un literal toma el tipo del otro.</summary>
    public static StType Arithmetic(StType a, StType b)
    {
        if (a.IsUnknown || b.IsUnknown) return Unknown;
        if (a.Kind == StTypeKind.Time || b.Kind == StTypeKind.Time) return a.Kind == StTypeKind.Time ? a : b;
        if (a.Kind == StTypeKind.AnyInt) return b.Kind == StTypeKind.AnyInt ? AnyInt : b;
        if (b.Kind == StTypeKind.AnyInt) return a;
        if (a.Kind == StTypeKind.AnyReal) return b.Kind == StTypeKind.Real ? b : AnyReal;
        if (b.Kind == StTypeKind.AnyReal) return a.Kind == StTypeKind.Real ? a : AnyReal;
        if (a.Kind == StTypeKind.Real || b.Kind == StTypeKind.Real) return a.Name == "LREAL" || b.Name == "LREAL" ? new StType(StTypeKind.Real, "LREAL") : new StType(StTypeKind.Real, "REAL");
        if (a.Kind == StTypeKind.Integer && b.Kind == StTypeKind.Integer)
            return Integers[a.Name].Bits >= Integers[b.Name].Bits ? a : b;
        return a;
    }

    public static bool IsElementaryName(string name) => ElementaryTypes.IsElementary(name);
}
