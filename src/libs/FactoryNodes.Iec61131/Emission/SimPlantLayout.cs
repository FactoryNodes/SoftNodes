using System.Globalization;

namespace FactoryNodes.Iec61131.Emission;

/// <summary>
/// Dónde va cada entrada simulada dentro de <c>Sim</c> (PENDIENTES 136; autor, 2026-10-28: «se pueden evitar esos booleanos y agruparlos en words»,
/// «con AND's igual es más standard, para todos los plc's»): un bit en <c>Sim.Used[palabra]</c> (si la da SoftNodes) y su valor, un bit en
/// <c>Sim.Bits[palabra]</c> si es BOOL o un elemento del array de su tipo (<c>Sim.Ints[i]</c>, <c>Sim.Reals[i]</c>…). Las mismas cuentas para el
/// generador (la sección <c>SimPlant</c> y el tipo <c>UDT_Sim</c>) y para SoftNodes (lo que escribe): salen del orden de
/// <see cref="ProgramModel.SimInputs"/>.
/// </summary>
public sealed class SimPlantLayout
{
    /// <summary>Bits de una palabra.</summary>
    public const int WordBits = 16;

    /// <summary>Los bits de <c>Used</c>: uno por entrada, en el orden de <see cref="ProgramModel.SimInputs"/>.</summary>
    public const string UsedMember = "Used";

    /// <summary>Los valores BOOL, empaquetados en palabras.</summary>
    public const string BitsMember = "Bits";

    private SimPlantLayout(IReadOnlyList<SimPlantSlot> slots, IReadOnlyList<SimPlantArray> arrays)
    {
        Slots = slots;
        Arrays = arrays;
    }

    /// <summary>Cada entrada con su sitio.</summary>
    public IReadOnlyList<SimPlantSlot> Slots { get; }

    /// <summary>Los miembros de <c>UDT_Sim</c>, en orden: <c>Used</c>, <c>Bits</c> (si hay alguna BOOL) y uno por cada otro tipo.</summary>
    public IReadOnlyList<SimPlantArray> Arrays { get; }

    /// <summary>Palabras de <c>Used</c>.</summary>
    public int UsedWords => Words(Slots.Count);

    public static SimPlantLayout Of(IReadOnlyList<SimPlantInput> inputs)
    {
        var slots = new List<SimPlantSlot>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var member = MemberFor(input.DataType);
            if (!counts.TryGetValue(member, out var index)) order.Add(member);
            counts[member] = index + 1;
            slots.Add(new SimPlantSlot(input, i / WordBits, i % WordBits, member, member == BitsMember ? index / WordBits : index,
                member == BitsMember ? index % WordBits : -1));
        }

        var arrays = new List<SimPlantArray>();
        if (inputs.Count > 0) arrays.Add(new SimPlantArray(UsedMember, "WORD", Words(inputs.Count)));
        if (counts.TryGetValue(BitsMember, out var bools)) arrays.Add(new SimPlantArray(BitsMember, "WORD", Words(bools)));
        foreach (var member in order.Where(m => m != BitsMember))
            arrays.Add(new SimPlantArray(member, inputs.First(i => MemberFor(i.DataType) == member).DataType.ToUpperInvariant(), counts[member]));
        return new SimPlantLayout(slots, arrays);
    }

    /// <summary>El miembro del valor de una entrada de ese tipo: <c>Bits</c> las BOOL; <c>Ints</c>, <c>UInts</c>, <c>Reals</c>… las demás.</summary>
    public static string MemberFor(string dataType) => dataType.ToUpperInvariant() switch
    {
        "BOOL" => BitsMember,
        "INT" => "Ints",
        "UINT" => "UInts",
        "DINT" => "DInts",
        "UDINT" => "UDInts",
        "REAL" => "Reals",
        "LREAL" => "LReals",
        "WORD" => "Words",
        "DWORD" => "DWords",
        var other => "Values_" + other
    };

    /// <summary>La máscara de un bit en ST estándar: <c>WORD#16#0020</c> para el bit 5.</summary>
    public static string Mask(int bit) => "WORD#16#" + (1 << bit).ToString("X4", CultureInfo.InvariantCulture);

    private static int Words(int bits) => (bits + WordBits - 1) / WordBits;
}

/// <summary>Un miembro de <c>UDT_Sim</c>: <c>Used : ARRAY[0..25] OF WORD</c>.</summary>
public sealed record SimPlantArray(string Name, string ElementType, int Length);

/// <summary>
/// Dónde va una entrada: su bit en <c>Used[UseWord]</c> y su valor en <c>ValueMember[ValueIndex]</c> (con <see cref="ValueBit"/> si es BOOL, un bit
/// de esa palabra de <c>Bits</c>; −1 en las demás).
/// </summary>
public sealed record SimPlantSlot(SimPlantInput Input, int UseWord, int UseBit, string ValueMember, int ValueIndex, int ValueBit)
{
    public bool IsBit => ValueBit >= 0;
}
