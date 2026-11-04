using FactoryNodes.Iec61131.Model;

namespace FactoryNodes.StEditing.Semantics;

public enum StSymbolKind
{
    Input,
    Output,
    InOut,
    Variable,
    Constant,

    /// <summary>Constante de tamaño de la librería (<c>/// SIZE</c>).</summary>
    SizeConstant,

    /// <summary>El nombre de la FUNCTION dentro de su cuerpo: se le asigna lo que devuelve.</summary>
    ReturnValue,
    Function,
    StandardFunction,
    Conversion
}

/// <summary>Un nombre que se puede usar en el cuerpo: variable, constante, función…</summary>
public sealed record StSymbol(string Name, StSymbolKind Kind, StType Type, string TypeText, string? Comment = null)
{
    public bool IsCallable => Kind is StSymbolKind.Function or StSymbolKind.StandardFunction or StSymbolKind.Conversion;

    public bool IsConstant => Kind is StSymbolKind.Constant or StSymbolKind.SizeConstant;
}

/// <summary>Miembro de una estructura o parámetro de un bloque visto desde fuera (<c>tStart.Q</c>, <c>Data.Status</c>).</summary>
public sealed record StMember(string Name, StType Type, string TypeText, string? Comment, VarSectionKind? Direction = null);

/// <summary>
/// Lo que se ve desde el cuerpo de una POU: sus variables, las constantes de tamaño, las funciones de la librería y las
/// estándar, y los miembros de cada tipo. Todo sale de la librería maestra leída (<see cref="MasterLibrary"/>), la misma
/// que usan los emisores.
/// </summary>
public sealed class StScope
{
    private readonly Dictionary<string, StSymbol> _locals = new(StringComparer.OrdinalIgnoreCase);

    public StScope(MasterLibrary library, Pou pou)
    {
        Library = library;
        Pou = pou;
        foreach (var section in pou.Sections)
        {
            var kind = section.Kind switch
            {
                VarSectionKind.Input => StSymbolKind.Input,
                VarSectionKind.Output => StSymbolKind.Output,
                VarSectionKind.InOut => StSymbolKind.InOut,
                VarSectionKind.VarConstant => StSymbolKind.Constant,
                _ => StSymbolKind.Variable
            };
            foreach (var member in section.Members)
                _locals.TryAdd(member.Name, new StSymbol(member.Name, kind, StType.Of(member, library), TypeText(member), member.Comment));
        }

        if (pou.Kind == PouKind.Function && pou.ReturnType is { } ret)
            _locals.TryAdd(pou.Name, new StSymbol(pou.Name, StSymbolKind.ReturnValue, StType.Of(ret, library), ret, "Lo que devuelve la función"));
    }

    public MasterLibrary Library { get; }

    public Pou Pou { get; }

    /// <summary>Variables y constantes de la POU (y su valor de retorno).</summary>
    public IEnumerable<StSymbol> Locals => _locals.Values;

    public static string TypeText(MemberDeclaration m) => m.Array is { } a ? $"ARRAY[{a.Lower}..{a.Upper}] OF {m.TypeName}" : m.TypeName;

    /// <summary>Lo que significa un nombre suelto en el cuerpo, o null si no existe.</summary>
    public StSymbol? Lookup(string name)
    {
        if (_locals.TryGetValue(name, out var local)) return local;
        if (Library.Size(name) is { } size) return new StSymbol(size.Name, StSymbolKind.SizeConstant, StType.AnyInt, $"= {size.Value}", "Constante de tamaño");
        if (Library.Pou(name) is { Kind: PouKind.Function } function)
            return new StSymbol(function.Name, StSymbolKind.Function, StType.None, function.ReturnType ?? "", "Función de la librería");
        if (StandardFunctions.Get(name) is { } standard)
            return new StSymbol(standard.Name, StSymbolKind.StandardFunction, StType.None, "", standard.Description);
        if (StandardFunctions.Conversion(name) is { } conversion)
            return new StSymbol(name.ToUpperInvariant(), StSymbolKind.Conversion, StType.None, conversion.To, $"Convierte {conversion.From} en {conversion.To}");
        return null;
    }

    /// <summary>Lo que se puede usar tras un punto: miembros de una estructura, entradas y salidas de un bloque.</summary>
    public IReadOnlyList<StMember> MembersOf(StType type)
    {
        switch (type.Kind)
        {
            case StTypeKind.Struct when Library.Type(type.Name) is { } s:
                return s.Members.Select(m => new StMember(m.Name, StType.Of(m, Library), TypeText(m), m.Comment)).ToList();
            case StTypeKind.FunctionBlock:
                return ParametersOf(type.Name)
                    .Where(p => p.Direction is VarSectionKind.Input or VarSectionKind.Output)
                    .Select(p => new StMember(p.Name, StType.Of(p.TypeName, Library), p.TypeName, p.Comment, p.Direction))
                    .ToList();
            default:
                return [];
        }
    }

    /// <summary>Parámetros de un bloque de función o de una función (de la librería o estándar).</summary>
    public IReadOnlyList<StParameter> ParametersOf(string blockOrFunction)
    {
        if (Library.Pou(blockOrFunction) is { } pou)
            return pou.Sections
                .Where(s => s.Kind is VarSectionKind.Input or VarSectionKind.Output or VarSectionKind.InOut)
                .SelectMany(s => s.Members.Select(m => new StParameter(m.Name, TypeText(m), s.Kind, m.Comment)))
                .ToList();
        return StandardBlocks.Get(blockOrFunction)?.Parameters ?? [];
    }

    /// <summary>Todos los nombres sueltos que se pueden usar (para «¿quisiste decir…?» y el autocompletado).</summary>
    public IEnumerable<string> VisibleNames =>
        _locals.Keys
            .Concat(Library.Sizes.Select(s => s.Name))
            .Concat(Library.Pous.Where(p => p.Kind == PouKind.Function).Select(p => p.Name))
            .Concat(StandardFunctions.All.Select(f => f.Name));

    /// <summary>El nombre más parecido de <paramref name="candidates"/> (distancia de edición pequeña), o null.</summary>
    public static string? Suggest(string name, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        var limit = Math.Max(1, Math.Min(3, name.Length / 3));
        foreach (var candidate in candidates)
        {
            if (candidate.Equals(name, StringComparison.Ordinal)) continue;
            var d = Distance(name.ToUpperInvariant(), candidate.ToUpperInvariant());
            if (d < bestDistance && d <= limit)
            {
                bestDistance = d;
                best = candidate;
            }
        }

        return best;
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
