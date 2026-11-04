# FORMAT – Subconjunto ST de la librería maestra

_Fuente: `library/master/*.st`, UTF-8, LF o CRLF. Reglas completas: `docs/MASTER_LIBRARY_CONVENTIONS.md`._

## Estructura de un fichero
- Nivel superior: líneas vacías, comentarios `//` y `(* *)` (ignorados), `/// COMPAT` (antes de una FUNCTION), `TYPE`, `FUNCTION`, `FUNCTION_BLOCK`. Cualquier otra cosa es error `ML002`.
- `TYPE <Nombre> :` (con `STRUCT` en la misma línea o en la siguiente) … `END_STRUCT;` … `END_TYPE`. Solo STRUCT.
- `FUNCTION <Nombre> : <Tipo>` / `FUNCTION_BLOCK <Nombre>`, secciones `VAR_INPUT`, `VAR_OUTPUT`, `VAR_IN_OUT`, `VAR`, `VAR CONSTANT` y, en un FB, `VAR_TEMP` (variables de ciclo, elementales y sin valor inicial; `VAR RETAIN` se lee para dar error), cuerpo, `END_FUNCTION` / `END_FUNCTION_BLOCK`.
- El cuerpo empieza en la primera línea no vacía que no es una sección; se quitan las líneas vacías del principio y del final.

## Declaración (una por línea)
`Nombre[, Nombre…] : TIPO [:= valor]; [/// directiva] [// comentario]` — el tipo es un identificador o, desde el 2026-10-01, `ARRAY[a..b] OF identificador` (una dimensión; `a` y `b`, enteros o constantes `/// SIZE`). Fuera de los bloques, `/// SIZE NOMBRE número` declara una constante de tamaño. Un comentario `//` en su propia línea dentro de un STRUCT o de una sección se conserva como `CommentLine`.

## Directivas
| Dónde | Directiva |
|---|---|
| Nivel superior | `/// COMPAT` |
| Final de un miembro | `/// IO <DI\|DO\|AI\|AO> [REQUIRED \| DEFAULT <valor>]` (valor: constante o ruta `Status.X` desde la estructura raíz) |
| Cuerpo, sola en su línea | `/// REGION <título>`, `/// END_REGION` |

Todo lo demás es error `ML001`. Las directivas van en mayúsculas.
