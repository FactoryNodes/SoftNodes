using System.Globalization;
using System.Text.Json;

namespace FactoryNodes.Monitoring;

/// <summary>
/// Una variable encontrada al explorar el PLC (no solo las del proyecto: todo lo que publica el servidor).
/// </summary>
/// <param name="Id">Identificador de texto dentro del espacio de nombres de las variables (TIA <c>"hw_P"."CT01"."StartDelay"</c>, Sysmac <c>CT01.StartDelay</c>).</param>
/// <param name="Tree">Su sitio en el árbol del servidor, desde «Objects» (<c>PLC › DataBlocksGlobal › hw_P › CT01 › StartDelay</c>).</param>
/// <param name="DataType">Tipo simple de OPC UA (<c>Boolean</c>, <c>UInt16</c>, <c>Float</c>…); con <c>[]</c> si es un array de ese tipo.</param>
/// <param name="Writable">El servidor deja escribirla.</param>
public sealed record PlcNode(string Id, IReadOnlyList<string> Tree, string DataType, bool Writable);

/// <summary>El valor de una variable leída por su identificador.</summary>
public sealed record PlcNodeValue(string Id, object? Value, PlcValueStatus Status, string? Detail = null)
{
    public bool IsGood => Status == PlcValueStatus.Good;
}

/// <summary>Una escritura por identificador: el valor en texto invariante y el tipo con el que se leyó (<c>UInt16</c>, <c>Single[]</c>).</summary>
public sealed record PlcNodeWrite(string Id, string Value, string Type);

public sealed record PlcNodeWriteResult(string Id, PlcValueStatus Status, string? Detail = null)
{
    public bool Ok => Status == PlcValueStatus.Good;
}

/// <summary>Valores simples ↔ texto invariante (el del fichero de parámetros): números, booleanos, texto, fechas y arrays de ellos.</summary>
public static class PlcNodeValues
{
    private static readonly Dictionary<string, Type> Types = new[]
    {
        typeof(bool), typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long),
        typeof(ulong), typeof(float), typeof(double), typeof(string), typeof(DateTime)
    }.ToDictionary(t => t.Name);

    /// <summary><c>30</c>, <c>12.5</c>, <c>True</c>, <c>2026-10-05T08:00:00.0000000Z</c>; un array, como lista JSON de esos textos.</summary>
    public static string Format(object value) => value switch
    {
        Array array when value is not string => JsonSerializer.Serialize(array.Cast<object>().Select(Format).ToArray()),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        DateTime t => t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    /// <summary>El valor con su tipo (<c>UInt16</c>, <c>Single[]</c>), o null si el texto no vale para ese tipo.</summary>
    public static object? Parse(string text, string type)
    {
        try
        {
            if (type.EndsWith("[]", StringComparison.Ordinal))
            {
                if (!Types.TryGetValue(type[..^2], out var element)) return null;
                var items = JsonSerializer.Deserialize<string[]>(text) ?? [];
                var array = Array.CreateInstance(element, items.Length);
                for (var i = 0; i < items.Length; i++) array.SetValue(Parse(items[i], element.Name) ?? throw new FormatException(), i);
                return array;
            }

            if (!Types.TryGetValue(type, out var target)) return null;
            if (target == typeof(DateTime)) return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            return Convert.ChangeType(text, target, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or JsonException)
        {
            return null;
        }
    }
}
