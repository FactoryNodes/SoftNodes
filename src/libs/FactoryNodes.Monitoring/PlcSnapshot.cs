using System.Collections.Concurrent;

namespace FactoryNodes.Monitoring;

/// <summary>
/// Instantánea del estado del PLC: el último valor conocido de cada variable. Es el contrato común de las vistas en vivo
/// y del futuro diagnóstico (docs/INTEGRATION.md): da igual si los valores llegan por OPC-UA o de preguntar al operario.
/// Se puede actualizar desde el hilo de las suscripciones y leer desde el de la interfaz.
/// </summary>
public sealed class PlcSnapshot
{
    private readonly ConcurrentDictionary<PlcPath, PlcValue> _values = new();

    /// <summary>Hora del último valor recibido.</summary>
    public DateTimeOffset LastUpdate { get; private set; }

    /// <summary>Se lanza tras aplicar valores, con los que han llegado.</summary>
    public event EventHandler<IReadOnlyList<PlcValue>>? Changed;

    public void Apply(IReadOnlyList<PlcValue> values)
    {
        foreach (var value in values) _values[value.Path] = value;
        if (values.Count > 0) LastUpdate = values.Max(v => v.Timestamp);
        Changed?.Invoke(this, values);
    }

    public PlcValue? Get(PlcPath path) => _values.TryGetValue(path, out var value) ? value : null;

    /// <summary>El valor si es bueno y del tipo pedido; si no, <paramref name="fallback"/>.</summary>
    public T Value<T>(PlcPath path, T fallback) => Get(path) is { IsGood: true, Value: T value } ? value : fallback;

    public IReadOnlyCollection<PlcValue> Values => _values.Values.ToList();

    public void Clear()
    {
        _values.Clear();
        LastUpdate = default;
    }
}
