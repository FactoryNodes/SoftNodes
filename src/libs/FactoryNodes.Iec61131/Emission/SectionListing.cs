namespace FactoryNodes.Iec61131.Emission;

/// <summary>
/// El código de una sección del programa tal como lo escribe un emisor (el FB <c>"fnc050_Motors"</c> de TIA, el programa
/// <c>fnc050_Motors</c> de Sysmac), para enseñarlo en un depurador (SoftNodes, PENDIENTES 87). Cada sentencia que se ejecuta tiene su
/// número, la primera la 1: el código del usuario del principio (si lo hay, PENDIENTES 88); por cada elemento de la sección, en orden,
/// su bloqueo, su orden de marcha, cada línea del mapeo de señales (también las comentadas, que no se ejecutan pero cuentan) y la
/// llamada a su bloque; en la de salidas físicas, cada salida; y el código del usuario del final.
/// </summary>
/// <param name="Lines">Las líneas del código, sin saltos de línea.</param>
/// <param name="StatementLines">La línea (desde 1, en <paramref name="Lines"/>) de cada sentencia: la de la sentencia n es la n-1.</param>
public sealed record SectionListing(IReadOnlyList<string> Lines, IReadOnlyList<int> StatementLines);
