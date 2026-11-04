using System.Text.RegularExpressions;
using FactoryNodes.Iec61131.Model;
using FactoryNodes.SoftNodes.Syntax;

namespace FactoryNodes.SoftNodes.Runtime;

public sealed partial class StInterpreter
{
    /// <summary>El valor de una expresión. Las funciones se ejecutan enteras (no se entra en ellas al ir paso a paso).</summary>
    public object Evaluate(Expr expr, Frame frame)
    {
        switch (expr)
        {
            case LiteralExpr literal:
                return literal.Value;

            case PathExpr path:
                if (path.Parts is [{ Index: null } only] && !frame.Vars.ContainsKey(only.Name) && !Globals.ContainsKey(only.Name) &&
                    Library.Size(only.Name) is { } size)
                    return (long)size.Value;
                return ResolveSlot(path, frame, 0).Value;

            case UnaryExpr unary:
                var operand = Evaluate(unary.Operand, frame);
                return unary.Op switch
                {
                    "NOT" => operand switch { bool b => !b, long n => ~n, _ => throw Fault("NOT necesita un BOOL o un entero.", frame, 0) },
                    _ => operand switch { long n => -n, double d => -d, _ => throw Fault("El signo menos necesita un número.", frame, 0) }
                };

            case BinaryExpr binary:
                return Binary(binary, frame);

            case CallExpr call:
                return CallFunction(call.Name, call.Args, frame, 0);

            default:
                throw new InvalidOperationException("Expresión desconocida: " + expr);
        }
    }

    private object Binary(BinaryExpr e, Frame frame)
    {
        var left = Evaluate(e.Left, frame);
        // AND y OR de BOOL no evalúan el segundo operando si no hace falta (como el cortocircuito de los PLC modernos)
        if (left is bool lb)
        {
            if (e.Op == "AND" && !lb) return false;
            if (e.Op == "OR" && lb) return true;
        }

        var right = Evaluate(e.Right, frame);

        switch (e.Op)
        {
            case "AND" or "OR" or "XOR":
                if (left is bool a && right is bool b)
                    return e.Op switch { "AND" => a && b, "OR" => a || b, _ => a ^ b };
                var x = AsLong(left);
                var y = AsLong(right);
                return e.Op switch { "AND" => x & y, "OR" => x | y, _ => x ^ y };

            case "=" or "<>":
                var equal = left is bool or string || right is bool or string
                    ? Equals(left, right)
                    : AsDouble(left) == AsDouble(right);
                return e.Op == "=" ? equal : !equal;

            case "<" or ">" or "<=" or ">=":
                var l = AsDouble(left);
                var r = AsDouble(right);
                return e.Op switch { "<" => l < r, ">" => l > r, "<=" => l <= r, _ => l >= r };

            case "**":
                return Math.Pow(AsDouble(left), AsDouble(right));
        }

        if (left is double || right is double)
        {
            var l = AsDouble(left);
            var r = AsDouble(right);
            return e.Op switch
            {
                "+" => l + r,
                "-" => l - r,
                "*" => l * r,
                "/" => l / r,
                _ => throw Fault($"«{e.Op}» no vale con REAL.", frame, 0)
            };
        }

        var p = AsLong(left);
        var q = AsLong(right);
        switch (e.Op)
        {
            case "+": return p + q;
            case "-": return p - q;
            case "*": return p * q;
            case "/" or "MOD":
                if (q == 0) throw Fault("División entre 0.", frame, 0);
                return e.Op == "/" ? p / q : p % q;
            default:
                throw Fault($"Operador «{e.Op}» desconocido.", frame, 0);
        }
    }

    private static long AsLong(object value) => value switch
    {
        long n => n,
        bool b => b ? 1 : 0,
        double d => (long)Math.Round(d, MidpointRounding.ToEven),
        _ => throw new InvalidCastException()
    };

    private static double AsDouble(object value) => value switch
    {
        double d => d,
        long n => n,
        bool b => b ? 1 : 0,
        _ => throw new InvalidCastException()
    };

    // ---------------------------------------------------------------- Funciones

    private static readonly Regex Conversion = new(@"^(?<from>[A-Z_0-9]+?)_TO_(?<to>[A-Z_0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Llama a una función de la librería (<c>TENTHS_TO_TIME</c>…) o estándar (conversiones, MIN, MAX, ABS, LIMIT…).</summary>
    public object CallFunction(string name, IReadOnlyList<CallArg> args, Frame caller, int line)
    {
        try
        {
            // CLOCK_MS (compat del PID): su cuerpo de referencia es el de TIA (TIME_TCK); aquí, el reloj del intérprete
            if (name.Equals("CLOCK_MS", StringComparison.OrdinalIgnoreCase)) return (double)NowMs;
            if (_pous.TryGetValue(name, out var pou) && pou.Kind == PouKind.Function) return CallLibraryFunction(pou, args, caller, line);
            var values = args.Select(a => Evaluate(a.Value, caller)).ToList();
            return Builtin(name, values, caller, line);
        }
        catch (InvalidCastException)
        {
            throw Fault($"Argumentos no válidos en {name}.", caller, line);
        }
    }

    private object CallLibraryFunction(Pou pou, IReadOnlyList<CallArg> args, Frame caller, int line)
    {
        var vars = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in pou.Sections)
            foreach (var member in section.Members)
                vars[member.Name] = NewSlot(member);
        var returnType = TypeOf(pou.ReturnType ?? "BOOL");
        vars[pou.Name] = new Slot(returnType, NewValue(returnType));

        var inputs = pou.VariablesOf(VarSectionKind.Input).ToList();
        for (var i = 0; i < args.Count; i++)
        {
            var parameter = args[i].Name ?? (i < inputs.Count ? inputs[i].Name : throw Fault($"Sobran argumentos en {pou.Name}.", caller, line));
            if (!vars.TryGetValue(parameter, out var slot)) throw Fault($"{pou.Name} no tiene el parámetro «{parameter}».", caller, line);
            Assign(slot, Evaluate(args[i].Value, caller), caller, line);
        }

        var frame = new Frame(pou.Name, pou.Location.File, vars, caller.Depth + 1);
        foreach (var _ in ExecuteStatements(Body(pou), frame)) { }
        return vars[pou.Name].Value;
    }

    private object Builtin(string name, List<object> v, Frame frame, int line)
    {
        var upper = name.ToUpperInvariant();
        switch (upper)
        {
            case "ABS": return v[0] is double d0 ? Math.Abs(d0) : Math.Abs(AsLong(v[0]));
            case "SQRT": return Math.Sqrt(AsDouble(v[0]));
            case "TRUNC": return (long)Math.Truncate(AsDouble(v[0]));
            case "MIN": return v.Skip(1).Aggregate(v[0], (a, b) => AsDouble(b) < AsDouble(a) ? b : a);
            case "MAX": return v.Skip(1).Aggregate(v[0], (a, b) => AsDouble(b) > AsDouble(a) ? b : a);
            case "LIMIT":   // LIMIT(MN, IN, MX)
                return AsDouble(v[1]) < AsDouble(v[0]) ? v[0] : AsDouble(v[1]) > AsDouble(v[2]) ? v[2] : v[1];
            case "SEL":     // SEL(G, IN0, IN1)
                return (bool)v[0] ? v[2] : v[1];
        }

        if (Conversion.Match(name) is { Success: true } m)
        {
            var target = m.Groups["to"].Value;
            if (target.Equals("TIME", StringComparison.OrdinalIgnoreCase)) return Coerce(v[0], StType.Time);
            if (StType.Elementary.TryGetValue(target, out var type)) return Coerce(v[0], type);
        }

        throw Fault($"La función «{name}» no existe.", frame, line);
    }
}
