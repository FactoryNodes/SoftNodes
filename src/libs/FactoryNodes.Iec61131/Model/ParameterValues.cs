using System.Globalization;
using System.Numerics;

namespace FactoryNodes.Iec61131.Model;

/// <summary>
/// Valores de los parámetros (docs/design/BLOCK_PARAMETERS.md) escritos por una persona: se comprueban contra el tipo IEC
/// del miembro y se dejan como literal ST neutro, el mismo que llevaría el <c>:=</c> de la librería: <c>TRUE</c>/<c>FALSE</c>,
/// enteros sin «+» ni ceros a la izquierda, REAL con punto decimal (<c>12</c> → <c>12.0</c>; se admite la coma, <c>2,5</c> → <c>2.5</c>).
/// </summary>
public static class ParameterValues
{
    private static readonly Dictionary<string, (BigInteger Min, BigInteger Max)> IntegerRanges = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SINT"] = (sbyte.MinValue, sbyte.MaxValue),
        ["INT"] = (short.MinValue, short.MaxValue),
        ["DINT"] = (int.MinValue, int.MaxValue),
        ["LINT"] = (long.MinValue, long.MaxValue),
        ["USINT"] = (byte.MinValue, byte.MaxValue),
        ["UINT"] = (ushort.MinValue, ushort.MaxValue),
        ["UDINT"] = (uint.MinValue, uint.MaxValue),
        ["ULINT"] = (ulong.MinValue, ulong.MaxValue)
    };

    /// <summary>Se puede dar valor a un miembro de este tipo (BOOL, enteros y REAL/LREAL; no WORD, TIME…).</summary>
    public static bool IsSupported(string dataType) =>
        dataType.Equals("BOOL", StringComparison.OrdinalIgnoreCase) || IntegerRanges.ContainsKey(dataType) || IsReal(dataType);

    /// <summary>El valor que tiene un miembro sin <c>:=</c>: el cero de su tipo.</summary>
    public static string ZeroOf(string dataType) =>
        dataType.Equals("BOOL", StringComparison.OrdinalIgnoreCase) ? "FALSE" : IsReal(dataType) ? "0.0" : "0";

    /// <summary>
    /// Comprueba <paramref name="text"/> contra <paramref name="dataType"/>. Si vale, <paramref name="literal"/> es el
    /// literal normalizado y devuelve null; si no, devuelve el motivo.
    /// </summary>
    public static string? Normalize(string dataType, string text, out string literal)
    {
        literal = "";
        var t = text.Trim();
        if (t.Length == 0) return "Escribe un valor.";

        if (dataType.Equals("BOOL", StringComparison.OrdinalIgnoreCase))
        {
            if (t.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || t == "1") { literal = "TRUE"; return null; }
            if (t.Equals("FALSE", StringComparison.OrdinalIgnoreCase) || t == "0") { literal = "FALSE"; return null; }
            return "Un BOOL vale TRUE o FALSE.";
        }

        if (IntegerRanges.TryGetValue(dataType, out var range))
        {
            if (!BigInteger.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                return $"Un {dataType.ToUpperInvariant()} es un número entero.";
            if (value < range.Min || value > range.Max)
                return $"Un {dataType.ToUpperInvariant()} va de {range.Min} a {range.Max}.";
            literal = value.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        if (IsReal(dataType))
        {
            var dot = t.Replace(',', '.');
            if (!double.TryParse(dot, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture, out var value) || double.IsInfinity(value) ||
                (dataType.Equals("REAL", StringComparison.OrdinalIgnoreCase) && Math.Abs(value) > float.MaxValue))
                return $"Un {dataType.ToUpperInvariant()} es un número (decimales con punto o coma).";
            literal = value.ToString("0.0###############", CultureInfo.InvariantCulture);
            return null;
        }

        return $"No se pueden dar valores a un miembro {dataType.ToUpperInvariant()}.";
    }

    /// <summary>El valor efectivo de un <c>:=</c> de la librería (o de su ausencia), normalizado para comparar.</summary>
    public static string Effective(string dataType, string? libraryDefault) =>
        libraryDefault is null ? ZeroOf(dataType) : Normalize(dataType, libraryDefault, out var l) is null ? l : libraryDefault;

    private static bool IsReal(string dataType) =>
        dataType.Equals("REAL", StringComparison.OrdinalIgnoreCase) || dataType.Equals("LREAL", StringComparison.OrdinalIgnoreCase);
}
