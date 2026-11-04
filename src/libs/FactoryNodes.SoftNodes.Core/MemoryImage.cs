using System.Globalization;
using FactoryNodes.SoftNodes.Runtime;

namespace FactoryNodes.SoftNodes;

/// <summary>
/// La memoria de un programa como pares ruta/valor: cada variable elemental con su nombre completo
/// (<c>hw_P.Cinta211.Control.Auto_ON</c>), su tipo y su valor. Sirve para la retentiva (se guarda al apagar) y para
/// conservar lo que sigue igual al cargar un programa nuevo.
/// </summary>
public sealed class MemoryImage
{
    private readonly Dictionary<string, (string Type, string Text)> _entries;

    private MemoryImage(Dictionary<string, (string Type, string Text)> entries) => _entries = entries;

    public int Count => _entries.Count;

    /// <summary>
    /// Todas las variables elementales de los datos y de las señales físicas; con <paramref name="includeInstances"/>, también las de cada
    /// instancia de bloque (<c>M101_FB.tStop.ET</c>, lo que no publica ningún PLC) para monitorizar desde fuera (PENDIENTES 170).
    /// </summary>
    public static MemoryImage Capture(SoftProgram program, bool includeIo = true, bool includeInstances = false)
    {
        var entries = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, slot) in Leaves(program, includeIo, includeInstances)) entries[path] = (slot.Type.ToString(), Format(slot));
        return new MemoryImage(entries);
    }

    /// <summary>Solo los datos (<c>hw_P</c>): lo retentivo de un PLC; las señales vuelven a leerse de las tarjetas.</summary>
    public static MemoryImage CaptureRetentive(SoftProgram program) => Capture(program, includeIo: false);

    /// <summary>Toda la memoria: datos, señales y las internas de cada instancia de bloque (para monitorizar la caja, PENDIENTES 170).</summary>
    public static MemoryImage CaptureAll(SoftProgram program) => Capture(program, includeIo: true, includeInstances: true);

    public IReadOnlyDictionary<string, string> ToText() => _entries.ToDictionary(e => e.Key, e => $"{e.Value.Type}|{e.Value.Text}");

    public static MemoryImage FromText(IReadOnlyDictionary<string, string> saved) =>
        new(saved.Where(e => e.Value.Contains('|')).ToDictionary(
            e => e.Key, e => (e.Value[..e.Value.IndexOf('|')], e.Value[(e.Value.IndexOf('|') + 1)..]), StringComparer.OrdinalIgnoreCase));

    /// <summary>Pone en <paramref name="program"/> lo que sigue con el mismo nombre y tipo; informa de lo que no.</summary>
    public SoftLoadReport RestoreInto(SoftProgram program)
    {
        // Las internas de los bloques solo cuentan como «nuevas» si la imagen las traía (la retentiva no las lleva: no son novedad)
        var withInstances = _entries.Keys.Any(k => !k.StartsWith(program.DataName + ".", StringComparison.OrdinalIgnoreCase) &&
                                                   !k.StartsWith(program.IoName + ".", StringComparison.OrdinalIgnoreCase));
        var scope = Leaves(program, includeIo: true, includeInstances: withInstances).Select(l => l.Path).ToList();
        var leaves = Leaves(program, includeIo: true, includeInstances: true).ToDictionary(l => l.Path, l => l.Slot, StringComparer.OrdinalIgnoreCase);
        int kept = 0, changed = 0, removed = 0;
        foreach (var (path, (type, text)) in _entries)
        {
            if (!leaves.TryGetValue(path, out var slot)) { removed++; continue; }
            if (!slot.Type.ToString().Equals(type, StringComparison.OrdinalIgnoreCase) || !TryParse(slot, text, out var value)) { changed++; continue; }
            slot.Value = value;
            kept++;
        }

        return new SoftLoadReport(kept, scope.Count(k => !_entries.ContainsKey(k)), removed, changed);
    }

    private static IEnumerable<(string Path, Slot Slot)> Leaves(SoftProgram program, bool includeIo, bool includeInstances = false)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var leaf in Walk(program.DataName, program.Data, visited)) yield return leaf;
        if (includeIo)
            foreach (var leaf in Walk(program.IoName, program.Io, visited)) yield return leaf;
        if (!includeInstances) yield break;
        // Las instancias de bloque son globales (M101_FB…); su Data es una referencia a los datos ya recorridos: no se repite
        foreach (var (name, slot) in program.Globals.Where(g => g.Value.Value is FbInstance))
            foreach (var leaf in Walk(name, slot.Value, visited))
                yield return leaf;
    }

    private static IEnumerable<(string Path, Slot Slot)> Walk(string path, object value, HashSet<object> visited)
    {
        switch (value)
        {
            case StructValue structure:
                if (!visited.Add(structure)) break;
                foreach (var (name, member) in structure.Members)
                    foreach (var leaf in Walk(path + "." + name, member.Value is StructValue or ArrayValue or FbInstance ? member.Value : member, visited))
                        yield return leaf;
                break;
            case ArrayValue array:
                if (!visited.Add(array)) break;
                for (var i = 0; i < array.Items.Length; i++)
                    foreach (var leaf in Walk($"{path}[{array.Lower + i}]", array.Items[i].Value is StructValue or ArrayValue or FbInstance ? array.Items[i].Value : array.Items[i], visited))
                        yield return leaf;
                break;
            case FbInstance instance:
                if (!visited.Add(instance)) break;
                foreach (var (name, member) in instance.Vars)
                    foreach (var leaf in Walk(path + "." + name, member.Value is StructValue or ArrayValue or FbInstance ? member.Value : member, visited))
                        yield return leaf;
                break;
            case Slot slot:
                yield return (path, slot);
                break;
        }
    }

    private static string Format(Slot slot) => slot.Value switch
    {
        bool b => b ? "TRUE" : "FALSE",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        long n => n.ToString(CultureInfo.InvariantCulture),
        _ => slot.Value?.ToString() ?? ""
    };

    private static bool TryParse(Slot slot, string text, out object value)
    {
        value = slot.Value;
        switch (slot.Type.Kind)
        {
            case StTypeKind.Bool:
                if (!bool.TryParse(text, out var b)) return false;
                value = b;
                return true;
            case StTypeKind.Int or StTypeKind.Time:
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return false;
                value = n;
                return true;
            case StTypeKind.Real:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return false;
                value = d;
                return true;
            case StTypeKind.String:
                value = text;
                return true;
            default:
                return false;
        }
    }
}
