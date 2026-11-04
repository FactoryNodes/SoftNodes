using System.Globalization;

namespace FactoryNodes.Devices;

/// <summary>
/// El cliente Modbus que usa la lib (lo da la app: <c>FactoryNodes.Modbus.ModbusTcpClient</c>, o uno falso en los tests).
/// Direcciones de la trama (ya sin <see cref="DeviceProtocol.RegisterBase"/>).
/// </summary>
public interface IModbusClient
{
    /// <summary>Función 03 (holding registers).</summary>
    Task<ushort[]> ReadHoldingRegistersAsync(byte unit, ushort address, ushort count, CancellationToken cancellation = default);

    /// <summary>Función 16 (varios registros; también vale para uno).</summary>
    Task WriteMultipleRegistersAsync(byte unit, ushort address, ushort[] values, CancellationToken cancellation = default);
}

/// <summary>
/// Lo que necesita la caja para hablar con un equipo por EtherNet/IP (PENDIENTES 45): leer y escribir atributos por clase/instancia/atributo
/// (Get/Set_Attribute_Single). El de verdad es <c>CipDeviceAttributes</c> de <c>FactoryNodes.Monitoring.Cip</c>.
/// </summary>
public interface IDeviceAttributeClient
{
    /// <summary>Los bytes de cada atributo, en el orden pedido (null y el motivo si ese no se pudo leer).</summary>
    Task<IReadOnlyList<(byte[]? Bytes, string Problem)>> ReadAttributesAsync(IReadOnlyList<(int Class, int Instance, int Attribute)> attributes,
        CancellationToken cancellation = default);

    Task WriteAttributeAsync((int Class, int Instance, int Attribute) attribute, byte[] value, CancellationToken cancellation = default);
}

/// <summary>De los registros de un parámetro a su valor y al revés.</summary>
public static class DeviceValues
{
    /// <summary>El valor en crudo (lo que se guarda en las copias: sin escala, sin perder nada).</summary>
    public static long Raw(DeviceParameter parameter, ReadOnlySpan<ushort> registers, DeviceWordOrder order)
    {
        switch (parameter.Type)
        {
            case DeviceValueType.UINT: return registers[0];
            case DeviceValueType.INT: return (short)registers[0];
        }

        var (high, low) = order == DeviceWordOrder.HighFirst ? (registers[0], registers[1]) : (registers[1], registers[0]);
        var bits = ((uint)high << 16) | low;
        return parameter.Type switch
        {
            DeviceValueType.DINT => (int)bits,
            DeviceValueType.REAL => BitConverter.SingleToInt32Bits(BitConverter.Int32BitsToSingle((int)bits)),   // los bits tal cual
            _ => bits
        };
    }

    /// <summary>El crudo de un parámetro con los bytes de su atributo EtherNet/IP (little-endian; los de 16 bits, los dos primeros).</summary>
    public static long RawOfBytes(DeviceParameter parameter, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < parameter.CipBytes)
            throw new FormatException($"{parameter.Code}: el equipo dio {bytes.Length} bytes y un {parameter.Type} necesita {parameter.CipBytes}.");
        return parameter.Type switch
        {
            DeviceValueType.UINT => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            DeviceValueType.INT => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes),
            DeviceValueType.DINT or DeviceValueType.REAL => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes),   // REAL: sus bits
            _ => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes)
        };
    }

    /// <summary>Lo contrario: los bytes que se escriben en su atributo.</summary>
    public static byte[] BytesOf(DeviceParameter parameter, long raw)
    {
        var bytes = new byte[parameter.CipBytes];
        if (bytes.Length == 2) System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)(raw & 0xFFFF));
        else System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)(raw & 0xFFFFFFFF));
        return bytes;
    }

    public static ushort[] Registers(DeviceParameter parameter, long raw, DeviceWordOrder order)
    {
        if (parameter.Registers == 1) return [(ushort)(raw & 0xFFFF)];
        var bits = (uint)(raw & 0xFFFFFFFF);
        var (high, low) = ((ushort)(bits >> 16), (ushort)(bits & 0xFFFF));
        return order == DeviceWordOrder.HighFirst ? [high, low] : [low, high];
    }

    /// <summary>
    /// El parámetro con los decimales que le da otro (<see cref="DeviceParameter.DecimalsFrom"/>), si se sabe lo que vale ese
    /// otro (<paramref name="rawOf"/>, por código; null = sin leer). Si no los tiene o no se sabe, el mismo parámetro. Para
    /// enseñarlo, escribirlo con su coma y llevarlo al PLC: los crudos no cambian.
    /// </summary>
    public static DeviceParameter Resolve(DeviceParameter parameter, Func<string, long?> rawOf)
    {
        if (parameter.DecimalsFrom is not { } from || rawOf(from.From) is not { } raw || from.Of(raw) is not { } decimals || decimals == 0)
            return parameter;
        return parameter.WithScale(parameter.Scale / Math.Pow(10, decimals));
    }

    /// <summary><see cref="Resolve"/> con lo leído (las lecturas buenas, por código).</summary>
    public static DeviceParameter Resolve(DeviceParameter parameter, IEnumerable<DeviceReading> readings) =>
        Resolve(parameter, code => readings.FirstOrDefault(r => r.Ok && r.Parameter.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Raw);

    /// <summary>El valor con su escala (12,5 s).</summary>
    public static double Value(DeviceParameter parameter, long raw) =>
        parameter.Type == DeviceValueType.REAL ? BitConverter.Int32BitsToSingle((int)raw) * parameter.Scale : raw * parameter.Scale;

    /// <summary>De un valor con escala al crudo (null si no cabe en su tipo).</summary>
    public static long? RawOf(DeviceParameter parameter, double value)
    {
        if (parameter.Type == DeviceValueType.REAL) return BitConverter.SingleToInt32Bits((float)(value / parameter.Scale));
        var raw = Math.Round(value / parameter.Scale);
        (double Min, double Max) range = parameter.Type switch
        {
            DeviceValueType.UINT => (0, ushort.MaxValue),
            DeviceValueType.INT => (short.MinValue, short.MaxValue),
            DeviceValueType.UDINT => (0, uint.MaxValue),
            _ => (int.MinValue, int.MaxValue)
        };
        var (min, max) = range;
        return raw < min || raw > max ? null : (long)raw;
    }

    /// <summary>Como se enseña: «12,5 s», o el texto de su opción («1 · Potenciómetro»).</summary>
    public static string Text(DeviceParameter parameter, long raw, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (parameter.Options is { } options && options.TryGetValue(raw.ToString(CultureInfo.InvariantCulture), out var option))
            return $"{raw} · {option}";
        if (parameter.Bits is { Count: > 0 } bits)
        {
            var on = bits.Where(b => int.TryParse(b.Key, out var bit) && bit is >= 0 and < 32 && (raw >> bit & 1) == 1).Select(b => b.Value).ToList();
            return $"16#{raw:X4}" + (on.Count > 0 ? " · " + string.Join(" · ", on) : "");
        }

        var value = Value(parameter, raw);
        var decimals = parameter.Type == DeviceValueType.REAL ? 3 : Math.Max(0, (int)Math.Ceiling(-Math.Log10(Math.Abs(parameter.Scale)) - 1e-9));
        var text = value.ToString("F" + decimals, culture);
        return parameter.Unit.Length > 0 ? $"{text} {parameter.Unit}" : text;
    }
}

/// <summary>Lo leído de un parámetro: el crudo, o el error de esa lectura.</summary>
public sealed record DeviceReading(DeviceParameter Parameter, long? Raw, string Problem = "")
{
    public bool Ok => Raw is not null;
}

/// <summary>
/// Lee los parámetros de un equipo: en bloques de registros seguidos (hasta <see cref="DeviceProtocol.MaxRegistersPerRead"/>)
/// y, si un bloque falla (hay equipos que no aceptan leer huecos), uno a uno, para que un parámetro malo no se lleve a
/// los demás.
/// </summary>
public static class DeviceReader
{
    public static async Task<IReadOnlyList<DeviceReading>> ReadAsync(IModbusClient client, byte unit, DeviceModel model,
        IEnumerable<DeviceParameter> parameters, CancellationToken cancellation = default)
    {
        var wanted = parameters.Where(p => p.AddressNumber is not null).OrderBy(model.PduAddress).ToList();
        var result = new Dictionary<DeviceParameter, DeviceReading>();
        foreach (var block in Blocks(model, wanted))
        {
            var start = model.PduAddress(block[0]);
            var count = (ushort)(model.PduAddress(block[^1]) + block[^1].Registers - start);
            try
            {
                var registers = await client.ReadHoldingRegistersAsync(unit, start, count, cancellation);
                foreach (var p in block)
                    result[p] = new DeviceReading(p, DeviceValues.Raw(p, registers.AsSpan(model.PduAddress(p) - start, p.Registers), model.Protocol.WordOrder));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch when (block.Count > 1)
            {
                foreach (var p in block) result[p] = await ReadOneAsync(client, unit, model, p, cancellation);
            }
            catch (Exception ex)
            {
                result[block[0]] = new DeviceReading(block[0], null, ex.Message);
            }
        }

        return wanted.Select(p => result[p]).ToList();
    }

    private static async Task<DeviceReading> ReadOneAsync(IModbusClient client, byte unit, DeviceModel model, DeviceParameter p, CancellationToken cancellation)
    {
        try
        {
            var registers = await client.ReadHoldingRegistersAsync(unit, model.PduAddress(p), (ushort)p.Registers, cancellation);
            return new DeviceReading(p, DeviceValues.Raw(p, registers, model.Protocol.WordOrder));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DeviceReading(p, null, ex.Message);
        }
    }

    /// <summary>
    /// Lo mismo por EtherNet/IP (PENDIENTES 45): los parámetros con dirección CIP (<see cref="DeviceParameter.Cip"/>), juntos en las peticiones que
    /// diga el cliente; un atributo que falla no se lleva a los demás. Los que no tienen dirección CIP no salen.
    /// </summary>
    public static async Task<IReadOnlyList<DeviceReading>> ReadAsync(IDeviceAttributeClient client, IEnumerable<DeviceParameter> parameters,
        CancellationToken cancellation = default)
    {
        var wanted = parameters.Where(p => p.CipAddress is not null).ToList();
        if (wanted.Count == 0) return [];
        var values = await client.ReadAttributesAsync(wanted.Select(p => p.CipAddress!.Value).ToList(), cancellation);
        return wanted.Select((p, i) =>
        {
            var (bytes, problem) = values[i];
            if (bytes is null) return new DeviceReading(p, null, problem);
            try
            {
                return new DeviceReading(p, DeviceValues.RawOfBytes(p, bytes));
            }
            catch (FormatException ex)
            {
                return new DeviceReading(p, null, ex.Message);
            }
        }).ToList();
    }

    /// <summary>Parámetros seguidos (como mucho 4 registros de hueco) que caben en una lectura.</summary>
    private static IEnumerable<List<DeviceParameter>> Blocks(DeviceModel model, List<DeviceParameter> sorted)
    {
        var block = new List<DeviceParameter>();
        foreach (var p in sorted)
        {
            if (block.Count > 0)
            {
                var start = model.PduAddress(block[0]);
                var end = model.PduAddress(block[^1]) + block[^1].Registers;
                var address = model.PduAddress(p);
                if (address < end || address - end > 4 || address + p.Registers - start > model.Protocol.MaxRegistersPerRead)
                {
                    yield return block;
                    block = [];
                }
            }

            block.Add(p);
        }

        if (block.Count > 0) yield return block;
    }
}
