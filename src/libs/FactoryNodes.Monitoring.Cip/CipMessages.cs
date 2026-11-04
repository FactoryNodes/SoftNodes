using System.Buffers.Binary;
using System.Text;

namespace FactoryNodes.Monitoring.Cip;

/// <summary>
/// Cómo nombrar los miembros de una estructura en la ruta CIP (<c>Cinta.Status.HMI</c>): un segmento simbólico por tramo
/// (como Allen-Bradley) o uno solo con el nombre entero con puntos. Cuál acepta el NJ/NX, por comprobar (FORMAT.md).
/// </summary>
public enum CipMemberSegments
{
    Separate,
    Dotted
}

/// <summary>Una respuesta CIP: el estado general (0 = bien), el adicional y los datos.</summary>
internal sealed record CipReply(byte Service, byte GeneralStatus, IReadOnlyList<ushort> ExtendedStatus, byte[] Data)
{
    public bool Ok => GeneralStatus == 0;
}

/// <summary>
/// Un valor tal como viaja: el código de tipo (Omron: 1 byte, <see cref="CipTypes"/>), la información adicional (el CRC
/// de una estructura) y los bytes del valor. Se guarda tal cual para volver a escribir con el mismo tipo y tamaño.
/// </summary>
internal sealed record CipRaw(byte Type, byte[] Extra, byte[] Bytes);

/// <summary>Códigos de tipo de Omron en los mensajes de variables (Read/Write Tag). Públicos: el servidor de SoftNodes los usa al contestar.</summary>
public static class CipTypes
{
    public const byte Bool = 0xC1, Sint = 0xC2, Int = 0xC3, Dint = 0xC4, Lint = 0xC5, Usint = 0xC6, Uint = 0xC7, Udint = 0xC8,
        Ulint = 0xC9, Real = 0xCA, Lreal = 0xCB, String = 0xD0, Byte = 0xD1, Word = 0xD2, Dword = 0xD3, Lword = 0xD4,
        Structure = 0xA0, Array = 0xA3;

    public static string Name(byte type) => type switch
    {
        Bool => "BOOL", Sint => "SINT", Int => "INT", Dint => "DINT", Lint => "LINT", Usint => "USINT", Uint => "UINT",
        Udint => "UDINT", Ulint => "ULINT", Real => "REAL", Lreal => "LREAL", String => "STRING", Byte => "BYTE",
        Word => "WORD", Dword => "DWORD", Lword => "LWORD", Structure => "estructura", Array => "matriz",
        _ => $"0x{type:X2}"
    };
}

/// <summary>Cómo se arman y se leen los mensajes CIP (Read Tag 0x4C, Write Tag 0x4D, Multiple Service Packet 0x0A).</summary>
internal static class CipMessages
{
    public const byte ReadTag = 0x4C, WriteTag = 0x4D, MultipleService = 0x0A, UnconnectedSend = 0x52;

    /// <summary>Los servicios de los equipos (variadores, indicadores de pesaje…): un atributo de un objeto, sin nombre de variable.</summary>
    public const byte GetAttributeSingle = 0x0E, SetAttributeSingle = 0x10;

    /// <summary>
    /// La ruta de una variable: <c>Cinta.Elements[12].Mode</c> → segmentos simbólicos (0x91) y de elemento (0x28/0x29/0x2A)
    /// para los índices.
    /// </summary>
    public static byte[] TagPath(string name, CipMemberSegments members)
    {
        var path = new List<byte>();
        if (members == CipMemberSegments.Dotted)
        {
            AddSymbol(path, name);
            return path.ToArray();
        }

        foreach (var part in name.Split('.'))
        {
            var bracket = part.IndexOf('[');
            var symbol = bracket < 0 ? part : part[..bracket];
            if (symbol.Length == 0 || bracket >= 0 && !part.EndsWith(']'))
                throw new ArgumentException($"«{name}» no es un nombre de variable válido.", nameof(name));

            AddSymbol(path, symbol);
            if (bracket >= 0)
                foreach (var index in part[(bracket + 1)..^1].Split(',', StringSplitOptions.TrimEntries))
                    path.AddRange(ElementSegment(uint.Parse(index, System.Globalization.CultureInfo.InvariantCulture)));
        }

        return path.ToArray();
    }

    /// <summary>Segmento simbólico ANSI extendido: 0x91, longitud, caracteres y relleno a palabra.</summary>
    private static void AddSymbol(List<byte> path, string symbol)
    {
        var ascii = Encoding.ASCII.GetBytes(symbol);
        if (ascii.Length > 255) throw new ArgumentException($"«{symbol}» es demasiado largo.", nameof(symbol));
        path.Add(0x91);
        path.Add((byte)ascii.Length);
        path.AddRange(ascii);
        if (ascii.Length % 2 == 1) path.Add(0);
    }

    private static byte[] ElementSegment(uint index) => index switch
    {
        <= 0xFF => [0x28, (byte)index],
        <= 0xFFFF => [0x29, 0, (byte)index, (byte)(index >> 8)],
        _ => [0x2A, 0, (byte)index, (byte)(index >> 8), (byte)(index >> 16), (byte)(index >> 24)]
    };

    /// <summary>Una petición CIP: servicio, tamaño de la ruta en palabras, ruta y datos.</summary>
    public static byte[] Request(byte service, byte[] path, byte[] data)
    {
        if (path.Length % 2 == 1) throw new ArgumentException("La ruta CIP va en palabras.", nameof(path));
        var message = new byte[2 + path.Length + data.Length];
        message[0] = service;
        message[1] = (byte)(path.Length / 2);
        path.CopyTo(message, 2);
        data.CopyTo(message, 2 + path.Length);
        return message;
    }

    public static byte[] ReadRequest(byte[] tagPath) => Request(ReadTag, tagPath, [1, 0]);

    /// <summary>
    /// La ruta lógica de un atributo: clase (0x20), instancia (0x24) y atributo (0x30), de 8 bits; con un valor mayor de 255, la forma de 16
    /// bits (0x21/0x25/0x31, un byte de relleno y el valor en dos bytes).
    /// </summary>
    public static byte[] AttributePath(CipAttribute attribute)
    {
        var path = new List<byte>();
        void Segment(byte kind, int value)
        {
            if (value <= 0xFF) path.AddRange([kind, (byte)value]);
            else path.AddRange([(byte)(kind | 1), 0, (byte)value, (byte)(value >> 8)]);
        }

        Segment(0x20, attribute.Class);
        Segment(0x24, attribute.Instance);
        Segment(0x30, attribute.Attribute);
        return [.. path];
    }

    public static byte[] GetAttributeRequest(CipAttribute attribute) => Request(GetAttributeSingle, AttributePath(attribute), []);

    public static byte[] SetAttributeRequest(CipAttribute attribute, byte[] value) => Request(SetAttributeSingle, AttributePath(attribute), value);

    public static byte[] WriteRequest(byte[] tagPath, CipRaw value)
    {
        var data = new byte[2 + value.Extra.Length + 2 + value.Bytes.Length];
        data[0] = value.Type;
        data[1] = (byte)value.Extra.Length;
        value.Extra.CopyTo(data, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2 + value.Extra.Length), 1);
        value.Bytes.CopyTo(data, 4 + value.Extra.Length);
        return Request(WriteTag, tagPath, data);
    }

    /// <summary>Varias peticiones en una (al Message Router, clase 2 instancia 1).</summary>
    public static byte[] MultipleRequest(IReadOnlyList<byte[]> requests)
    {
        var header = 2 + 2 * requests.Count;
        var data = new byte[header + requests.Sum(r => r.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)requests.Count);
        var offset = header;
        for (var i = 0; i < requests.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2 + 2 * i), (ushort)offset);
            requests[i].CopyTo(data, offset);
            offset += requests[i].Length;
        }

        return Request(MultipleService, [0x20, 0x02, 0x24, 0x01], data);
    }

    /// <summary>
    /// Envuelve una petición en un Unconnected Send al Connection Manager (clase 6 instancia 1) con la ruta hasta la CPU
    /// (<paramref name="route"/>, p. ej. puerto 1 ranura 0 = <c>[1, 0]</c>).
    /// </summary>
    public static byte[] Routed(byte[] request, byte[] route)
    {
        var data = new List<byte> { 0x0A, 0x0E };   // tick de 2^10 ms y 14 ticks: unos 14 s
        data.AddRange(BitConverter.GetBytes((ushort)request.Length));
        data.AddRange(request);
        if (request.Length % 2 == 1) data.Add(0);
        data.Add((byte)((route.Length + 1) / 2));
        data.Add(0);
        data.AddRange(route);
        if (route.Length % 2 == 1) data.Add(0);
        return Request(UnconnectedSend, [0x20, 0x06, 0x24, 0x01], data.ToArray());
    }

    public static CipReply ParseReply(ReadOnlySpan<byte> message)
    {
        if (message.Length < 4) throw new CipException("Respuesta CIP demasiado corta.");
        var extended = new ushort[message[3]];
        for (var i = 0; i < extended.Length; i++) extended[i] = BinaryPrimitives.ReadUInt16LittleEndian(message[(4 + 2 * i)..]);
        return new CipReply(message[0], message[2], extended, message[(4 + 2 * extended.Length)..].ToArray());
    }

    /// <summary>Las respuestas de un Multiple Service Packet, en el orden de las peticiones.</summary>
    public static IReadOnlyList<CipReply> ParseMultiple(byte[] data)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var offsets = Enumerable.Range(0, count).Select(i => (int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2 + 2 * i))).ToList();
        return offsets.Select((start, i) => ParseReply(data.AsSpan(start, (i + 1 < count ? offsets[i + 1] : data.Length) - start))).ToList();
    }

    /// <summary>Los datos de un Read Tag: tipo, información adicional y valor.</summary>
    public static CipRaw ParseReadData(byte[] data)
    {
        if (data.Length < 2) throw new CipException("La lectura no trae el tipo de la variable.");
        var extra = data[1];
        if (data.Length < 2 + extra) throw new CipException("La lectura viene cortada.");
        return new CipRaw(data[0], data[2..(2 + extra)], data[(2 + extra)..]);
    }

    /// <summary>El valor en .NET, o null si el tipo no es simple (estructura, matriz: ahí se leen sus miembros).</summary>
    public static object? Decode(CipRaw raw)
    {
        var b = raw.Bytes;
        return raw.Type switch
        {
            CipTypes.Bool => b.Any(x => x != 0),
            CipTypes.Sint when b.Length >= 1 => (sbyte)b[0],
            CipTypes.Usint or CipTypes.Byte when b.Length >= 1 => b[0],
            CipTypes.Int when b.Length >= 2 => BinaryPrimitives.ReadInt16LittleEndian(b),
            CipTypes.Uint or CipTypes.Word when b.Length >= 2 => BinaryPrimitives.ReadUInt16LittleEndian(b),
            CipTypes.Dint when b.Length >= 4 => BinaryPrimitives.ReadInt32LittleEndian(b),
            CipTypes.Udint or CipTypes.Dword when b.Length >= 4 => BinaryPrimitives.ReadUInt32LittleEndian(b),
            CipTypes.Lint when b.Length >= 8 => BinaryPrimitives.ReadInt64LittleEndian(b),
            CipTypes.Ulint or CipTypes.Lword when b.Length >= 8 => BinaryPrimitives.ReadUInt64LittleEndian(b),
            CipTypes.Real when b.Length >= 4 => BinaryPrimitives.ReadSingleLittleEndian(b),
            CipTypes.Lreal when b.Length >= 8 => BinaryPrimitives.ReadDoubleLittleEndian(b),
            CipTypes.String => DecodeString(b),
            _ => null
        };
    }

    /// <summary>STRING: longitud en una palabra y los caracteres (por comprobar en el NJ; si no cuadra, hasta el primer NUL).</summary>
    private static string DecodeString(byte[] b)
    {
        if (b.Length >= 2)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(b);
            if (length <= b.Length - 2) return Encoding.UTF8.GetString(b, 2, length);
        }

        var end = Array.IndexOf(b, (byte)0);
        return Encoding.UTF8.GetString(b, 0, end < 0 ? b.Length : end);
    }

    /// <summary>
    /// El valor nuevo con el tipo y el tamaño que tenía la variable al leerla (<paramref name="read"/>). Falla si no se
    /// puede convertir.
    /// </summary>
    public static CipRaw Encode(CipRaw read, object value)
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        byte[] bytes;
        switch (read.Type)
        {
            case CipTypes.Bool:
                var on = value is string s ? s.Trim() is "1" || bool.Parse(s) : Convert.ToBoolean(value, c);
                bytes = new byte[Math.Max(1, read.Bytes.Length)];
                if (on) bytes[0] = 1;
                break;
            case CipTypes.Sint: bytes = [(byte)Convert.ToSByte(value, c)]; break;
            case CipTypes.Usint or CipTypes.Byte: bytes = [Convert.ToByte(value, c)]; break;
            case CipTypes.Int: bytes = new byte[2]; BinaryPrimitives.WriteInt16LittleEndian(bytes, Convert.ToInt16(value, c)); break;
            case CipTypes.Uint or CipTypes.Word: bytes = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, Convert.ToUInt16(value, c)); break;
            case CipTypes.Dint: bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, Convert.ToInt32(value, c)); break;
            case CipTypes.Udint or CipTypes.Dword: bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, Convert.ToUInt32(value, c)); break;
            case CipTypes.Lint: bytes = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(bytes, Convert.ToInt64(value, c)); break;
            case CipTypes.Ulint or CipTypes.Lword: bytes = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, Convert.ToUInt64(value, c)); break;
            case CipTypes.Real: bytes = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(bytes, Convert.ToSingle(value, c)); break;
            case CipTypes.Lreal: bytes = new byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(bytes, Convert.ToDouble(value, c)); break;
            case CipTypes.String:
                var text = Encoding.UTF8.GetBytes(Convert.ToString(value, c) ?? "");
                bytes = new byte[2 + text.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)text.Length);
                text.CopyTo(bytes, 2);
                break;
            default:
                throw new InvalidCastException($"No se escribe una variable de tipo {CipTypes.Name(read.Type)} entera: escribe sus miembros.");
        }

        return read with { Bytes = bytes };
    }

    /// <summary>El estado general en palabras (CIP Vol. 1, apéndice B), para los mensajes de error.</summary>
    public static string StatusText(byte status, IReadOnlyList<ushort> extended)
    {
        var text = status switch
        {
            0x00 => "bien",
            0x01 => "fallo de conexión",
            0x04 => "no existe en el PLC, no está publicada o el nombre está mal formado",
            0x05 => "no existe (la variable no está publicada en red, o el equipo no tiene ese objeto)",
            0x06 => "solo cabe parte de la respuesta",
            0x08 => "servicio no admitido",
            0x0C => "el PLC no está en un estado que lo permita",
            0x0F => "sin permiso",
            0x10 => "el dispositivo no lo permite ahora",
            0x11 => "la respuesta no cabe en un mensaje",
            0x0E => "el atributo no se puede escribir",
            0x13 => "faltan datos",
            0x14 => "el equipo no tiene ese atributo",
            0x15 => "sobran datos",
            0x16 => "el equipo no tiene ese objeto (clase o instancia)",
            0x1E => "falló alguna de las peticiones agrupadas",
            0x20 => "parámetro no válido",
            0x26 => "la ruta tiene un tamaño incorrecto",
            0xFF => "error propio del fabricante",
            _ => "error"
        };
        var more = extended.Count > 0 ? $", adicional {string.Join(" ", extended.Select(x => $"0x{x:X4}"))}" : "";
        return $"0x{status:X2} ({text}{more})";
    }

    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
}

public sealed class CipException(string message) : Exception(message);
