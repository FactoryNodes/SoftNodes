using System.Buffers.Binary;
using System.Text;
using FactoryNodes.Monitoring.Cip;
using FactoryNodes.SoftNodes.Runtime;

namespace FactoryNodes.SoftNodes.Cip;

/// <summary>
/// Contesta a las peticiones CIP sobre la memoria de un programa de SoftNodes como lo haría un Omron NJ/NX (lo comprobado con el
/// NJ101 real, <c>FactoryNodes.Monitoring.Cip/FORMAT.md</c>): Read Tag (0x4C), Write Tag (0x4D), Multiple Service Packet (0x0A) al
/// Message Router, Unconnected Send (0x52) al Connection Manager y Get_Attribute_Single (0x0E) del objeto Identity. Sin red: recibe
/// los bytes de una petición y devuelve los de la respuesta, así se prueba a secas. Es el espejo del cliente <c>CipPlcConnection</c>.
/// </summary>
public sealed class CipRequestHandler
{
    private const byte ReadTag = 0x4C, WriteTag = 0x4D, MultipleService = 0x0A, UnconnectedSend = 0x52, GetAttributeSingle = 0x0E;
    private const byte Ok = 0x00, PathSegmentError = 0x04, ClassNotFound = 0x05, ServiceNotSupported = 0x08, InvalidParameter = 0x20,
        AttributeNotFound = 0x14, EmbeddedFailed = 0x1E, NotEnoughData = 0x13;

    private readonly SoftProgram _program;

    public CipRequestHandler(SoftProgram program, CipServerIdentity identity)
    {
        _program = program;
        Identity = identity;
    }

    public CipServerIdentity Identity { get; }

    /// <summary>La respuesta CIP (servicio | 0x80, reservado, estado general, estados adicionales, datos) a una petición.</summary>
    public byte[] Handle(ReadOnlySpan<byte> request)
    {
        if (request.Length < 2) return Reply(0, NotEnoughData, []);
        var service = request[0];
        var pathLength = request[1] * 2;
        if (request.Length < 2 + pathLength) return Reply(service, PathSegmentError, []);
        var path = request.Slice(2, pathLength);
        var data = request[(2 + pathLength)..];
        if (!CipPath.TryParse(path, out var parsed)) return Reply(service, PathSegmentError, []);

        return service switch
        {
            MultipleService when parsed.Class == 2 && parsed.Instance == 1 => Multiple(data),
            UnconnectedSend when parsed.Class == 6 && parsed.Instance == 1 => Unwrap(data),
            GetAttributeSingle => Attribute(parsed),
            ReadTag when parsed.Symbols.Count > 0 => Read(parsed),
            WriteTag when parsed.Symbols.Count > 0 => Write(parsed, data),
            ReadTag or WriteTag => Reply(service, PathSegmentError, []),
            _ => Reply(service, ServiceNotSupported, [])
        };
    }

    // ---------- Servicios ----------

    private byte[] Multiple(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return Reply(MultipleService, NotEnoughData, []);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (data.Length < 2 + 2 * count) return Reply(MultipleService, NotEnoughData, []);
        var replies = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            var start = BinaryPrimitives.ReadUInt16LittleEndian(data[(2 + 2 * i)..]);
            var end = i + 1 < count ? BinaryPrimitives.ReadUInt16LittleEndian(data[(4 + 2 * i)..]) : data.Length;
            if (start > end || end > data.Length) return Reply(MultipleService, InvalidParameter, []);
            replies.Add(Handle(data[start..end]));
        }

        var header = 2 + 2 * count;
        var body = new byte[header + replies.Sum(r => r.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)count);
        var offset = header;
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2 + 2 * i), (ushort)offset);
            replies[i].CopyTo(body, offset);
            offset += replies[i].Length;
        }

        return Reply(MultipleService, replies.All(r => r[2] == Ok) ? Ok : EmbeddedFailed, body);
    }

    /// <summary>El NJ contesta a un Unconnected Send con la respuesta del mensaje que llevaba dentro, tal cual.</summary>
    private byte[] Unwrap(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return Reply(UnconnectedSend, NotEnoughData, []);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        if (data.Length < 4 + length) return Reply(UnconnectedSend, NotEnoughData, []);
        return Handle(data.Slice(4, length));
    }

    private byte[] Attribute(CipPath path)
    {
        if (path.Class != 1 || path.Instance != 1) return Reply(GetAttributeSingle, ClassNotFound, []);
        var identity = Identity;
        byte[]? value = path.Attribute switch
        {
            1 => Word(identity.Vendor),
            2 => Word(identity.DeviceType),
            3 => Word(identity.ProductCode),
            4 => [identity.Major, identity.Minor],
            5 => Word(0),
            6 => DWord(identity.Serial),
            7 => ShortString(identity.ProductName),
            _ => null
        };
        return value is null ? Reply(GetAttributeSingle, AttributeNotFound, []) : Reply(GetAttributeSingle, Ok, value);
    }

    private byte[] Read(CipPath path)
    {
        lock (_program.Sync)
        {
            var slot = Resolve(path);
            if (slot is null) return Reply(ReadTag, PathSegmentError, []);
            return slot.Value switch
            {
                StructValue => Reply(ReadTag, Ok, [CipTypes.Structure, 2, 0, 0]),   // el CRC de la estructura: por ahora 0 (se leen sus miembros)
                ArrayValue => Reply(ReadTag, Ok, [CipTypes.Array, 2, 0, 0]),
                FbInstance => Reply(ReadTag, PathSegmentError, []),   // las instancias no se publican, como por OPC UA
                _ => Reply(ReadTag, Ok, CipValues.Encode(slot))
            };
        }
    }

    private byte[] Write(CipPath path, ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return Reply(WriteTag, NotEnoughData, []);
        var type = data[0];
        var extra = data[1];
        if (data.Length < 4 + extra) return Reply(WriteTag, NotEnoughData, []);
        var bytes = data[(4 + extra)..];
        lock (_program.Sync)
        {
            var slot = Resolve(path);
            if (slot is null) return Reply(WriteTag, PathSegmentError, []);
            if (slot.Value is StructValue or ArrayValue or FbInstance) return Reply(WriteTag, ServiceNotSupported, []);
            try
            {
                slot.Value = CipValues.Decode(type, bytes, slot.Type);
                return Reply(WriteTag, Ok, []);
            }
            catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException or ArgumentException)
            {
                return Reply(WriteTag, InvalidParameter, []);
            }
        }
    }

    // ---------- Memoria ----------

    /// <summary>
    /// La casilla que nombra la ruta: una global por su nombre (<c>"hw_P".Lamp1.Status</c>, como direcciona TIA, con o sin comillas) o,
    /// como en Sysmac, un miembro de los grupos publicados <c>hw_*</c>/<c>io_*</c> sin el grupo delante (<c>Lamp1.Status</c>). Los
    /// índices entran en los arrays con su base. Dentro de la instancia de un bloque no se entra.
    /// </summary>
    private Slot? Resolve(CipPath path)
    {
        var names = path.Symbols.SelectMany(s => s.Name.Split('.')).Select(n => n.Trim('"')).Where(n => n.Length > 0).ToList();
        var indexes = path.Symbols.Select(s => s.Indexes).ToList();
        if (names.Count == 0) return null;

        foreach (var start in Starts(names[0]))
        {
            var slot = start;
            var ok = true;
            var indexAt = 0;
            for (var i = 0; i < names.Count && ok; i++)
            {
                if (i > 0)
                {
                    if (slot.Value is StructValue structure && structure.Members.TryGetValue(names[i], out var member)) slot = member;
                    else { ok = false; break; }
                }

                // los índices van con el símbolo que los lleva; un símbolo con puntos (A.B) solo puede llevarlos al final
                if (indexAt < indexes.Count && IsLastOfSymbol(path, names, i, ref indexAt, out var list))
                    foreach (var index in list)
                    {
                        if (slot.Value is ArrayValue array && index - array.Lower is var k && k >= 0 && k < array.Items.Length) slot = array.Items[k];
                        else { ok = false; break; }
                    }
            }

            if (ok) return slot;
        }

        return null;
    }

    /// <summary>Casa cada nombre con su símbolo original (un símbolo <c>A.B</c> son dos nombres y sus índices van tras el último).</summary>
    private static bool IsLastOfSymbol(CipPath path, List<string> names, int nameIndex, ref int symbolIndex, out IReadOnlyList<int> indexes)
    {
        var consumed = 0;
        for (var s = 0; s < path.Symbols.Count; s++)
        {
            var parts = path.Symbols[s].Name.Split('.').Count(p => p.Trim('"').Length > 0);
            consumed += parts;
            if (nameIndex == consumed - 1)
            {
                symbolIndex = s + 1;
                indexes = path.Symbols[s].Indexes;
                return true;
            }
        }

        indexes = [];
        return false;
    }

    private IEnumerable<Slot> Starts(string name)
    {
        if (_program.Globals.TryGetValue(name, out var global)) yield return global;
        foreach (var root in _program.Globals)
            if (root.Value.Value is StructValue structure &&
                (root.Key.StartsWith("hw_", StringComparison.OrdinalIgnoreCase) || root.Key.StartsWith("io_", StringComparison.OrdinalIgnoreCase)) &&
                structure.Members.TryGetValue(name, out var member))
                yield return member;
    }

    // ---------- Bytes ----------

    private static byte[] Reply(byte service, byte status, byte[] data)
    {
        var reply = new byte[4 + data.Length];
        reply[0] = (byte)(service | 0x80);
        reply[2] = status;
        data.CopyTo(reply, 4);
        return reply;
    }

    private static byte[] Word(ushort value) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, value); return b; }

    private static byte[] DWord(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); return b; }

    internal static byte[] ShortString(string text)
    {
        var ascii = Encoding.ASCII.GetBytes(text.Length > 32 ? text[..32] : text);
        return [(byte)ascii.Length, .. ascii];
    }
}

/// <summary>Quién dice ser el servidor en ListIdentity y en el objeto Identity (clase 1).</summary>
/// <param name="Vendor">Identificador ODVA del fabricante. FactoryNodes no tiene uno: 0 (reservado, ningún equipo real lo usa).</param>
public sealed record CipServerIdentity(ushort Vendor, ushort DeviceType, ushort ProductCode, byte Major, byte Minor, uint Serial, string ProductName)
{
    /// <summary>SoftNodes: fabricante 0, tipo 14 (controlador programable), producto 1, revisión 1.00; la serie sale del nombre de la instancia.</summary>
    public static CipServerIdentity SoftNodes(string instanceName) =>
        new(0, 0x0E, 1, 1, 0, (uint)StringComparer.Ordinal.GetHashCode(instanceName), $"SoftNodes {instanceName}");
}

/// <summary>Una ruta CIP leída: los símbolos con sus índices y, si los lleva, clase, instancia y atributo.</summary>
public sealed class CipPath
{
    public List<(string Name, List<int> Indexes)> Symbols { get; } = [];
    public int? Class { get; private set; }
    public int? Instance { get; private set; }
    public int? Attribute { get; private set; }

    /// <summary>Segmentos simbólicos ANSI (0x91), de elemento (0x28/0x29/0x2A) y lógicos de 8 y 16 bits (clase 0x20/0x21, instancia 0x24/0x25, atributo 0x30/0x31).</summary>
    public static bool TryParse(ReadOnlySpan<byte> path, out CipPath parsed)
    {
        parsed = new CipPath();
        var i = 0;
        while (i < path.Length)
        {
            var segment = path[i];
            switch (segment)
            {
                case 0x91:
                    if (i + 1 >= path.Length) return false;
                    var length = path[i + 1];
                    if (i + 2 + length > path.Length) return false;
                    parsed.Symbols.Add((Encoding.ASCII.GetString(path.Slice(i + 2, length)), []));
                    i += 2 + length + (length % 2);
                    break;
                case 0x28:
                    if (i + 1 >= path.Length || !parsed.AddIndex(path[i + 1])) return false;
                    i += 2;
                    break;
                case 0x29:
                    if (i + 3 >= path.Length || !parsed.AddIndex(BinaryPrimitives.ReadUInt16LittleEndian(path[(i + 2)..]))) return false;
                    i += 4;
                    break;
                case 0x2A:
                    if (i + 5 >= path.Length || !parsed.AddIndex((int)BinaryPrimitives.ReadUInt32LittleEndian(path[(i + 2)..]))) return false;
                    i += 6;
                    break;
                case 0x20 or 0x24 or 0x30:
                    if (i + 1 >= path.Length) return false;
                    parsed.SetLogical(segment, path[i + 1]);
                    i += 2;
                    break;
                case 0x21 or 0x25 or 0x31:
                    if (i + 3 >= path.Length) return false;
                    parsed.SetLogical((byte)(segment & ~1), BinaryPrimitives.ReadUInt16LittleEndian(path[(i + 2)..]));
                    i += 4;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private bool AddIndex(int index)
    {
        if (Symbols.Count == 0) return false;
        Symbols[^1].Indexes.Add(index);
        return true;
    }

    private void SetLogical(byte kind, int value)
    {
        if (kind == 0x20) Class = value;
        else if (kind == 0x24) Instance = value;
        else Attribute = value;
    }
}

/// <summary>Cómo viaja cada tipo del intérprete por CIP, con los códigos y tamaños que manda el NJ101 (<c>Monitoring.Cip/FORMAT.md</c>).</summary>
internal static class CipValues
{
    /// <summary>Tipo (1 byte), longitud de la información adicional (0 en los simples) y el valor.</summary>
    public static byte[] Encode(Slot slot)
    {
        var (code, bytes) = slot.Type.Kind switch
        {
            StTypeKind.Bool => (CipTypes.Bool, new byte[] { (bool)slot.Value ? (byte)1 : (byte)0, 0 }),   // el NJ manda el BOOL en 2 bytes
            StTypeKind.Int => Integer(slot.Type, (long)slot.Value),
            StTypeKind.Real => slot.Type.Is32BitReal ? (CipTypes.Real, Float((double)slot.Value)) : (CipTypes.Lreal, Double((double)slot.Value)),
            StTypeKind.Time => (CipTypes.Lint, Long((long)slot.Value * 1_000_000)),   // TIME de Sysmac: 64 bits en nanosegundos (por comprobar en el NJ)
            StTypeKind.String => (CipTypes.String, Str((string)slot.Value)),
            _ => throw new InvalidOperationException($"El tipo {slot.Type} no se sirve por CIP.")
        };
        return [code, 0, .. bytes];
    }

    /// <summary>El valor que trae un Write Tag, convertido al tipo de la casilla (<see cref="StInterpreter.Coerce"/>).</summary>
    public static object Decode(byte type, ReadOnlySpan<byte> bytes, StType target)
    {
        object value = type switch
        {
            CipTypes.Bool => bytes.ToArray().Any(b => b != 0),
            CipTypes.Sint when bytes.Length >= 1 => (long)(sbyte)bytes[0],
            CipTypes.Usint or CipTypes.Byte when bytes.Length >= 1 => (long)bytes[0],
            CipTypes.Int when bytes.Length >= 2 => (long)BinaryPrimitives.ReadInt16LittleEndian(bytes),
            CipTypes.Uint or CipTypes.Word when bytes.Length >= 2 => (long)BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            CipTypes.Dint when bytes.Length >= 4 => (long)BinaryPrimitives.ReadInt32LittleEndian(bytes),
            CipTypes.Udint or CipTypes.Dword when bytes.Length >= 4 => (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            CipTypes.Lint when bytes.Length >= 8 => BinaryPrimitives.ReadInt64LittleEndian(bytes),
            CipTypes.Ulint or CipTypes.Lword when bytes.Length >= 8 => unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(bytes)),
            CipTypes.Real when bytes.Length >= 4 => (double)BinaryPrimitives.ReadSingleLittleEndian(bytes),
            CipTypes.Lreal when bytes.Length >= 8 => BinaryPrimitives.ReadDoubleLittleEndian(bytes),
            CipTypes.String => Text(bytes),
            _ => throw new InvalidCastException($"No se escribe un valor de tipo {CipTypes.Name(type)} ({bytes.Length} bytes).")
        };
        if (target.Kind == StTypeKind.Time && type == CipTypes.Lint) value = (long)value / 1_000_000;   // nanosegundos → milisegundos
        return StInterpreter.Coerce(value, target);
    }

    private static (byte Code, byte[] Bytes) Integer(StType type, long value)
    {
        var code = type.Name.ToUpperInvariant() switch
        {
            "BYTE" => CipTypes.Byte,
            "WORD" => CipTypes.Word,
            "DWORD" => CipTypes.Dword,
            "LWORD" => CipTypes.Lword,
            _ => (type.Bits, type.Signed) switch
            {
                (8, true) => CipTypes.Sint,
                (8, false) => CipTypes.Usint,
                (16, true) => CipTypes.Int,
                (16, false) => CipTypes.Uint,
                (32, true) => CipTypes.Dint,
                (32, false) => CipTypes.Udint,
                (_, true) => CipTypes.Lint,
                _ => CipTypes.Ulint
            }
        };
        var size = type.Bits <= 8 ? 1 : type.Bits <= 16 ? 2 : type.Bits <= 32 ? 4 : 8;
        Span<byte> full = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(full, value);   // little endian: los primeros bytes son los que caben en el tipo
        return (code, full[..size].ToArray());
    }

    private static byte[] Float(double value) { var b = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, (float)value); return b; }

    private static byte[] Double(double value) { var b = new byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(b, value); return b; }

    private static byte[] Long(long value) { var b = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, value); return b; }

    /// <summary>STRING: la longitud en una palabra y los caracteres (como lo espera el cliente; por comprobar en el NJ).</summary>
    private static byte[] Str(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        var bytes = new byte[2 + utf8.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)utf8.Length);
        utf8.CopyTo(bytes, 2);
        return bytes;
    }

    private static string Text(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            if (length <= bytes.Length - 2) return Encoding.UTF8.GetString(bytes.Slice(2, length));
        }

        var end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }
}
