using FactoryNodes.SoftNodes.Debugging;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FactoryNodes.SoftNodes.Open.Presentation;
using FactoryNodes.StEditing;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using TextBlock = System.Windows.Controls.TextBlock;
using ToolTip = System.Windows.Controls.ToolTip;

namespace SoftNodes.Controls;

/// <summary>
/// Pega un <see cref="TextEditor"/> de solo lectura al <see cref="DebugViewModel"/>: pinta el código con los colores de
/// <c>FactoryNodes.StEditing</c>, marca la línea que se ejecutará y los puntos de parada (clic en el margen izquierdo) y
/// lleva el número de línea del fichero. Qué se ve y qué línea toca lo decide la ViewModel.
/// </summary>
public sealed class DebugEditorAdapter
{
    private readonly TextEditor _editor;
    private readonly SpanColorizer _colorizer = new();
    private readonly CurrentLineRenderer _current = new();
    private readonly CurrentLineRenderer _fault = new(Brush("#40A32D2D"));
    private readonly ValuesGenerator _values = new();
    private readonly LineMargin _margin = new();
    private readonly FoldingManager _folding;
    private readonly HighlightRenderer _highlights = new();
    private const double NormalFontSize = 13;
    private readonly Dictionary<string, HashSet<int>> _folded = new(StringComparer.OrdinalIgnoreCase);
    private string? _foldingsOf;
    private ToolTip? _toolTip;
    private DebugViewModel? _viewModel;

    public DebugEditorAdapter(TextEditor editor)
    {
        var search = SearchPanel.Install(editor);
        search.Localization = new SearchTexts();
        _editor = editor;
        editor.IsReadOnly = true;
        editor.ShowLineNumbers = false;
        editor.FontFamily = new FontFamily("Consolas");
        editor.FontSize = 13;
        editor.WordWrap = false;
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.HighlightCurrentLine = false;
        editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        editor.TextArea.TextView.BackgroundRenderers.Add(_current);
        editor.TextArea.TextView.BackgroundRenderers.Add(_fault);
        editor.TextArea.TextView.ElementGenerators.Add(_values);
        editor.TextArea.LeftMargins.Insert(0, _margin);
        _folding = FoldingManager.Install(editor.TextArea);   // el «+»/«−», a la derecha del margen de los puntos de parada
        editor.TextArea.TextView.BackgroundRenderers.Add(_highlights);
        editor.Options.ColumnRulerPosition = 120;
        editor.ContextMenu = ViewMenu();
        // Ctrl+clic en un nombre: la instancia de un equipo se elige a la derecha; un bloque, se enseña
        editor.TextArea.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_viewModel is null || Keyboard.Modifiers != ModifierKeys.Control || _editor.GetPositionFromPoint(e.GetPosition(_editor)) is not { } position) return;
            if (_viewModel.GoToDefinition(position.Line, position.Column - 1)) e.Handled = true;
        };
        editor.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            _editor.FontSize = Math.Clamp(_editor.FontSize + (e.Delta > 0 ? 1 : -1), 8, 32);
            e.Handled = true;
        };
        editor.TextArea.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.D0 || Keyboard.Modifiers != ModifierKeys.Control) return;
            _editor.FontSize = NormalFontSize;
            e.Handled = true;
        };
        editor.MouseHover += (_, e) =>
        {
            if (_viewModel is null || _editor.GetPositionFromPoint(e.GetPosition(_editor)) is not { } position) return;
            if (_viewModel.ValueAt(position.Line, position.Column - 1) is not { } text) return;
            _toolTip = new ToolTip { Content = text, PlacementTarget = _editor, FontFamily = _editor.FontFamily, IsOpen = true };
            e.Handled = true;
        };
        editor.MouseHoverStopped += (_, _) =>
        {
            if (_toolTip is null) return;
            _toolTip.IsOpen = false;
            _toolTip = null;
        };
        _margin.Toggle = line => _viewModel?.ToggleBreakpoint(line);
        // La línea del cursor decide a qué etapa de un loop lleva «Ir al loop» cuando no hay un ciclo a medias
        editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.CaretLine = editor.TextArea.Caret.Line;
            // la pareja de IF/END_IF (y ELSE…) o las demás apariciones del nombre
            _highlights.Ranges = StHighlights.At(_editor.Text, _editor.CaretOffset);
            _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        };
    }

    public void Bind(DebugViewModel? viewModel)
    {
        if (_viewModel is not null) _viewModel.Changed -= OnChanged;
        _viewModel = viewModel;
        _margin.ViewModel = viewModel;
        if (viewModel is not null) viewModel.Changed += OnChanged;
        Reload();
    }

    private void OnChanged(object? sender, EventArgs e) => Reload();

    private void Reload()
    {
        if (_viewModel is null)
        {
            RememberFolded();
            _editor.Document.Text = "";
            _folding.Clear();
            _foldingsOf = null;
            _colorizer.Spans = [];
            _values.Values = [];
            _current.Line = 0;
            _fault.Line = 0;
            _editor.TextArea.TextView.Redraw();
            return;
        }

        // El texto solo cambia al cambiar de bloque o de sección: los valores de cada paso van aparte, así no se pierde lo plegado
        if (_editor.Document.Text != _viewModel.CodeText)
        {
            RememberFolded();
            _editor.Document.Text = _viewModel.CodeText;
            _folding.Clear();
            _folding.UpdateFoldings(StFolding.Find(_viewModel.CodeText).Select(f => new NewFolding(f.Start, f.End) { Name = "…" }), -1);
            _foldingsOf = _viewModel.ShownPou;
            if (_foldingsOf is not null && _folded.TryGetValue(_foldingsOf, out var lines))
                foreach (var section in _folding.AllFoldings)
                    section.IsFolded = lines.Contains(_editor.Document.GetLineByOffset(section.StartOffset).LineNumber);
        }

        _colorizer.Spans = _viewModel.Spans;
        _values.Values = _viewModel.LineValues;
        _current.Line = _viewModel.CurrentLine;
        _fault.Line = _viewModel.FaultLine;
        var shown = _viewModel.CurrentLine > 0 ? _viewModel.CurrentLine : _viewModel.FaultLine;
        if (shown > 0 && shown <= _editor.Document.LineCount)
            // la línea que se va a ejecutar (o la del fallo) nunca queda dentro de un bloque plegado
            foreach (var section in _folding.GetFoldingsContaining(_editor.Document.GetLineByNumber(shown).Offset))
                section.IsFolded = false;
        _margin.InvalidateVisual();
        _editor.TextArea.TextView.Redraw();
        if (shown > 0 && shown <= _editor.Document.LineCount)
        {
            _editor.ScrollTo(shown, 0);
            _editor.TextArea.Caret.Line = shown;   // «Punto de parada» y «Ejecutar hasta el cursor» van a la línea marcada
        }
    }

    /// <summary>El botón derecho: ver espacios y tabuladores, la regla vertical y el tamaño de letra de siempre.</summary>
    private System.Windows.Controls.ContextMenu ViewMenu()
    {
        var whitespace = new System.Windows.Controls.MenuItem { Header = EditorTexts.ShowWhitespace, IsCheckable = true };
        whitespace.Click += (_, _) => _editor.Options.ShowSpaces = _editor.Options.ShowTabs = whitespace.IsChecked;
        var ruler = new System.Windows.Controls.MenuItem { Header = EditorTexts.ShowRuler, IsCheckable = true };
        ruler.Click += (_, _) => _editor.Options.ShowColumnRuler = ruler.IsChecked;
        var zoom = new System.Windows.Controls.MenuItem { Header = EditorTexts.ResetZoom };
        zoom.Click += (_, _) => _editor.FontSize = NormalFontSize;
        return new System.Windows.Controls.ContextMenu { Items = { whitespace, ruler, new System.Windows.Controls.Separator(), zoom } };
    }

    /// <summary>La pareja de un bloque (IF … END_IF) o las demás apariciones de un nombre, con un fondo gris tenue.</summary>
    private sealed class HighlightRenderer : IBackgroundRenderer
    {
        private static readonly Brush Fill = Brush("#38888780");

        public IReadOnlyList<StRange> Ranges { get; set; } = [];

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var length = textView.Document?.TextLength ?? 0;
            foreach (var range in Ranges)
            {
                if (range.Offset + range.Length > length) continue;
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = range.Offset, Length = range.Length }))
                    drawingContext.DrawRoundedRectangle(Fill, null, rect, 2, 2);
            }
        }
    }

    /// <summary>Lo plegado del bloque o la sección que se deja de ver (las líneas donde empieza), para dejarlo igual al volver.</summary>
    private void RememberFolded()
    {
        if (_foldingsOf is null) return;
        _folded[_foldingsOf] = _folding.AllFoldings.Where(f => f.IsFolded)
            .Select(f => _editor.Document.GetLineByOffset(f.StartOffset).LineNumber).ToHashSet();
    }

    // ---------------------------------------------------------------- pinceles

    private static Brush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>Los mismos colores que el editor de FactoryNodes; los valores a la derecha (comentario) en gris.</summary>
    private static readonly Dictionary<StSpanKind, (Brush Brush, bool Bold, bool Italic)> Styles = new()
    {
        [StSpanKind.Keyword] = (Brush("#4A3B78"), true, false),
        [StSpanKind.Type] = (Brush("#2F5D8A"), false, false),
        [StSpanKind.Comment] = (Brush("#888780"), false, true),
        [StSpanKind.Directive] = (Brush("#8A5A1E"), true, false),
        [StSpanKind.String] = (Brush("#8A3B2A"), false, false),
        [StSpanKind.Number] = (Brush("#2F5D8A"), false, false),
        [StSpanKind.Parameter] = (Brush("#5B4A99"), false, false),
        [StSpanKind.Constant] = (Brush("#1F6F8B"), false, false),
        [StSpanKind.Function] = (Brush("#3A3A8C"), false, false),
        [StSpanKind.Unknown] = (Brush("#A32D2D"), false, false)
    };

    private sealed class SpanColorizer : DocumentColorizingTransformer
    {
        public IReadOnlyList<StSpan> Spans { get; set; } = [];

        protected override void ColorizeLine(DocumentLine line)
        {
            var length = CurrentContext.Document.TextLength;
            foreach (var span in Spans)
            {
                if (span.Offset >= line.EndOffset) break;
                var start = Math.Max(span.Offset, line.Offset);
                var end = Math.Min(Math.Min(span.Offset + span.Length, line.EndOffset), length);
                if (end <= start || !Styles.TryGetValue(span.Kind, out var style)) continue;
                ChangeLinePart(start, end, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(style.Brush);
                    if (style.Bold || style.Italic)
                    {
                        var face = element.TextRunProperties.Typeface;
                        element.TextRunProperties.SetTypeface(new Typeface(face.FontFamily,
                            style.Italic ? FontStyles.Italic : face.Style, style.Bold ? FontWeights.SemiBold : face.Weight, face.Stretch));
                    }
                });
            }
        }
    }

    /// <summary>
    /// La línea que se ejecutará en el próximo paso: fondo dorado tenue (docs/STYLE.md: dorado = seleccionado); la del fallo, rojo
    /// tenue.
    /// </summary>
    private sealed class CurrentLineRenderer(Brush? fill = null) : IBackgroundRenderer
    {
        private readonly Brush Fill = fill ?? Brush("#40D6A24C");

        public int Line { get; set; }

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Line < 1 || textView.Document is null || Line > textView.Document.LineCount) return;
            var segment = new TextSegment { StartOffset = textView.Document.GetLineByNumber(Line).Offset, Length = 0 };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment, true))
                drawingContext.DrawRectangle(Fill, null, new Rect(0, rect.Top, Math.Max(textView.ActualWidth, textView.DocumentHeight), rect.Height));
        }
    }

    /// <summary>
    /// Los valores de cada línea, pintados al final sin ser parte del texto: la raya «│» y los valores sobre fondo Mist, en gris (no son
    /// código ni un «or», autor 2026-10-21). El texto no cambia con cada paso, así que no se pierden lo plegado ni la selección.
    /// </summary>
    private sealed class ValuesGenerator : VisualLineElementGenerator
    {
        private static readonly Brush Fill = Brush("#A0DCD6E8");
        private static readonly Brush Text = Brush("#6B6A64");

        public IReadOnlyList<string> Values { get; set; } = [];

        private string ValueOf(DocumentLine line) => line.LineNumber - 1 < Values.Count ? Values[line.LineNumber - 1] : "";

        public override int GetFirstInterestedOffset(int startOffset)
        {
            var line = CurrentContext.VisualLine.LastDocumentLine;
            return startOffset <= line.EndOffset && ValueOf(line).Length > 0 ? line.EndOffset : -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var line = CurrentContext.Document.GetLineByOffset(offset);
            if (offset != line.EndOffset || ValueOf(line) is not { Length: > 0 } values) return null;
            var text = new System.Windows.Controls.TextBlock
            {
                Text = "│ " + values,
                FontFamily = CurrentContext.TextView.GetValue(TextBlock.FontFamilyProperty) as FontFamily,
                FontStyle = FontStyles.Italic,
                Foreground = Text
            };
            var box = new System.Windows.Controls.Border
            {
                Child = text, Background = Fill, Padding = new Thickness(4, 0, 6, 0), Margin = new Thickness(24, 0, 0, 0),
                CornerRadius = new CornerRadius(3)
            };
            return new InlineObjectElement(0, box);
        }
    }

    /// <summary>Los textos del panel «Buscar» (Ctrl+F) en el idioma de SoftNodes.</summary>
    private sealed class SearchTexts : ICSharpCode.AvalonEdit.Search.Localization
    {
        public override string MatchCaseText => EditorTexts.MatchCase;
        public override string MatchWholeWordsText => EditorTexts.MatchWholeWords;
        public override string UseRegexText => EditorTexts.UseRegex;
        public override string FindNextText => EditorTexts.FindNext;
        public override string FindPreviousText => EditorTexts.FindPrevious;
        public override string ErrorText => EditorTexts.Error;
        public override string NoMatchesFoundText => EditorTexts.NoMatches;
    }

    /// <summary>
    /// El margen izquierdo: el número de línea del fichero y, con un clic, el punto de parada (un punto rojo). Es el único
    /// «control» del código: el depurador no deja editar.
    /// </summary>
    private sealed class LineMargin : AbstractMargin
    {
        private static readonly Brush HoverFill = Brush("#55888780");

        public DebugViewModel? ViewModel { get; set; }

        public Action<int>? Toggle { get; set; }

        private int _hover;

        protected override Size MeasureOverride(Size availableSize) => new(60, 0);

        /// <summary>Con fondo (transparente) todo el margen recibe el clic, no solo las cifras.</summary>
        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters) => new PointHitTestResult(this, hitTestParameters.HitPoint);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var line = LineAt(e);
            if (line == _hover) return;
            _hover = line;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = 0;
            InvalidateVisual();
        }

        private int LineAt(MouseEventArgs e)
        {
            if (TextView is null) return 0;
            var y = e.GetPosition(TextView).Y + TextView.VerticalOffset;
            return TextView.GetVisualLineFromVisualTop(y)?.FirstDocumentLine.LineNumber ?? 0;
        }

        protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
        {
            if (oldTextView is not null) oldTextView.VisualLinesChanged -= Redraw;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null) newTextView.VisualLinesChanged += Redraw;
        }

        private void Redraw(object? sender, EventArgs e) => InvalidateVisual();

        protected override void OnRender(DrawingContext context)
        {
            var textView = TextView;
            if (textView is null || !textView.VisualLinesValid || ViewModel is null) return;
            var numbers = ViewModel.LineNumbers;
            var breakpoints = ViewModel.BreakpointLines.ToHashSet();
            var typeface = new Typeface("Consolas");
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            foreach (var visual in textView.VisualLines)
            {
                var line = visual.FirstDocumentLine.LineNumber;
                var top = visual.GetTextLineVisualYPosition(visual.TextLines[0], VisualYPosition.TextTop) - textView.VerticalOffset;
                if (line - 1 < numbers.Count)
                {
                    var text = new FormattedText(numbers[line - 1].ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, typeface, 12, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    context.DrawText(text, new Point(60 - 6 - text.Width, top));
                }

                if (breakpoints.Contains(line))
                    context.DrawEllipse(Brushes.Firebrick, null, new Point(10, top + 8), 5, 5);
                else if (line == _hover)
                    context.DrawEllipse(HoverFill, null, new Point(10, top + 8), 5, 5);   // aquí se pondría un punto de parada
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (TextView is null || Toggle is null) return;
            var y = e.GetPosition(TextView).Y + TextView.VerticalOffset;
            if (TextView.GetVisualLineFromVisualTop(y) is { } visual) Toggle(visual.FirstDocumentLine.LineNumber);
            InvalidateVisual();
            e.Handled = true;
        }
    }
}
