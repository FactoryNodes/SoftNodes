using System.Buffers.Binary;
using FactoryNodes.Iec61131.Emission;
using FactoryNodes.SoftNodes.Runtime;

namespace FactoryNodes.SoftNodes.Modbus;

/// <summary>
/// Contesta a las peticiones Modbus (la PDU: función + datos, sin la cabecera MBAP) con la memoria del programa y su mapa
/// (<see cref="ProgramModel.Modbus"/>, PENDIENTES 199). Sin red: así se prueba a secas. Funciones 01, 02, 03, 04, 05, 06, 15 y 16.
/// <list type="bullet">
/// <item>Una dirección que no está en el mapa contesta con la excepción 02, como un equipo de verdad, aunque el resto del bloque sí esté.</item>
/// <item>Las entradas discretas y los input registers son de solo lectura: no hay función que los escriba.</item>
/// <item>Un valor de 2 o 4 registros se escribe entero o nada (excepción 02 si la escritura lo coge a medias); leerlo a medias sí se puede.</item>
/// </list>
/// La memoria se toca con <c>lock (program.Sync)</c>, como el servidor OPC UA y el de EtherNet/IP.
/// </summary>
public sealed class ModbusRequestHandler
{
    public const byte ReadCoils = 0x01, ReadDiscreteInputs = 0x02, ReadHoldingRegisters = 0x03, ReadInputRegisters = 0x04,
        WriteSingleCoil = 0x05, WriteSingleRegister = 0x06, WriteMultipleCoils = 0x0F, WriteMultipleRegisters = 0x10;

    public const byte IllegalFunction = 0x01, IllegalDataAddress = 0x02, IllegalDataValue = 0x03, DeviceFailure = 0x04;

    private const int MaxReadBits = 2000, MaxReadRegisters = 125, MaxWriteBits = 1968, MaxWriteRegisters = 123;

    private readonly SoftProgram _program;
    private readonly bool _lowWordFirst;

    /// <summary>Por zona: dirección → (casilla, cuál de sus palabras es esta dirección, cuántas tiene, su primera dirección).</summary>
    private readonly Dictionary<ModbusArea, Dictionary<int, Cell>> _cells = new()
    {
        [ModbusArea.Coil] = [], [ModbusArea.DiscreteInput] = [], [ModbusArea.HoldingRegister] = [], [ModbusArea.InputRegister] = []
    };

    private sealed record Cell(Slot Slot, int Word, int Words, int Start, ProgramModbusEntry Entry);

    public ModbusRequestHandler(SoftProgram program, ProgramModbus? map)
    {
        _program = program;
        _lowWordFirst = map?.LowWordFirst ?? false;
        if (map is null) return;
        lock (program.Sync)
            foreach (var entry in map.Entries)
                Add(entry);
    }

    /// <summary>Lo que del mapa no se puede servir (variable que no existe, tipo que no cabe, direcciones que se pisan), en palabras.</summary>
    public IReadOnlyList<string> Problems => _problems;

    private readonly List<string> _problems = [];

    /// <summary>Cuántas variables del mapa se sirven.</summary>
    public int Served { get; private set; }

    private void Add(ProgramModbusEntry entry)
    {
        var where = $"{Name(entry.Area)} {entry.Address} («{entry.Variable}»)";
        Slot slot;
        try
        {
            slot = _program.Find(entry.Variable);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _problems.Add($"{where}: la variable no está en el programa.");
            return;
        }

        var words = WordsOf(slot.Type, ModbusSizes.IsBitArea(entry.Area));
        if (words == 0)
        {
            _problems.Add(ModbusSizes.IsBitArea(entry.Area)
                ? $"{where}: en {Name(entry.Area)} solo van BOOL; es {slot.Type.Name}."
                : $"{where}: el tipo {slot.Type.Name} no se sirve por Modbus.");
            return;
        }

        if (entry.Address < 0 || entry.Address + words - 1 > ushort.MaxValue)
        {
            _problems.Add($"{where}: la dirección se sale de 0…65535.");
            return;
        }

        var cells = _cells[entry.Area];
        for (var i = 0; i < words; i++)
            if (cells.TryGetValue(entry.Address + i, out var taken))
            {
                _problems.Add($"{where}: la dirección {entry.Address + i} ya es de «{taken.Entry.Variable}».");
                return;
            }

        for (var i = 0; i < words; i++) cells[entry.Address + i] = new Cell(slot, i, words, entry.Address, entry);
        Served++;
    }

    /// <summary>Contesta a una PDU. La respuesta es otra PDU (la de error: función | 0x80 y el código de excepción).</summary>
    public byte[] Handle(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length == 0) return [0x80, IllegalFunction];
        var function = pdu[0];
        if (pdu.Length < 5) return Error(function, function is >= 1 and <= 6 or 0x0F or 0x10 ? IllegalDataValue : IllegalFunction);
        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var value = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        lock (_program.Sync)
        {
            return function switch
            {
                ReadCoils => ReadBits(function, ModbusArea.Coil, address, value),
                ReadDiscreteInputs => ReadBits(function, ModbusArea.DiscreteInput, address, value),
                ReadHoldingRegisters => ReadWords(function, ModbusArea.HoldingRegister, address, value),
                ReadInputRegisters => ReadWords(function, ModbusArea.InputRegister, address, value),
                WriteSingleCoil => WriteCoil(pdu, address, value),
                WriteSingleRegister => WriteWords(pdu, address, [value], pdu[..5].ToArray()),
                WriteMultipleCoils => WriteCoils(pdu, address, value),
                WriteMultipleRegisters => WriteRegisters(pdu, address, value),
                _ => Error(function, IllegalFunction)
            };
        }
    }

    // ---------- Bits ----------

    private byte[] ReadBits(byte function, ModbusArea area, int address, int count)
    {
        if (count is < 1 or > MaxReadBits) return Error(function, IllegalDataValue);
        var cells = _cells[area];
        var answer = new byte[2 + (count + 7) / 8];
        answer[0] = function;
        answer[1] = (byte)(answer.Length - 2);
        for (var i = 0; i < count; i++)
        {
            if (!cells.TryGetValue(address + i, out var cell)) return Error(function, IllegalDataAddress);
            if (cell.Slot.Value is true != cell.Entry.Negated) answer[2 + i / 8] |= (byte)(1 << (i % 8));
        }

        return answer;
    }

    private byte[] WriteCoil(ReadOnlySpan<byte> pdu, int address, int value)
    {
        if (value is not (0x0000 or 0xFF00)) return Error(WriteSingleCoil, IllegalDataValue);
        if (!_cells[ModbusArea.Coil].TryGetValue(address, out var cell)) return Error(WriteSingleCoil, IllegalDataAddress);
        cell.Slot.Value = value == 0xFF00 != cell.Entry.Negated;
        return pdu[..5].ToArray();
    }

    private byte[] WriteCoils(ReadOnlySpan<byte> pdu, int address, int count)
    {
        if (count is < 1 or > MaxWriteBits || pdu.Length < 6 || pdu[5] != (count + 7) / 8 || pdu.Length < 6 + pdu[5])
            return Error(WriteMultipleCoils, IllegalDataValue);
        var cells = _cells[ModbusArea.Coil];
        for (var i = 0; i < count; i++)
            if (!cells.ContainsKey(address + i)) return Error(WriteMultipleCoils, IllegalDataAddress);
        for (var i = 0; i < count; i++) cells[address + i].Slot.Value = (pdu[6 + i / 8] & (1 << (i % 8))) != 0 != cells[address + i].Entry.Negated;
        return pdu[..5].ToArray();
    }

    // ---------- Registros ----------

    private byte[] ReadWords(byte function, ModbusArea area, int address, int count)
    {
        if (count is < 1 or > MaxReadRegisters) return Error(function, IllegalDataValue);
        var cells = _cells[area];
        var answer = new byte[2 + count * 2];
        answer[0] = function;
        answer[1] = (byte)(count * 2);
        for (var i = 0; i < count; i++)
        {
            if (!cells.TryGetValue(address + i, out var cell)) return Error(function, IllegalDataAddress);
            BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(2 + i * 2), WordsOf(cell)[cell.Word]);
        }

        return answer;
    }

    private byte[] WriteRegisters(ReadOnlySpan<byte> pdu, int address, int count)
    {
        if (count is < 1 or > MaxWriteRegisters || pdu.Length < 6 || pdu[5] != count * 2 || pdu.Length < 6 + count * 2)
            return Error(WriteMultipleRegisters, IllegalDataValue);
        var values = new ushort[count];
        for (var i = 0; i < count; i++) values[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu[(6 + i * 2)..]);
        return WriteWords(pdu, address, values, pdu[..5].ToArray());
    }

    /// <summary>Escribe registros holding: todos o ninguno; cada valor de varias palabras, entero.</summary>
    private byte[] WriteWords(ReadOnlySpan<byte> pdu, int address, ushort[] values, byte[] answer)
    {
        var function = pdu[0];
        var cells = _cells[ModbusArea.HoldingRegister];
        var touched = new List<(Cell Cell, ushort[] Words)>();
        for (var i = 0; i < values.Length; i++)
        {
            if (!cells.TryGetValue(address + i, out var cell)) return Error(function, IllegalDataAddress);
            if (cell.Word != 0 || i + cell.Words > values.Length) return Error(function, IllegalDataAddress);   // un valor a medias
            touched.Add((cell, values[i..(i + cell.Words)]));
            i += cell.Words - 1;
        }

        var decoded = new List<(Slot Slot, object Value)>();
        foreach (var (cell, words) in touched)
        {
            try
            {
                var value = Decode(cell.Slot.Type, words);
                decoded.Add((cell.Slot, cell.Entry.Negated && value is bool b ? !b : value));
            }
            catch (Exception e) when (e is InvalidCastException or OverflowException or ArgumentException)
            {
                return Error(function, IllegalDataValue);
            }
        }

        foreach (var (slot, value) in decoded) slot.Value = value;
        return answer;
    }

    // ---------- Valores ----------

    /// <summary>Cuántas direcciones ocupa una casilla de este tipo (0 = no se sirve).</summary>
    public static int WordsOf(StType type, bool bitArea)
    {
        if (bitArea) return type.Kind == StTypeKind.Bool ? 1 : 0;
        return type.Kind switch
        {
            StTypeKind.Bool => 1,
            StTypeKind.Int => type.Bits <= 16 ? 1 : type.Bits <= 32 ? 2 : 4,
            StTypeKind.Real => type.Is32BitReal ? 2 : 4,
            StTypeKind.Time => 2,
            _ => 0
        };
    }

    /// <summary>Las palabras del valor, en el orden de la trama (la alta primero salvo <see cref="ProgramModbus.LowWordFirst"/>).</summary>
    private ushort[] WordsOf(Cell cell)
    {
        var slot = cell.Slot;
        ulong bits = slot.Type.Kind switch
        {
            StTypeKind.Bool => slot.Value is true != cell.Entry.Negated ? 1UL : 0UL,
            StTypeKind.Real when slot.Type.Is32BitReal => (uint)BitConverter.SingleToInt32Bits((float)(double)slot.Value),
            StTypeKind.Real => (ulong)BitConverter.DoubleToInt64Bits((double)slot.Value),
            _ => unchecked((ulong)(long)slot.Value)
        };
        var words = new ushort[cell.Words];
        for (var i = 0; i < cell.Words; i++)   // i = 0, la palabra más alta
            words[i] = (ushort)(bits >> (16 * (cell.Words - 1 - i)));
        if (_lowWordFirst) Array.Reverse(words);
        return words;
    }

    private object Decode(StType type, ushort[] words)
    {
        if (_lowWordFirst) words = words.Reverse().ToArray();
        ulong bits = 0;
        foreach (var word in words) bits = (bits << 16) | word;
        object value = type.Kind switch
        {
            StTypeKind.Bool => bits != 0,
            StTypeKind.Real when type.Is32BitReal => (double)BitConverter.Int32BitsToSingle((int)(uint)bits),
            StTypeKind.Real => BitConverter.Int64BitsToDouble((long)bits),
            StTypeKind.Int when type.Signed => SignExtend(bits, type.Bits),
            StTypeKind.Time => (long)(int)(uint)bits,
            _ => unchecked((long)bits)
        };
        return StInterpreter.Coerce(value, type);
    }

    private static long SignExtend(ulong bits, int width) =>
        width >= 64 ? unchecked((long)bits) : (long)(bits << (64 - width)) >> (64 - width);

    private static byte[] Error(byte function, byte code) => [(byte)(function | 0x80), code];

    public static string Name(ModbusArea area) => area switch
    {
        ModbusArea.Coil => "bobina",
        ModbusArea.DiscreteInput => "entrada discreta",
        ModbusArea.HoldingRegister => "holding register",
        _ => "input register"
    };
}
