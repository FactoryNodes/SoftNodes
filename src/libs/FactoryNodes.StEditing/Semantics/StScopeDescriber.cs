using System.Text;

namespace FactoryNodes.StEditing.Semantics;

/// <summary>
/// Lo que ve el cuerpo de una POU como texto para quien no tiene el editor delante (el asistente de IA): sus variables y, de las
/// estructuras, sus miembros por su camino (<c>Data.Control.Stop : BOOL  // …</c>), con el tipo y el comentario de la librería.
/// Es lo mismo que enseña el autocompletado, en una lista.
/// </summary>
public static class StScopeDescriber
{
    /// <param name="maxDepth">Cuántos niveles de estructura se abren (<c>Data.Control.X</c> son tres).</param>
    /// <param name="maxLines">Tope de líneas (una estructura enorme no se vuelca entera); si se corta lo dice al final.</param>
    public static string Describe(StScope scope, int maxDepth = 3, int maxLines = 160)
    {
        var lines = new List<string>();
        var truncated = false;

        void Add(string path, string typeText, string? comment)
        {
            if (lines.Count >= maxLines)
            {
                truncated = true;
                return;
            }

            lines.Add($"{path} : {typeText}" + (string.IsNullOrWhiteSpace(comment) ? "" : "  // " + comment.Trim()));
        }

        void Walk(string path, StType type, int depth)
        {
            if (depth >= maxDepth || type.Kind != StTypeKind.Struct) return;
            foreach (var member in scope.MembersOf(type))
            {
                var child = $"{path}.{member.Name}";
                Add(child, member.TypeText, member.Comment);
                Walk(child, member.Type, depth + 1);
            }
        }

        foreach (var local in scope.Locals.Where(l => l.Kind is not StSymbolKind.ReturnValue))
        {
            Add(local.Name, local.TypeText, local.Comment);
            Walk(local.Name, local.Type, 1);
        }

        var text = new StringBuilder(string.Join("\n", lines));
        if (truncated) text.Append($"\n… (solo las primeras {maxLines} líneas)");
        return text.ToString();
    }
}
