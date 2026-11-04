using FactoryNodes.Iec61131.Syntax;

namespace FactoryNodes.StEditing.Syntax;

/// <summary>Un token de <see cref="StLexer"/> con su posición en el texto.</summary>
public readonly record struct PToken(StTokenKind Kind, string Text, int Offset)
{
    public int End => Offset + Text.Length;

    public bool Is(string symbol) => Kind == StTokenKind.Symbol && Text == symbol;

    /// <summary>Palabra (identificador) sin distinguir mayúsculas: <c>IF</c>, <c>end_if</c>…</summary>
    public bool IsWord(string word) => Kind == StTokenKind.Identifier && Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<PToken> From(string text)
    {
        var result = new List<PToken>();
        var offset = 0;
        foreach (var token in StLexer.Tokenize(text))
        {
            result.Add(new PToken(token.Kind, token.Text, offset));
            offset += token.Text.Length;
        }

        return result;
    }
}

// ---------------------------------------------------------------- Expresiones

public abstract record StExpr(int Offset, int End);

/// <summary><c>TRUE</c>, <c>12</c>, <c>1.5</c>, <c>T#2s</c>, <c>WORD#16#0001</c>, <c>'texto'</c>.</summary>
public sealed record LiteralExpr(PToken Token) : StExpr(Token.Offset, Token.End);

public sealed record NameExpr(PToken Name) : StExpr(Name.Offset, Name.End);

/// <summary><c>Data.Status</c>; también el acceso a un bit, <c>w.3</c> (<see cref="Member"/> es un número).</summary>
public sealed record MemberExpr(StExpr Target, PToken Member) : StExpr(Target.Offset, Member.End);

public sealed record IndexExpr(StExpr Target, IReadOnlyList<StExpr> Indices, int Close) : StExpr(Target.Offset, Close);

/// <summary>Un argumento: con nombre (<c>IN := x</c>, <c>Q =&gt; y</c>) o por posición.</summary>
public sealed record StArgument(PToken? Name, bool IsOutput, StExpr Value);

public sealed record CallExpr(StExpr Target, IReadOnlyList<StArgument> Arguments, int Close) : StExpr(Target.Offset, Close);

public sealed record UnaryExpr(PToken Operator, StExpr Operand) : StExpr(Operator.Offset, Operand.End);

public sealed record BinaryExpr(PToken Operator, StExpr Left, StExpr Right) : StExpr(Left.Offset, Right.End);

public sealed record ParenExpr(StExpr Inner, int Open, int Close) : StExpr(Open, Close);

/// <summary>Lo que no se pudo leer (ya hay un error de sintaxis en su sitio).</summary>
public sealed record ErrorExpr(int At) : StExpr(At, At);

// ---------------------------------------------------------------- Sentencias

public abstract record StStatement(int Offset);

public sealed record AssignStatement(StExpr Target, PToken Operator, StExpr Value) : StStatement(Target.Offset);

public sealed record CallStatement(CallExpr Call) : StStatement(Call.Offset);

/// <summary>Una expresión suelta que no es una llamada (<c>x;</c>): no hace nada.</summary>
public sealed record ExpressionStatement(StExpr Expression) : StStatement(Expression.Offset);

public sealed record IfBranch(StExpr Condition, IReadOnlyList<StStatement> Body);

public sealed record IfStatement(PToken Keyword, IReadOnlyList<IfBranch> Branches, IReadOnlyList<StStatement>? Else) : StStatement(Keyword.Offset);

/// <summary>Etiqueta de un CASE: un valor o un rango <c>a..b</c>.</summary>
public sealed record CaseLabel(StExpr From, StExpr? To);

public sealed record CaseBranch(IReadOnlyList<CaseLabel> Labels, IReadOnlyList<StStatement> Body);

public sealed record CaseStatement(PToken Keyword, StExpr Selector, IReadOnlyList<CaseBranch> Branches, IReadOnlyList<StStatement>? Else)
    : StStatement(Keyword.Offset);

public sealed record ForStatement(PToken Keyword, PToken Variable, StExpr From, StExpr To, StExpr? By, IReadOnlyList<StStatement> Body)
    : StStatement(Keyword.Offset);

public sealed record WhileStatement(PToken Keyword, StExpr Condition, IReadOnlyList<StStatement> Body) : StStatement(Keyword.Offset);

public sealed record RepeatStatement(PToken Keyword, IReadOnlyList<StStatement> Body, StExpr Until) : StStatement(Keyword.Offset);

/// <summary><c>EXIT</c>, <c>RETURN</c> o <c>CONTINUE</c>.</summary>
public sealed record JumpStatement(PToken Keyword) : StStatement(Keyword.Offset);

/// <summary>Lo leído de un cuerpo: sus sentencias y los errores de sintaxis.</summary>
public sealed record StBodySyntax(string Text, IReadOnlyList<PToken> Tokens, IReadOnlyList<StStatement> Statements, IReadOnlyList<StDiagnostic> Errors);
