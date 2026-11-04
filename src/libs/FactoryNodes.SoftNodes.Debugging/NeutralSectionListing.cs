using FactoryNodes.Iec61131.Emission;

namespace FactoryNodes.SoftNodes.Debugging;

/// <summary>
/// El código de una sección del programa en ST neutro, sin los emisores de FactoryNodes (PENDIENTES 159): lo que enseña el depurador del SoftNodes
/// abierto. SoftNodes Pro enseña el de cada PLC (el SCL de TIA, el ST de Sysmac); este dice lo mismo con los nombres del programa
/// (<c>hw_P.M101.Control.Run_Conditions := …</c>). Una sentencia por línea, en el orden y con el número con que las recorre el intérprete
/// (<see cref="SoftProgram"/>): el código del usuario del principio; por cada elemento, su bloqueo, su orden, cada línea del mapeo (también las
/// comentadas, que no se ejecutan pero cuentan) y la llamada a su bloque; en la de salidas, cada salida; y el código del usuario del final.
/// </summary>
public static class NeutralSectionListing
{
    /// <summary>La sección <paramref name="section"/> (<c>fnc050_Motors</c>, también la de salidas), o null si no está o es de usuario.</summary>
    public static SectionListing? For(SoftInstance instance, string section) => For(instance.Program.Model, section);

    public static SectionListing? For(ProgramModel model, string sectionName)
    {
        var section = model.Sections.Append(model.OutputSection).FirstOrDefault(s => s.FullName.Equals(sectionName, StringComparison.OrdinalIgnoreCase));
        if (section is null || section.User is not null) return null;
        var lines = new List<string> { $"// {section.FullName}" };
        var statements = new List<int>();
        void Statement(string text)
        {
            lines.Add(text);
            statements.Add(lines.Count);
        }

        if (section.Prologue is { } start) Statement($"{UserName(start)}();   // código del usuario al principio");
        if (section == model.OutputSection)
        {
            if (model.Outputs.Count > 0) lines.Add("");
            foreach (var output in model.Outputs)
                Statement($"{model.IoName}.{output.Physical} := {(output.Not ? "NOT " : "")}{model.DataName}.{output.Element}.{output.Member};" +
                          (output.Comment.Length > 0 ? $"   // {output.Comment}" : ""));
        }
        else
        {
            foreach (var element in section.Elements)
            {
                lines.Add("");
                lines.Add($"// {element.Name}" + (element.Description.Length > 0 ? $": {element.Description}" : "") + $" ({element.FunctionBlock})");
                foreach (var assignment in new[] { element.Lock, element.Run }.Concat(element.Mapping))
                    Statement(Assignment(model, element, assignment));
                var companions = element.Companions.Select(c => $", {c.Parameter} := {model.DataName}.{c.Name}");
                Statement($"{element.Instance}(Data := {model.DataName}.{element.Name}{string.Concat(companions)});");
            }
        }

        if (section.Epilogue is { } end)
        {
            lines.Add("");
            Statement($"{UserName(end)}();   // código del usuario al final");
        }

        return new SectionListing(lines, statements);
    }

    private static string Assignment(ProgramModel model, ProgramElement element, ProgramAssignment assignment)
    {
        var target = $"{model.DataName}.{assignment.Variable ?? element.Name}.{assignment.Target}";
        var text = $"{target} := {Value(model, element, assignment.Value)};";
        if (assignment.When is { } when) text = $"IF {Value(model, element, when)} THEN {text} END_IF;";
        if (assignment.Comment is { Length: > 0 } comment) text += $"   // {comment}";
        return assignment.Commented ? "// " + text : text;
    }

    private static string Value(ProgramModel model, ProgramElement element, ProgramValue value)
    {
        var text = value.Kind switch
        {
            ProgramValueKind.IoVariable => $"{model.IoName}.{value.Text}",
            ProgramValueKind.OwnMember => $"{model.DataName}.{element.Name}.{value.Text}",
            ProgramValueKind.Expression => value.Resolve(name => $"{model.DataName}.{name}", (_, literal) => literal, SoftProgram.ClockName),
            _ => value.Text
        };
        return value.Not ? $"NOT ({text})" : text;
    }

    private static string UserName(ProgramUserCode user) =>
        user.FunctionBlock.StartsWith("FB_", StringComparison.Ordinal) ? user.FunctionBlock[3..] : user.FunctionBlock;
}
