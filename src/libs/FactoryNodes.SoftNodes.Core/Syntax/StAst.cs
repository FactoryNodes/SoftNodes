namespace FactoryNodes.SoftNodes.Syntax;

// Árbol del cuerpo de una POU. Cada sentencia lleva su línea (1 = primera del fuente de su fichero) para el paso a paso.

public abstract record Expr;

public sealed record LiteralExpr(object Value) : Expr;

/// <summary>Un nombre con accesos: <c>Data.Status.Q</c>, <c>Params.Elements[i].Max</c>.</summary>
public sealed record PathExpr(IReadOnlyList<PathPart> Parts) : Expr
{
    public override string ToString() => string.Concat(Parts.Select((p, i) => (i > 0 && p.Index is null ? "." : "") + p));
}

/// <summary>Un nombre o un índice (<c>Name</c> con <c>Index</c> = null; o <c>Name[Index]</c>).</summary>
public sealed record PathPart(string Name, Expr? Index)
{
    public override string ToString() => Index is null ? Name : Name + "[…]";
}

public sealed record UnaryExpr(string Op, Expr Operand) : Expr;

public sealed record BinaryExpr(string Op, Expr Left, Expr Right) : Expr;

/// <summary>Llamada a una función en una expresión (<c>TENTHS_TO_TIME(Data.StopDelay)</c>).</summary>
public sealed record CallExpr(string Name, IReadOnlyList<CallArg> Args) : Expr;

/// <summary><c>Nombre := valor</c>, <c>valor</c> (posicional) o <c>Nombre =&gt; destino</c> (salida).</summary>
public sealed record CallArg(string? Name, Expr Value, bool IsOutput = false);

public abstract record Stmt(int Line);

public sealed record AssignStmt(PathExpr Target, Expr Value, int Line) : Stmt(Line);

/// <summary>Llamada a la instancia de un bloque de función (<c>tStop(IN := x, PT := y);</c>) o a una función sin usar su resultado.</summary>
public sealed record FbCallStmt(PathExpr Instance, IReadOnlyList<CallArg> Args, int Line) : Stmt(Line);

public sealed record IfBranch(Expr Condition, IReadOnlyList<Stmt> Body, int Line);

public sealed record IfStmt(IReadOnlyList<IfBranch> Branches, IReadOnlyList<Stmt>? Else, int Line) : Stmt(Line);

/// <summary>Un rango de una etiqueta de CASE (<c>1</c>, <c>2..5</c>).</summary>
public sealed record CaseRange(Expr From, Expr? To);

public sealed record CaseBranch(IReadOnlyList<CaseRange> Labels, IReadOnlyList<Stmt> Body, int Line);

public sealed record CaseStmt(Expr Selector, IReadOnlyList<CaseBranch> Branches, IReadOnlyList<Stmt>? Else, int Line) : Stmt(Line);

public sealed record ForStmt(string Variable, Expr From, Expr To, Expr? By, IReadOnlyList<Stmt> Body, int Line) : Stmt(Line);

public sealed record WhileStmt(Expr Condition, IReadOnlyList<Stmt> Body, int Line) : Stmt(Line);

public sealed record RepeatStmt(IReadOnlyList<Stmt> Body, Expr Until, int Line) : Stmt(Line);

public sealed record ExitStmt(int Line) : Stmt(Line);

public sealed record ReturnStmt(int Line) : Stmt(Line);

/// <summary>Un error de sintaxis con su línea.</summary>
public sealed class StSyntaxException(string message, string file, int line) : Exception($"{file}:{line}: {message}")
{
    public string File { get; } = file;
    public int Line { get; } = line;
}
