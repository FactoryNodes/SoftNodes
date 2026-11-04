using System.Text.RegularExpressions;
using FactoryNodes.Iec61131.Model;
using FactoryNodes.Iec61131.Syntax;

namespace FactoryNodes.Iec61131.Reading;

/// <summary>Resultado de leer la librería: el modelo (aunque haya errores, con lo que se pudo leer) y los diagnósticos.</summary>
public sealed record LibraryReadResult(MasterLibrary Library, IReadOnlyList<LibraryDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>
/// Lee los ficheros <c>.st</c> de la librería maestra: <c>TYPE … STRUCT</c>, <c>FUNCTION</c> y <c>FUNCTION_BLOCK</c>, con
/// sus directivas <c>///</c>. Es estricto a propósito: lo que no reconoce es un error con fichero y línea, nunca se
/// salta en silencio. Los comentarios <c>(* *)</c> no llegan al modelo (son cabeceras para personas); los <c>//</c>
/// se conservan. No valida las convenciones de los bloques: eso lo hace <see cref="Validation.MasterLibraryValidator"/>.
/// Códigos <c>ML0xx</c>. Especificación: docs/MASTER_LIBRARY_CONVENTIONS.md.
/// </summary>
public static partial class MasterLibraryReader
{
    /// <param name="files">Nombre (sin extensión, p. ej. "Motor") y texto de cada fichero, en el orden de la librería.</param>
    public static LibraryReadResult Read(IEnumerable<(string Name, string Text)> files)
    {
        var diagnostics = new List<LibraryDiagnostic>();
        var sources = files.Select(f => new FileReader(f.Name, f.Text, diagnostics).Read()).ToList();
        return new LibraryReadResult(new MasterLibrary(sources), diagnostics);
    }

    // Port de la expresión del prototipo (Tools/prototype/master.py): nombres, tipo, valor inicial, directiva, comentario.
    // Ampliada (2026-10-01) con ARRAY[a..b] OF de una dimensión; a y b, enteros o constantes de tamaño (/// SIZE).
    [GeneratedRegex(@"^\s*(?<names>[A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)\s*:\s*(?:ARRAY\s*\[\s*(?<lo>-?\w+)\s*\.\.\s*(?<hi>-?\w+)\s*\]\s*OF\s+)?(?<type>[A-Za-z_]\w*)\s*(?::=\s*(?<init>[^;]+?))?\s*;\s*(?<dir>///[^/].*?|///)?\s*(?://\s*(?<comment>.*))?$")]
    private static partial Regex DeclarationRegex();

    [GeneratedRegex(@"^SIZE\s+(?<name>[A-Za-z_]\w*)\s+(?<value>\d+)$")]
    private static partial Regex SizeRegex();

    /// <summary><c>/// ONLY TIA</c>: el bloque que sigue es solo de esa plataforma (PENDIENTES 165).</summary>
    [GeneratedRegex(@"^ONLY\s+(?<platform>[A-Za-z]\w*)$")]
    private static partial Regex OnlyRegex();

    [GeneratedRegex(@"^ALARM\s+(?<bit>\d+)\s+(?<code>[^\s""]+)\s+""(?<description>[^""]*)""(?:\s+""(?<solutions>[^""]*)"")?$")]
    private static partial Regex AlarmRegex();

    [GeneratedRegex(@"^TYPE\s+([A-Za-z_]\w*)\s*:\s*(STRUCT)?\s*$")]
    private static partial Regex TypeHeaderRegex();

    [GeneratedRegex(@"^(FUNCTION_BLOCK|FUNCTION)\s+([A-Za-z_]\w*)(?:\s*:\s*([A-Za-z_]\w*))?\s*$")]
    private static partial Regex PouHeaderRegex();

    [GeneratedRegex(@"^VAR(_INPUT|_OUTPUT|_IN_OUT|_TEMP)?(\s+CONSTANT|\s+RETAIN)?\s*$")]
    private static partial Regex SectionRegex();

    private sealed class FileReader(string name, string text, List<LibraryDiagnostic> diagnostics)
    {
        private string[] _lines = [];
        private int _i;

        private SourceLocation At(int index) => new(name, index + 1);

        private void Error(string code, string message, int lineIndex) =>
            diagnostics.Add(new LibraryDiagnostic(DiagnosticSeverity.Error, code, message, At(lineIndex)));

        public LibrarySource Read()
        {
            _lines = WithoutBlockComments(text.Replace("\r\n", "\n").Replace('\r', '\n')).Split('\n');
            var types = new List<StructType>();
            var pous = new List<Pou>();
            var sizes = new List<SizeConstant>();
            int? pendingCompat = null;
            (string Platform, int Line)? pendingOnly = null;
            var pendingAlarms = new List<BlockAlarm>();

            for (_i = 0; _i < _lines.Length; _i++)
            {
                var t = _lines[_i].Trim();
                if (t.Length == 0) continue;

                if (t.StartsWith("///", StringComparison.Ordinal))
                {
                    var directive = t[3..].Trim();
                    if (directive == "COMPAT") pendingCompat = _i;
                    else if (OnlyRegex().Match(directive) is { Success: true } only) pendingOnly = (only.Groups["platform"].Value, _i);
                    else if (directive.StartsWith("ONLY", StringComparison.Ordinal))
                        Error("ML001", $"Se escribe «/// ONLY Plataforma» (TIA, Sysmac…), justo antes de un FUNCTION_BLOCK o una FUNCTION: «/// {directive}».", _i);
                    else if (SizeRegex().Match(directive) is { Success: true } size)
                        sizes.Add(new SizeConstant(size.Groups["name"].Value, int.Parse(size.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture), At(_i)));
                    else if (directive.StartsWith("SIZE", StringComparison.Ordinal))
                        Error("ML001", $"Se escribe «/// SIZE NOMBRE número» (p. ej. «/// SIZE TOPO_NODES 1»): «/// {directive}».", _i);
                    else if (directive.StartsWith("ALARM", StringComparison.Ordinal)) ReadAlarm(directive, pendingAlarms);
                    else Error("ML001", $"Directiva desconocida o fuera de sitio: «/// {directive}». Fuera de un bloque solo valen «/// COMPAT», «/// ONLY», «/// SIZE» y «/// ALARM».", _i);
                    continue;
                }

                if (t.StartsWith("//", StringComparison.Ordinal)) continue;

                if (TypeHeaderRegex().Match(t) is { Success: true } typeHeader)
                {
                    if (pendingCompat is { } compatLine)
                    {
                        Error("ML006", "«/// COMPAT» debe ir justo antes de una FUNCTION.", compatLine);
                        pendingCompat = null;
                    }

                    if (pendingOnly is { } onlyBeforeType)
                    {
                        Error("ML006", "«/// ONLY» debe ir justo antes de un FUNCTION_BLOCK o una FUNCTION, no de un TYPE.", onlyBeforeType.Line);
                        pendingOnly = null;
                    }

                    DropAlarms(pendingAlarms);
                    types.Add(ReadType(typeHeader.Groups[1].Value));
                    continue;
                }

                if (PouHeaderRegex().Match(t) is { Success: true } pouHeader)
                {
                    var kind = pouHeader.Groups[1].Value == "FUNCTION" ? PouKind.Function : PouKind.FunctionBlock;
                    var returnType = pouHeader.Groups[3].Success ? pouHeader.Groups[3].Value : null;
                    if (kind == PouKind.Function && returnType is null)
                        Error("ML004", $"La FUNCTION {pouHeader.Groups[2].Value} no declara el tipo que devuelve.", _i);
                    if (kind == PouKind.FunctionBlock && returnType is not null)
                        Error("ML004", $"Un FUNCTION_BLOCK no devuelve tipo: «{t}».", _i);

                    var pou = ReadPou(kind, pouHeader.Groups[2].Value, returnType, pendingCompat is not null);
                    if (kind == PouKind.FunctionBlock && pendingAlarms.Count > 0) pou = pou with { Alarms = [.. pendingAlarms.OrderBy(a => a.Bit)] };
                    else DropAlarms(pendingAlarms);
                    if (pendingOnly is { } only) pou = pou with { OnlyPlatform = only.Platform };
                    pendingAlarms.Clear();
                    pous.Add(pou);
                    pendingCompat = null;
                    pendingOnly = null;
                    continue;
                }

                if (t.StartsWith("TYPE", StringComparison.Ordinal))
                {
                    Error("ML002", $"Solo se admiten tipos «TYPE <Nombre> : STRUCT … END_STRUCT; END_TYPE»: «{t}».", _i);
                    SkipTo("END_TYPE");
                    continue;
                }

                Error("ML002", $"Texto no reconocido fuera de un TYPE, FUNCTION o FUNCTION_BLOCK: «{t}».", _i);
            }

            if (pendingCompat is { } dangling) Error("ML006", "«/// COMPAT» sin FUNCTION detrás.", dangling);
            if (pendingOnly is { } danglingOnly) Error("ML006", "«/// ONLY» sin FUNCTION_BLOCK ni FUNCTION detrás.", danglingOnly.Line);
            DropAlarms(pendingAlarms);
            return new LibrarySource(name, types, pous, sizes);
        }

        /// <summary><c>/// ALARM bit abreviatura "descripción" ["soluciones"]</c>: para el FUNCTION_BLOCK que viene detrás.</summary>
        private void ReadAlarm(string directive, List<BlockAlarm> pending)
        {
            if (AlarmRegex().Match(directive) is not { Success: true } m)
            {
                Error("ML008", $"Se escribe «/// ALARM bit abreviatura \"descripción\" [\"soluciones\"]» (p. ej. «/// ALARM 2 KM \"Contactor…\" \"Revisar…\"»): «/// {directive}».", _i);
                return;
            }

            var bit = int.Parse(m.Groups["bit"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (bit > 15) Error("ML008", $"«/// ALARM {bit}»: ALM_Word tiene los bits 0 a 15.", _i);
            else if (pending.Any(a => a.Bit == bit)) Error("ML008", $"«/// ALARM {bit}» repetida para el mismo bloque.", _i);
            else pending.Add(new BlockAlarm(bit, m.Groups["code"].Value, m.Groups["description"].Value.Trim(), m.Groups["solutions"].Value.Trim(), At(_i)));
        }

        private void DropAlarms(List<BlockAlarm> pending)
        {
            foreach (var alarm in pending)
                diagnostics.Add(new LibraryDiagnostic(DiagnosticSeverity.Error, "ML008", "«/// ALARM» tiene que ir justo antes de un FUNCTION_BLOCK.", alarm.Location));
            pending.Clear();
        }

        /// <summary>Quita los comentarios (* *) conservando los saltos de línea, para que los números de línea sigan valiendo.</summary>
        private string WithoutBlockComments(string source)
        {
            var tokens = StLexer.Tokenize(source);
            var line = 0;
            foreach (var token in tokens)
            {
                if (token.Kind == StTokenKind.BlockComment && !token.Text.EndsWith("*)", StringComparison.Ordinal))
                    diagnostics.Add(new LibraryDiagnostic(DiagnosticSeverity.Error, "ML007", "Comentario (* sin cerrar.", new SourceLocation(name, line + 1)));
                if (token.Kind is StTokenKind.NewLine or StTokenKind.BlockComment) line += token.Text.Count(c => c == '\n');
            }

            return StLexer.Join(tokens.Select(t => t.Kind == StTokenKind.BlockComment
                ? new StToken(StTokenKind.NewLine, new string('\n', t.Text.Count(c => c == '\n')))
                : t));
        }

        private void SkipTo(string keyword)
        {
            while (_i < _lines.Length && !_lines[_i].Trim().StartsWith(keyword, StringComparison.Ordinal)) _i++;
        }

        private StructType ReadType(string typeName)
        {
            var start = _i;
            var items = new List<DeclarationItem>();
            for (_i++; _i < _lines.Length; _i++)
            {
                var t = _lines[_i].Trim();
                if (t.StartsWith("END_TYPE", StringComparison.Ordinal)) return new StructType(typeName, items, At(start));
                if (t.Length == 0 || t == "STRUCT" || t is "END_STRUCT;" or "END_STRUCT") continue;
                ReadDeclarationLine(items);
            }

            Error("ML003", $"Falta END_TYPE en {typeName}.", start);
            return new StructType(typeName, items, At(start));
        }

        /// <summary>Una línea dentro de un STRUCT o de una sección VAR: comentario o declaración.</summary>
        private void ReadDeclarationLine(List<DeclarationItem> items)
        {
            var line = _lines[_i];
            var t = line.Trim();
            if (t.StartsWith("///", StringComparison.Ordinal))
            {
                Error("ML001", $"Una directiva en una declaración va al final de la línea del miembro: «{t}».", _i);
                return;
            }

            if (t.StartsWith("//", StringComparison.Ordinal))
            {
                items.Add(new CommentLine(t[2..].Trim(), At(_i)));
                return;
            }

            var m = DeclarationRegex().Match(line);
            if (!m.Success)
            {
                Error("ML004", $"Declaración no reconocida (se admite «Nombre[, Nombre] : [ARRAY[a..b] OF] TIPO [:= valor];»): «{t}».", _i);
                return;
            }

            IoDirective? io = null;
            var isParameter = false;
            if (m.Groups["dir"].Success)
            {
                var directive = m.Groups["dir"].Value.Trim()[3..].Trim();
                if (directive == "PARAM") isParameter = true;
                else io = ParseIo(directive);
            }

            var comment = m.Groups["comment"].Success && m.Groups["comment"].Value.Trim().Length > 0 ? m.Groups["comment"].Value.Trim() : null;
            var init = m.Groups["init"].Success ? m.Groups["init"].Value.Trim() : null;
            var array = m.Groups["lo"].Success ? new ArrayBounds(m.Groups["lo"].Value, m.Groups["hi"].Value) : null;
            foreach (var memberName in m.Groups["names"].Value.Split(',').Select(n => n.Trim()))
                items.Add(new MemberDeclaration(memberName, m.Groups["type"].Value, init, io, comment, At(_i), isParameter, array));
        }

        private IoDirective? ParseIo(string directive)
        {
            var parts = directive.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0] != "IO")
            {
                Error("ML001", $"Directiva desconocida en un miembro: «/// {directive}». Solo valen «/// IO …» y «/// PARAM».", _i);
                return null;
            }

            switch (parts.Length)
            {
                case 2:
                    return new IoDirective(directive, parts[1], false, null);
                case 3 when parts[2] == "REQUIRED":
                    return new IoDirective(directive, parts[1], true, null);
                case 4 when parts[2] == "DEFAULT":
                    return new IoDirective(directive, parts[1], false, parts[3]);
                default:
                    Error("ML001", $"«/// {directive}» no sigue la forma «IO <tipo> [REQUIRED | DEFAULT <valor>]».", _i);
                    return null;
            }
        }

        private Pou ReadPou(PouKind kind, string pouName, string? returnType, bool compat)
        {
            var start = _i;
            var endKeyword = kind == PouKind.Function ? "END_FUNCTION" : "END_FUNCTION_BLOCK";
            var sections = new List<VarSection>();
            List<DeclarationItem>? current = null;
            (VarSectionKind Kind, int Line) currentHeader = default;

            // Declaraciones: secciones VAR hasta la primera línea que no es declaración.
            for (_i++; _i < _lines.Length; _i++)
            {
                var t = _lines[_i].Trim();
                if (current is null && IsPouEnd(t)) break;

                if (SectionRegex().Match(t) is { Success: true } section)
                {
                    if (current is not null)
                        Error("ML003", "Falta END_VAR antes de la siguiente sección.", _i);
                    current = [];
                    currentHeader = (SectionKind(section), _i);
                    continue;
                }

                if (t == "END_VAR")
                {
                    if (current is null) Error("ML003", "END_VAR sin sección abierta.", _i);
                    else sections.Add(new VarSection(currentHeader.Kind, current, At(currentHeader.Line)));
                    current = null;
                    continue;
                }

                if (current is not null)
                {
                    if (t.Length > 0) ReadDeclarationLine(current);
                    continue;
                }

                if (t.Length == 0) continue;
                break;   // primera línea del cuerpo
            }

            if (current is not null)
            {
                Error("ML003", "Falta END_VAR.", currentHeader.Line);
                sections.Add(new VarSection(currentHeader.Kind, current, At(currentHeader.Line)));
            }

            // Cuerpo, hasta END_FUNCTION / END_FUNCTION_BLOCK.
            var bodyStart = _i;
            while (_i < _lines.Length && !IsPouEnd(_lines[_i].Trim())) _i++;
            if (_i >= _lines.Length) Error("ML003", $"Falta {endKeyword} en {pouName}.", start);
            else if (!_lines[_i].Trim().StartsWith(endKeyword, StringComparison.Ordinal) ||
                     (kind == PouKind.Function && _lines[_i].Trim().StartsWith("END_FUNCTION_BLOCK", StringComparison.Ordinal)))
                Error("ML003", $"{pouName} se cierra con «{_lines[_i].Trim()}»; debe ser {endKeyword}.", _i);

            var body = ReadBody(bodyStart, Math.Min(_i, _lines.Length));
            return new Pou(kind, pouName, returnType, compat, sections, body, At(start));
        }

        private static bool IsPouEnd(string trimmed) => trimmed.StartsWith("END_FUNCTION", StringComparison.Ordinal);

        private static VarSectionKind SectionKind(Match section) => (section.Groups[1].Value, section.Groups[2].Value.Trim()) switch
        {
            ("_INPUT", _) => VarSectionKind.Input,
            ("_OUTPUT", _) => VarSectionKind.Output,
            ("_IN_OUT", _) => VarSectionKind.InOut,
            ("_TEMP", _) => VarSectionKind.VarTemp,
            ("", "CONSTANT") => VarSectionKind.VarConstant,
            ("", "RETAIN") => VarSectionKind.VarRetain,
            _ => VarSectionKind.Var
        };

        private StBody ReadBody(int from, int to)
        {
            // Sin las líneas vacías del principio y del final (como el prototipo: strip('\n')).
            while (from < to && _lines[from].Length == 0) from++;
            while (to > from && _lines[to - 1].Length == 0) to--;

            var lines = new List<BodyLine>();
            var open = new Stack<int>();
            for (var k = from; k < to; k++)
            {
                var line = _lines[k];
                var t = line.Trim();
                if (t.StartsWith("///", StringComparison.Ordinal))
                {
                    var directive = t[3..].Trim();
                    if (directive == "END_REGION")
                    {
                        if (open.Count == 0) Error("ML005", "/// END_REGION sin /// REGION abierta.", k);
                        else open.Pop();
                        lines.Add(new RegionEnd(At(k)));
                    }
                    else if (directive == "REGION" || directive.StartsWith("REGION ", StringComparison.Ordinal))
                    {
                        var title = directive.Length > "REGION".Length ? directive["REGION".Length..].Trim() : "";
                        if (title.Length == 0) Error("ML005", "/// REGION sin título (el título es obligatorio).", k);
                        open.Push(k);
                        lines.Add(new RegionStart(title, At(k)));
                    }
                    else
                    {
                        Error("ML001", $"Directiva desconocida en el cuerpo: «/// {directive}».", k);
                    }

                    continue;
                }

                if (StLexer.Tokenize(line).Any(token => token.IsDirective))
                    Error("ML001", "Una directiva /// va sola en su línea dentro del cuerpo.", k);
                lines.Add(new CodeLine(line, At(k)));
            }

            foreach (var unclosed in open) Error("ML005", "/// REGION sin su /// END_REGION.", unclosed);
            return new StBody(lines);
        }
    }
}
