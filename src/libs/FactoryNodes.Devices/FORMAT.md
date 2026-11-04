# Catálogo de dispositivos: un JSON por marca y modelo

`library/devices/<marca>/<modelo>.json` (UTF-8). Lo lee `DeviceModel.Parse`, que dice qué no vale.

```json
{
  "schemaVersion": 1,
  "brand": "Omron", "model": "MX2", "kind": "Variador",
  "description": "…", "source": "de dónde salen las direcciones", "verified": false,
  "svg": "<svg …>…</svg>",
  "protocol": {
    "registerBase": 1,              // 1: el manual numera desde 1 (trama = dirección − 1); 0: ya es la de la trama
    "wordOrder": "HighFirst",       // 32 bits: palabra alta primero (HighFirst) o baja (LowFirst)
    "maxRegistersPerRead": 16,
    "save": { "address": "0x0900", "value": 1, "description": "Guardar en EEPROM" },   // o null
    "restoreNotes": "lo que hay que saber antes de restaurar"
  },
  "groups": [ { "number": 1, "text": "Datos del motor" } ],
  "parameters": [ {
    "code": "F002",                 // código del fabricante: único; clave en las copias
    "address": "0x1103",            // como en el manual: «0x…» hexadecimal o decimal; vacía si el equipo solo habla EtherNet/IP
    "cip": "0x64/1/0x08",           // opcional (2026-10-27, PENDIENTES 45): la dirección EtherNet/IP, clase/instancia/atributo; valor
                                    // little-endian de 2 bytes (INT/UINT) o 4 (DINT/UDINT/REAL). Al menos una de las dos direcciones
    "type": "UDINT",                // UINT, INT (1 registro); UDINT, DINT, REAL (2)
    "scale": 0.01, "unit": "s",     // valor = registro × scale
    "description": "Tiempo de aceleración 1",
    "group": 2,                     // 0 = sin grupo (al final)
    "basic": true,                  // sale primero, en «Básicos»
    "kind": "Setting",              // Setting (se guarda y restaura) o Live (en vivo: solo se lee)
    "writable": true,               // false: se guarda pero no se escribe
    "options": { "0": "Adelante", "1": "Atrás" },   // texto de cada valor (claves numéricas)
    "bits": { "0": "Listo" },       // palabra de bits: se enseñan los que están a 1
    "note": "rango, de fábrica, unidad que depende de otro parámetro…",
    "decimalsFrom": { "from": "DspDiv", "shift": 0, "mask": 0, "map": { "5": 2 } },   // o null: ver abajo
    "calibration": true             // de la calibración de este equipo: al restaurar, sin marcar
  } ],
  "commands": [ {                   // órdenes como botones (cero, tara…); [] si no tiene
    "text": "Tara", "address": "0x0090", "value": 212,
    "clearFirst": true,             // escribir 0 antes (eNod4)
    "response": { "address": "0x0091", "done": 2, "error": 3 },   // o null: no se espera respuesta
    "confirm": false,               // true: pide confirmación (lo que mueve algo o cambia la calibración)
    "note": "lo que hace"
  } ]
}
```

## Decimales que da otro parámetro (`decimalsFrom`, básculas)
El peso de una báscula es un entero cuyos decimales dependen de otro parámetro: decimales = `(crudo de from >> shift) & mask`
(`mask` 0 = todos) y, con `map`, lo que diga la tabla (clave: ese valor en número). Se suman a los de `scale`. El
parámetro `from` no puede tomar los suyos de otro. Sin leer `from`, se enseña sin coma.
- DAT 400: `{ "from": "DspDiv", "map": { "0": 3, … "5": 2, … "14": 0 } }` (código del valor de división).
- eNod4: `{ "from": "StabDec", "shift": 8, "mask": 255 }` (byte alto de 0x0008 = número de decimales).

## Calibración (`calibration`)
Capacidad, sensibilidad, puntos y pendientes de calibración, cero de calibración, valor de división…: son de cada báscula.
Se guardan en las copias, pero al restaurar una copia o cargar un CSV llegan **sin marcar** (en otra báscula la
estropearían); se marcan a mano si es la misma báscula. Se pueden cambiar a mano en el módulo Modbus.

## Copia en CSV (`DeviceBackup.ToCsv`)
Punto y coma, UTF-8. Primera línea `# FactoryNodes;copia de parámetros;<dispositivo>;<marca modelo>;<fecha ISO>;<quién>;<nota>`;
segunda, `Código;Crudo;Valor;Descripción`; luego una fila por parámetro. Al importar manda la columna «Crudo».
