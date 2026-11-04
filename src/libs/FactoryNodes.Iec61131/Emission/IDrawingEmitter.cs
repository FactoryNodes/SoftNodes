namespace FactoryNodes.Iec61131.Emission;

/// <summary>
/// Un destino de exportación que, en lugar de código, escribe los <b>dibujos</b> del proyecto (p. ej. SVG para abrirlo en
/// un programa de dibujo). Es un <see cref="IPlcEmitter"/> más para que salga en el mismo desplegable y se use con los
/// mismos botones (proyecto entero o pestaña), pero no necesita que el programa se pueda generar: la app le pasa los
/// dibujos tal como están, sin enlazar ni validar. Genérico para que esta lib no conozca los modelos de dibujo
/// (los da quien implementa, p. ej. <c>FactoryNodes.Nodes.ProjectDrawings</c>).
/// </summary>
public interface IDrawingEmitter<in TDrawings> : IPlcEmitter
{
    /// <summary>Un fichero por pestaña con lo que tenga dibujado. Sin nada que dibujar, ningún fichero.</summary>
    EmitResult EmitDrawings(TDrawings drawings, EmitContext context);
}
