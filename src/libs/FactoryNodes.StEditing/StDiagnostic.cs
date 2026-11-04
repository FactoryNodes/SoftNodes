namespace FactoryNodes.StEditing;

public enum StSeverity
{
    Error,
    Warning
}

/// <summary>
/// Un error o aviso sobre un texto: posición (<see cref="Offset"/> en caracteres desde el principio, y su línea y
/// columna, empezando en 1), longitud del trozo que se subraya, código estable (<c>ST0xx</c>) y mensaje.
/// </summary>
public sealed record StDiagnostic(int Offset, int Length, int Line, int Column, StSeverity Severity, string Code, string Message)
{
    public override string ToString() => $"{Line}:{Column} {Code} {Message}";
}

/// <summary>Línea y columna (desde 1) de una posición de un texto.</summary>
internal sealed class LineMap
{
    private readonly List<int> _starts = [0];

    public LineMap(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') _starts.Add(i + 1);
    }

    public (int Line, int Column) At(int offset)
    {
        var index = _starts.BinarySearch(offset);
        if (index < 0) index = ~index - 1;
        return (index + 1, offset - _starts[index] + 1);
    }
}
