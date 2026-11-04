namespace FactoryNodes.Monitoring;

/// <summary>
/// Ruta neutra de una variable del programa, tramo a tramo: <c>hw_Prueba.Alveolar_001.Status.Active_Q</c>. Es la misma
/// para todas las plataformas; cada una la traduce a su identificador (<c>IPlcAddressing</c>). Dos rutas son iguales si
/// lo son sus tramos, respetando mayúsculas (así las escribe el programa generado).
/// </summary>
public sealed class PlcPath : IEquatable<PlcPath>
{
    public PlcPath(IEnumerable<string> segments)
    {
        Segments = segments.ToArray();
        if (Segments.Count == 0 || Segments.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Una ruta tiene al menos un tramo y ninguno vacío.", nameof(segments));
    }

    public IReadOnlyList<string> Segments { get; }

    /// <summary><c>"hw_P.Alveolar_001.Status.Active_Q"</c> → sus cuatro tramos.</summary>
    public static PlcPath Parse(string text) => new(text.Split('.'));

    /// <summary>Añade tramos: <c>element.Then("Status", "HMI")</c>.</summary>
    public PlcPath Then(params string[] more) => new(Segments.Concat(more));

    public override string ToString() => string.Join(".", Segments);

    public bool Equals(PlcPath? other) => other is not null && Segments.SequenceEqual(other.Segments, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as PlcPath);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var segment in Segments) hash.Add(segment, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public static bool operator ==(PlcPath? a, PlcPath? b) => a?.Equals(b) ?? b is null;

    public static bool operator !=(PlcPath? a, PlcPath? b) => !(a == b);
}
