namespace FactoryNodes.StEditing.Semantics;

/// <summary>
/// Un nodo del árbol de lo que ve el cuerpo: un grupo («Variables», «Constantes»; sin <see cref="Path"/>), una variable o constante
/// suelta, o un miembro de una estructura. <see cref="Path"/> es el texto que se escribe en el código (<c>Data.Control.Stop</c>).
/// </summary>
public sealed record StScopeNode(string Name, string Path, string TypeText, string? Comment, IReadOnlyList<StScopeNode> Children)
{
    public bool IsGroup => Path.Length == 0;

    /// <summary>Un grupo que empieza plegado (las constantes de cada loop); los demás grupos empiezan abiertos.</summary>
    public bool StartsCollapsed { get; init; }
}

/// <summary>
/// Cómo repartir lo que ve el cuerpo en ramas (módulo IEC, PENDIENTES 88): las variables de cada grupo con nombre (las de la sección,
/// las instancias de la sección), el resto en <see cref="OtherVariables"/> (las globales) y las constantes en <see cref="ConstantsTitle"/>,
/// cada una en la carpeta plegada que diga <see cref="ConstantFolder"/> (su loop) o suelta si da null. Un grupo vacío no sale.
/// </summary>
public sealed record StScopeLayout(IReadOnlyList<(string Title, IReadOnlySet<string> Names)> VariableGroups, string OtherVariables,
    string ConstantsTitle, Func<string, string?>? ConstantFolder = null);

/// <summary>
/// Lo mismo que enseña el autocompletado y <see cref="StScopeDescriber"/>, como árbol para el panel de variables del editor:
/// «Variables» (con sus estructuras abiertas, <c>Data</c> ▸ <c>Control</c> ▸ <c>Stop</c>) y «Constantes» (las de la POU, como las de etapa de un loop).
/// </summary>
public static class StScopeTree
{
    public const string VariablesGroup = "Variables", ConstantsGroup = "Constantes";

    /// <param name="maxDepth">Cuántos niveles de estructura se abren (<c>Data.Control.X</c> son tres).</param>
    /// <param name="layout">Las ramas; null = «Variables» y «Constantes».</param>
    public static IReadOnlyList<StScopeNode> Build(StScope scope, int maxDepth = 4, StScopeLayout? layout = null)
    {
        IReadOnlyList<StScopeNode> Members(string path, StType type, int depth)
        {
            if (depth >= maxDepth || type.Kind != StTypeKind.Struct) return [];
            return scope.MembersOf(type)
                .Select(m => new StScopeNode(m.Name, $"{path}.{m.Name}", m.TypeText, m.Comment, Members($"{path}.{m.Name}", m.Type, depth + 1)))
                .ToList();
        }

        var locals = scope.Locals.Where(l => l.Kind != StSymbolKind.ReturnValue).ToList();
        var variables = locals.Where(l => !l.IsConstant)
            .Select(l => new StScopeNode(l.Name, l.Name, l.TypeText, l.Comment, Members(l.Name, l.Type, 1))).ToList();
        var constants = locals.Where(l => l.IsConstant).OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .Select(l => new StScopeNode(l.Name, l.Name, l.TypeText, l.Comment, [])).ToList();

        var roots = new List<StScopeNode>();
        if (layout is null)
        {
            if (variables.Count > 0) roots.Add(new StScopeNode(VariablesGroup, "", "", null, variables));
            if (constants.Count > 0) roots.Add(new StScopeNode(ConstantsGroup, "", "", null, constants));
            return roots;
        }

        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (title, names) in layout.VariableGroups)
        {
            var group = variables.Where(v => names.Contains(v.Name) && placed.Add(v.Name)).ToList();
            if (group.Count > 0) roots.Add(new StScopeNode(title, "", "", null, group));
        }

        var others = variables.Where(v => !placed.Contains(v.Name)).ToList();
        if (others.Count > 0) roots.Add(new StScopeNode(layout.OtherVariables, "", "", null, others));
        if (constants.Count == 0) return roots;

        // Las constantes, cada una en la carpeta de su loop (plegada) o suelta
        var folders = constants.GroupBy(c => layout.ConstantFolder?.Invoke(c.Name), StringComparer.OrdinalIgnoreCase).ToList();
        var children = folders.Where(f => f.Key is not null).OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .Select(f => new StScopeNode(f.Key!, "", "", null, f.ToList()) { StartsCollapsed = true })
            .Concat(folders.Where(f => f.Key is null).SelectMany(f => f)).ToList();
        roots.Add(new StScopeNode(layout.ConstantsTitle, "", "", null, children));
        return roots;
    }
}
