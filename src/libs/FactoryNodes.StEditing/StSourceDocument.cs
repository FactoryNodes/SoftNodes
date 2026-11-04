using System.Text.RegularExpressions;
using FactoryNodes.Iec61131.Model;
using FactoryNodes.Iec61131.Reading;
using FactoryNodes.Iec61131.Syntax;
using FactoryNodes.Iec61131.Validation;
using FactoryNodes.StEditing.Semantics;

namespace FactoryNodes.StEditing;

public enum StItemKind
{
    Type,
    FunctionBlock,
    Function
}

/// <summary>Un TYPE, FUNCTION_BLOCK o FUNCTION del fichero, con la línea (desde 1) de su cabecera.</summary>
public sealed record StOutlineItem(StItemKind Kind, string Name, int Line);

/// <summary>Dónde van unas declaraciones: los miembros de un TYPE, o una sección VAR de una POU.</summary>
public sealed record StContainer(string Item, VarSectionKind? Section = null);

/// <summary>
/// Una fila de una tabla de declaraciones: un miembro o variable, o un comentario en su propia línea
/// (<see cref="IsComment"/>, que sirve de separador: «// --- Núcleo común ---»). <see cref="Line"/> es la del fichero;
/// <see cref="Slot"/>, su posición si la línea declara varios nombres («a, b : BOOL;»).
/// </summary>
public sealed record StDeclarationRow(int Line, int Slot, string Name, string Type, string Initial, string Directive, string Comment, bool IsComment)
{
    public StDeclarationValues Values => new(Name, Type, Initial, Directive, Comment);
}

/// <summary>Lo que se escribe en una fila. Vacío = no lleva (sin valor inicial, sin directiva, sin comentario).</summary>
public sealed record StDeclarationValues(string Name, string Type, string Initial = "", string Directive = "", string Comment = "")
{
    /// <summary>Por qué no se puede escribir así, o null.</summary>
    public string? Problem(bool isComment)
    {
        if (Comment.Contains('\n') || Comment.Contains('\r')) return "El comentario va en una sola línea.";
        if (isComment) return null;
        if (!Regex.IsMatch(Name.Trim(), @"^[A-Za-z_]\w*$")) return $"«{Name}» no es un nombre válido: letras, números y «_», sin empezar por número.";
        if (Type.Trim().Length == 0) return $"«{Name}»: falta el tipo.";
        foreach (var (field, value) in new[] { ("El tipo", Type), ("El valor inicial", Initial), ("La directiva", Directive) })
            if (value.Contains(';') || value.Contains("//", StringComparison.Ordinal) || value.Contains('\n'))
                return $"{field} no puede llevar «;», «//» ni saltos de línea.";
        if (Directive.Trim().StartsWith("///", StringComparison.Ordinal)) return "La directiva se escribe sin «///» (p. ej. «PARAM» o «IO DI DEFAULT FALSE»).";
        return null;
    }
}

/// <summary>
/// Un fichero <c>.st</c> de la librería maestra abierto en el editor. Es el **texto** (nunca se regenera entero): las
/// tablas de declaraciones y el código del cuerpo son vistas sobre él, y cada cambio reescribe solo sus líneas. Así se
/// conservan cabeceras, comentarios y alineación, y lo que se guarda es lo mismo que lee <see cref="MasterLibraryReader"/>
/// (y los emisores). Tras cada cambio se vuelve a leer y a validar con el resto de la librería.
/// </summary>
public sealed partial class StSourceDocument
{
    private readonly IReadOnlyList<LibrarySource> _others;
    private readonly int _position;
    private List<string> _lines;
    private readonly string _newline;

    /// <param name="name">Nombre del fichero sin extensión (el de la librería, p. ej. «Motor»).</param>
    /// <param name="libraryFiles">La librería maestra en su orden. Si tiene un fichero con el mismo nombre, este lo sustituye.</param>
    public StSourceDocument(string name, string text, IReadOnlyList<(string Name, string Text)> libraryFiles)
    {
        Name = name;
        _newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        _lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var position = libraryFiles.Select(f => f.Name).ToList().FindIndex(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        _position = position < 0 ? libraryFiles.Count : position;
        _others = MasterLibraryReader.Read(libraryFiles.Where(f => !f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).Library.Sources;
        Reparse();
    }

    public string Name { get; }

    public string Text => string.Join(_newline, _lines);

    /// <summary>Lo leído de este fichero (con lo que se pudo, aunque haya errores).</summary>
    public LibrarySource Source { get; private set; } = null!;

    /// <summary>La librería con este fichero en su sitio: la que ve el código.</summary>
    public MasterLibrary Library { get; private set; } = null!;

    /// <summary>Errores de lectura (<c>ML0xx</c>) y, si se pudo leer, de las convenciones (<c>ML1xx</c>) de este fichero.</summary>
    public IReadOnlyList<LibraryDiagnostic> FileDiagnostics { get; private set; } = [];

    public IReadOnlyList<StOutlineItem> Outline { get; private set; } = [];

    /// <summary>Se incrementa con cada cambio del texto.</summary>
    public int Version { get; private set; }

    private void Reparse()
    {
        var read = MasterLibraryReader.Read([(Name, Text)]);
        Source = read.Library.Sources[0];
        var sources = _others.ToList();
        sources.Insert(Math.Min(_position, sources.Count), Source);
        Library = new MasterLibrary(sources);
        var diagnostics = read.Diagnostics.ToList();
        if (!read.HasErrors)
            diagnostics.AddRange(MasterLibraryValidator.Validate(Library)
                .Where(d => d.Location is { } at && at.File.Equals(Name, StringComparison.OrdinalIgnoreCase)));
        FileDiagnostics = diagnostics;
        Outline = Source.Types.Select(t => new StOutlineItem(StItemKind.Type, t.Name, t.Location.Line))
            .Concat(Source.Pous.Select(p => new StOutlineItem(p.Kind == PouKind.Function ? StItemKind.Function : StItemKind.FunctionBlock, p.Name, p.Location.Line)))
            .OrderBy(i => i.Line)
            .ToList();
    }

    private void Changed()
    {
        Version++;
        Reparse();
    }

    public StructType? Type(string name) => Source.Types.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public Pou? Pou(string name) => Source.Pous.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- tablas

    /// <summary>Filas de un contenedor, en el orden del fichero. Vacío si no existe (p. ej. una POU sin VAR CONSTANT).</summary>
    public IReadOnlyList<StDeclarationRow> Rows(StContainer container) => Rows(Items(container));

    private IReadOnlyList<DeclarationItem> Items(StContainer container)
    {
        if (container.Section is not { } kind) return Type(container.Item)?.Items ?? [];
        return Pou(container.Item)?.Sections.Where(s => s.Kind == kind).SelectMany(s => s.Items).ToList() ?? [];
    }

    private static IReadOnlyList<StDeclarationRow> Rows(IReadOnlyList<DeclarationItem> items)
    {
        var rows = new List<StDeclarationRow>();
        foreach (var group in items.GroupBy(i => i.Location.Line))
        {
            var slot = 0;
            foreach (var item in group)
                rows.Add(item switch
                {
                    MemberDeclaration m => new StDeclarationRow(m.Location.Line, slot++, m.Name, StScope.TypeText(m), m.InitialValue ?? "",
                        m.IsParameter ? "PARAM" : m.Io?.Text ?? "", m.Comment ?? "", false),
                    CommentLine c => new StDeclarationRow(c.Location.Line, slot++, "", "", "", "", c.Text, true),
                    _ => throw new InvalidOperationException()
                });
        }

        return rows;
    }

    /// <summary>Cambia una fila. Devuelve el problema si no se puede escribir así (y no cambia nada).</summary>
    public string? ReplaceRow(int line, int slot, StDeclarationValues values)
    {
        var (container, onLine) = RowsOnLine(line);
        if (onLine.Count == 0 || slot >= onLine.Count) return "Esa fila ya no existe.";
        var isComment = onLine[slot].IsComment;
        if (values.Problem(isComment) is { } problem) return problem;

        var index = line - 1;
        var template = _lines[index];
        var replacement = onLine.Select((row, k) => k == slot ? (row.IsComment, values) : (row.IsComment, row.Values))
            .Select(r => r.IsComment ? CommentText(template, r.Item2.Comment) : Format(r.Item2, template))
            .ToList();
        if (replacement.Count == 1 && replacement[0] == template) return null;
        _lines.RemoveAt(index);
        _lines.InsertRange(index, replacement);
        Changed();
        return null;
    }

    /// <summary>
    /// Añade una fila detrás de <paramref name="afterLine"/> o, si es null, al final del contenedor. Si la sección no
    /// existe, se crea en su sitio (VAR_INPUT, VAR_IN_OUT, VAR_OUTPUT, VAR CONSTANT, VAR). Devuelve la línea nueva o el problema.
    /// </summary>
    public (int Line, string? Problem) InsertRow(StContainer container, int? afterLine, StDeclarationValues values, bool isComment = false)
    {
        if (values.Problem(isComment) is { } problem) return (0, problem);
        var rows = Rows(container);
        var template = rows.LastOrDefault(r => !r.IsComment && (afterLine is null || r.Line <= afterLine)) is { } near ? _lines[near.Line - 1] : "    ";
        var text = isComment ? CommentText(template, values.Comment) : Format(values, template);

        int at;
        if (afterLine is { } after && rows.Any(r => r.Line == after)) at = after;   // índice 0 de la línea siguiente
        else if (container.Section is null)
        {
            var type = Type(container.Item) ?? throw new ArgumentException($"No hay un TYPE {container.Item}.");
            at = FindFrom(type.Location.Line - 1, t => t.StartsWith("END_STRUCT", StringComparison.Ordinal));
        }
        else if (SectionRange(container) is { } range) at = range.End;
        else return (CreateSection(container, text), null);

        if (at < 0) return (0, "El fichero no tiene la forma esperada (falta END_STRUCT o END_VAR): corrígelo en el texto.");
        _lines.Insert(at, text);
        Changed();
        return (at + 1, null);
    }

    public void RemoveRow(int line, int slot)
    {
        var (_, onLine) = RowsOnLine(line);
        if (onLine.Count == 0) return;
        var index = line - 1;
        var template = _lines[index];
        _lines.RemoveAt(index);
        _lines.InsertRange(index, onLine.Where((_, k) => k != slot)
            .Select(r => r.IsComment ? CommentText(template, r.Comment) : Format(r.Values, template)));
        Changed();
    }

    /// <summary>Sube (-1) o baja (+1) la línea dentro de su contenedor. Devuelve su línea nueva (la misma si no se mueve).</summary>
    public int MoveRow(int line, int direction)
    {
        var (container, onLine) = RowsOnLine(line);
        if (container is null || onLine.Count == 0) return line;
        var lines = Rows(container).Select(r => r.Line).Distinct().ToList();
        var k = lines.IndexOf(line) + Math.Sign(direction);
        if (k < 0 || k >= lines.Count) return line;
        var other = lines[k];
        (_lines[line - 1], _lines[other - 1]) = (_lines[other - 1], _lines[line - 1]);
        Changed();
        return other;
    }

    private (StContainer? Container, IReadOnlyList<StDeclarationRow> Rows) RowsOnLine(int line)
    {
        foreach (var type in Source.Types)
            if (Rows(type.Items).Where(r => r.Line == line).ToList() is { Count: > 0 } rows) return (new StContainer(type.Name), rows);
        foreach (var pou in Source.Pous)
            foreach (var section in pou.Sections)
                if (Rows(section.Items).Where(r => r.Line == line).ToList() is { Count: > 0 } rows) return (new StContainer(pou.Name, section.Kind), rows);
        return (null, []);
    }

    private int FindFrom(int from, Func<string, bool> match)
    {
        for (var k = from + 1; k < _lines.Count; k++)
            if (match(_lines[k].Trim())) return k;
        return -1;
    }

    /// <summary>Índices (desde 0) de la cabecera y del END_VAR de la primera sección de ese tipo.</summary>
    private (int Header, int End)? SectionRange(StContainer container)
    {
        var section = Pou(container.Item)?.Sections.FirstOrDefault(s => s.Kind == container.Section);
        if (section is null) return null;
        var header = section.Location.Line - 1;
        var end = FindFrom(header, t => t == "END_VAR");
        return end < 0 ? null : (header, end);
    }

    private static readonly VarSectionKind[] SectionOrder =
        [VarSectionKind.Input, VarSectionKind.InOut, VarSectionKind.Output, VarSectionKind.VarConstant, VarSectionKind.Var];

    private int CreateSection(StContainer container, string row)
    {
        var pou = Pou(container.Item) ?? throw new ArgumentException($"No hay un bloque {container.Item}.");
        var kind = container.Section!.Value;
        var order = Array.IndexOf(SectionOrder, kind);
        var next = pou.Sections.FirstOrDefault(s => Array.IndexOf(SectionOrder, s.Kind) > order);
        var block = new List<string> { VarSection.KeywordOf(kind), row, "END_VAR" };
        int at;
        if (next is not null)
        {
            at = next.Location.Line - 1;
            block.Add("");
        }
        else
        {
            var last = pou.Sections.LastOrDefault();
            at = (last is null ? pou.Location.Line - 1 : FindFrom(last.Location.Line - 1, t => t == "END_VAR")) + 1;
            block.Insert(0, "");
        }

        _lines.InsertRange(at, block);
        Changed();
        return at + block.IndexOf(row) + 1;
    }

    // ---------------------------------------------------------------- crear TYPE y bloques

    /// <summary>Por qué <paramref name="name"/> no sirve para crear algo de <paramref name="kind"/> aquí, o null.</summary>
    public string? NewNameProblem(StNewKind kind, string name)
    {
        name = name.Trim();
        if (!Regex.IsMatch(name, @"^[A-Za-z_]\w*$")) return $"«{name}» no es un nombre válido: letras, números y «_», sin empezar por número.";
        if (ReservedNames.PortableIdentifierProblem(name) is { } problem)
            return $"«{name}» no vale en todas las plataformas: {problem}.";
        if (kind == StNewKind.Element && name.StartsWith("UDT_", StringComparison.OrdinalIgnoreCase))
            return $"Escribe solo el tipo de elemento («Vibrotamiz»): «UDT_» y «_Block» se ponen solos.";
        var created = kind == StNewKind.Element ? StTemplates.ElementNames(name) : [name];
        var existing = Library.Sources.SelectMany(s => s.Types.Select(t => t.Name).Concat(s.Pous.Select(p => p.Name)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return created.FirstOrDefault(existing.Contains) is { } taken ? $"Ya hay un tipo o bloque «{taken}» en la librería." : null;
    }

    /// <summary>
    /// Crea un TYPE (detrás del último TYPE del fichero, o delante del primer bloque), un FUNCTION_BLOCK o un bloque de
    /// elemento (al final). Devuelve el problema (y no cambia nada) o null; lo creado sale en <see cref="Outline"/>.
    /// </summary>
    public string? Create(StNewKind kind, string name)
    {
        if (kind == StNewKind.File) throw new ArgumentException("Un fichero nuevo es otro documento.", nameof(kind));
        if (NewNameProblem(kind, name) is { } problem) return problem;
        name = name.Trim();
        var block = kind switch
        {
            StNewKind.Type => StTemplates.Type(name),
            StNewKind.FunctionBlock => StTemplates.FunctionBlock(name),
            _ => StTemplates.Element(name)
        };

        var lastType = kind == StNewKind.Type && Source.Types.Count > 0
            ? FindFrom(Source.Types.Max(t => t.Location.Line) - 1, t => t.StartsWith("END_TYPE", StringComparison.Ordinal))
            : -1;
        if (lastType >= 0)
            _lines.InsertRange(lastType + 1, ["", "", .. block]);   // con los otros TYPE
        else if (kind == StNewKind.Type && Source.Pous.Count > 0)
            _lines.InsertRange(Source.Pous.Min(p => p.Location.Line) - 1, [.. block, "", ""]);   // delante de los bloques
        else
        {
            // Al final, separado por dos líneas vacías y acabando en salto de línea.
            while (_lines.Count > 0 && _lines[^1].Trim().Length == 0) _lines.RemoveAt(_lines.Count - 1);
            if (_lines.Count > 0) _lines.AddRange(["", ""]);
            _lines.AddRange([.. block, ""]);
        }

        Changed();
        return null;
    }

    // ---------------------------------------------------------------- formato de una línea

    [GeneratedRegex(@"^(?<indent>\s*)(?<names>[A-Za-z_][\w\s,]*?)(?<pad>\s*):(?!=)")]
    private static partial Regex DeclarationStart();

    /// <summary>
    /// <c>Nombre : TIPO [:= valor]; [/// directiva] [// comentario]</c>, alineado como <paramref name="template"/> (la
    /// línea que sustituye o una vecina): misma sangría, los «:» en la misma columna y el comentario en la suya, si cabe.
    /// </summary>
    public static string Format(StDeclarationValues v, string template)
    {
        var indent = Regex.Match(template, @"^\s*").Value;
        if (indent.Length == 0) indent = "    ";
        var name = v.Name.Trim();
        var width = DeclarationStart().Match(template) is { Success: true } m ? m.Groups["names"].Length + m.Groups["pad"].Length : 0;
        var text = indent + (name.Length < width ? name.PadRight(width) + ": " : name + " : ") + v.Type.Trim();
        if (v.Initial.Trim().Length > 0) text += " := " + v.Initial.Trim();
        text += ";";
        if (v.Directive.Trim().Length > 0) text += " /// " + v.Directive.Trim();
        if (v.Comment.Trim().Length > 0)
        {
            var column = CommentColumn(template);
            text = (column > text.Length + 1 ? text.PadRight(column) : text + "  ") + "// " + v.Comment.Trim();
        }

        return text;
    }

    private static string CommentText(string template, string comment) =>
        (Regex.Match(template, @"^\s*").Value is { Length: > 0 } indent ? indent : "    ") + "// " + comment.Trim();

    /// <summary>Columna del comentario «//» (no «///») de una línea de declaración, o -1.</summary>
    private static int CommentColumn(string line)
    {
        var offset = 0;
        var seenSemicolon = false;
        foreach (var token in StLexer.Tokenize(line))
        {
            if (token.Is(";")) seenSemicolon = true;
            if (seenSemicolon && token.Kind == StTokenKind.LineComment && !token.IsDirective) return offset;
            offset += token.Text.Length;
        }

        return -1;
    }

    // ---------------------------------------------------------------- cuerpo

    /// <summary>Índices (desde 0) de la primera y la última línea entre el último END_VAR y el END_FUNCTION…; -1 si no se encuentra.</summary>
    private (int From, int To) BodyRange(Pou pou)
    {
        var header = pou.Location.Line - 1;
        var end = FindFrom(header, t => t.StartsWith("END_FUNCTION", StringComparison.Ordinal));
        if (end < 0) return (-1, -1);
        var last = pou.Sections.Select(s => FindFrom(s.Location.Line - 1, t => t == "END_VAR")).Where(e => e >= 0 && e < end).DefaultIfEmpty(header).Max();
        return (last + 1, end - 1);
    }

    /// <summary>El código del cuerpo (sin las líneas vacías del principio y del final), o null si la POU no existe o está mal cerrada.</summary>
    public string? Body(string pouName)
    {
        if (Pou(pouName) is not { } pou) return null;
        var (from, to) = BodyRange(pou);
        if (from < 0) return null;
        var (first, last) = Trimmed(from, to);
        return first > last ? "" : string.Join("\n", _lines.Skip(first).Take(last - first + 1));
    }

    /// <summary>Línea del fichero (desde 1) de la primera línea de <see cref="Body"/>.</summary>
    public int BodyFirstLine(string pouName)
    {
        if (Pou(pouName) is not { } pou) return 0;
        var (from, to) = BodyRange(pou);
        return from < 0 ? 0 : Trimmed(from, to).First + 1;
    }

    private (int First, int Last) Trimmed(int from, int to)
    {
        var first = from;
        var last = to;
        while (first <= last && _lines[first].Trim().Length == 0) first++;
        while (last >= first && _lines[last].Trim().Length == 0) last--;
        return (first, last);
    }

    [GeneratedRegex(@"^(END_FUNCTION|END_VAR\b|VAR(_INPUT|_OUTPUT|_IN_OUT|_TEMP)?(\s+(CONSTANT|RETAIN))?$|FUNCTION_BLOCK\b|FUNCTION\b|TYPE\b|END_TYPE\b)")]
    private static partial Regex StructuralLine();

    /// <summary>
    /// Sustituye el código del cuerpo. Las líneas vacías de alrededor se conservan. Devuelve el problema (y no cambia
    /// nada) si una línea cerraría o abriría un bloque o una sección (<c>END_FUNCTION_BLOCK</c>, <c>VAR</c>…).
    /// </summary>
    public string? SetBody(string pouName, string body)
    {
        if (Pou(pouName) is not { } pou) return $"No hay un bloque {pouName}.";
        var bodyLines = body.Replace("\r\n", "\n").Split('\n').ToList();
        while (bodyLines.Count > 0 && bodyLines[^1].Trim().Length == 0) bodyLines.RemoveAt(bodyLines.Count - 1);
        while (bodyLines.Count > 0 && bodyLines[0].Trim().Length == 0) bodyLines.RemoveAt(0);
        var structural = bodyLines.Select((l, k) => (Line: k + 1, Text: l.Trim())).FirstOrDefault(l => StructuralLine().IsMatch(l.Text));
        if (structural.Text is not null)
            return $"Línea {structural.Line}: «{structural.Text}» no puede ir en el código (cambiaría las declaraciones o cerraría el bloque).";

        var (from, to) = BodyRange(pou);
        if (from < 0) return $"{pouName} no está bien cerrado: corrígelo en el texto.";
        var (first, last) = Trimmed(from, to);
        int leading, trailing;
        if (first > last)
        {
            leading = 1;
            trailing = 1;
        }
        else
        {
            leading = first - from;
            trailing = to - last;
        }

        var replacement = Enumerable.Repeat("", leading).Concat(bodyLines).Concat(Enumerable.Repeat("", trailing)).ToList();
        var current = _lines.Skip(from).Take(to - from + 1).ToList();
        if (current.SequenceEqual(replacement)) return null;
        _lines.RemoveRange(from, to - from + 1);
        _lines.InsertRange(from, replacement);
        Changed();
        return null;
    }

    /// <summary>Analiza el código de una POU de este fichero con la librería actual; null si no existe.</summary>
    public StBodyAnalysis? AnalyzeBody(string pouName) =>
        Pou(pouName) is { } pou && Body(pouName) is { } body ? StBodyAnalyzer.Analyze(body, new StScope(Library, pou)) : null;

    /// <summary>Lo que se ve desde el cuerpo de una POU de este fichero (para el autocompletado).</summary>
    public StScope? Scope(string pouName) => Pou(pouName) is { } pou ? new StScope(Library, pou) : null;
}
