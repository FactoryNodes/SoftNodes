# FORMAT – Lo que contesta el servidor EtherNet/IP de SoftNodes

_Referencia: lo comprobado con el NJ101 real en `FactoryNodes.Monitoring.Cip/FORMAT.md` (2026-10-27/29). Lo que allí es «confirmado» aquí se
imita; lo que allí es «desconocido» aquí es una suposición marcada._

## Encapsulado (TCP, puerto de la instancia; 44818 la primera)
| Orden | Respuesta | Certeza |
|---|---|---|
| RegisterSession 0x65 | sesión nueva (contador), datos `01 00 00 00`, contexto del cliente devuelto tal cual | Como el NJ |
| UnregisterSession 0x66 | se cierra la conexión | Como el NJ |
| ListIdentity 0x63 (por TCP) | un elemento 0x0C: versión 1, sockaddr (familia 2, puerto, IP local), fabricante **0**, tipo 14, producto 1, rev. 1.00, serie (hash del nombre), «SoftNodes <nombre>», estado 3 | Formato del NJ; fabricante 0 es nuestro (reservado en ODVA) |
| SendRRData 0x6F con otra sesión | estado 0x0064 (sesión no válida) | CIP Vol. 2 |
| SendUnitData 0x70 (2026-11-03, PENDIENTES 195) | elementos 0xA1 (id O→T que dimos) y 0xB1 (contador + petición CIP); se contesta con 0xA1 = id T→O del cliente y 0xB1 = el mismo contador + la respuesta. Id que no es de una conexión abierta: estado 0x0003 | CIP Vol. 2; probado solo con nuestra prueba (los bytes de la pantalla) |
| Otra orden | estado 0x0001 (no admitida) | CIP Vol. 2 |
| ListIdentity por **UDP** | **no se contesta** (solo TCP) | Por hacer si hace falta descubrir SoftNodes en la red |

## Servicios CIP
| Servicio | Respuesta | Certeza |
|---|---|---|
| Read Tag 0x4C, variable simple | estado 0; tipo (1 byte), 0, valor | Confirmado en el NJ |
| Read Tag de una estructura / array | estado 0; `A0 02 00 00` / `A3 02 00 00`, sin valor (el CRC va a 0) | Nuestro cliente lo acepta («se leen sus miembros»). **Un NJ101 real no**: `CIPUCMMRead` de `EIP_OUT_x` entera contra SoftNodes da `ErrorID 16#1C04` y no lee nada (2026-10-30, PENDIENTES 167). Por eso el emisor Sysmac lee de un SoftNodes campo a campo, como de un TIA |
| ListServices 0x04 | un elemento 0x0100 «Communications», versión 1, capacidad 0x0020 | **Confirmado con el NJ101 real (2026-10-30)**: el NJ lo manda lo primero, antes de RegisterSession; cuando el servidor contestaba 0x0001 (comando no admitido), el NJ daba `16#1C04` (timeout) y no seguía. Con la respuesta, la secuencia es ListServices → RegisterSession → SendRRData |
| Lo que manda un NJ101 por `CIPUCMMRead 'EIP_OUT_PLC_O.Layout'` | SendRRData, plazo 7, dos elementos, Read Tag 0x4C con dos segmentos simbólicos 0x91 (`EIP_OUT_PLC_O` y `Layout`, relleno a palabra) y 1 elemento; el valor vuelve con su tipo (D3 DWORD, C7 UINT, C1 BOOL en 2 bytes) y el NJ lo escribe en su variable | **Confirmado (2026-10-30)**, unas 30 peticiones por segundo sobre una conexión TCP que mantiene abierta. El registro de la instancia apunta las primeras 12 peticiones de cada cliente enteras (`SoftCipServer.Logged`) |
| Read Tag de una instancia de bloque o un nombre que no existe | estado **0x04** | Confirmado en el NJ (0x04, no 0x05) |
| Write Tag 0x4D | se decodifica con el tipo que trae la petición y se convierte al de la casilla (`StInterpreter.Coerce`); estado 0; tipo que no cuadra, 0x20; estructura, 0x08 | Confirmado el camino normal |
| Multiple Service Packet 0x0A a 2/1 | número, desplazamientos, respuestas; estado 0, o **0x1E** si alguna falló | Confirmado en el NJ |
| Unconnected Send 0x52 a 6/1 | la respuesta del mensaje de dentro, tal cual | Confirmado en el NJ |
| Get_Attribute_Single 0x0E en 1/1 | 1 fabricante, 2 tipo, 3 producto, 4 revisión, 5 estado, 6 serie, 7 nombre (SHORT_STRING); otro atributo 0x14; otra clase 0x05 | Confirmado en el NJ |
| Forward_Close 0x4E a 6/1 | estado 0 y la triada (serie, fabricante, serie del originador), aunque no hubiera nada abierto | Nuestra elección: la pantalla lo manda para limpiar antes de abrir (2026-11-03) |
| Forward_Open 0x54 / Large_Forward_Open 0x5B a 6/1 | clase 3: estado 0; id O→T nuestro, id T→O del cliente, triada, los RPI pedidos como intervalos reales, 0, 0. Otra clase: 0x01 con estado adicional 0x0103 | CIP Vol. 1. Lo que pidió la pantalla (driver Omron NJ/NX, 2026-11-03): `5B 02 20 06 24 01 06 9A 00000033 23489D9D 019A 3000 5A01A8C0 07 000000 286B6E00 CC07F843 286B6E00 CC07F843 A3 03 01 00 20 02 24 01` (clase 3, Message Router, RPI ≈7,2 s, 1996 bytes) |
| Otro servicio | 0x08 | CIP Vol. 1 |

## Tipos (código y tamaño)
| Tipo del intérprete | Código | Bytes | Certeza |
|---|---|---|---|
| BOOL | `C1` | **2** (`01 00` / `00 00`) | Confirmado el falso; el verdadero se supone `01 00` |
| SINT/USINT/BYTE | `C2`/`C6`/`D1` | 1 | Códigos CIP estándar |
| INT/UINT/WORD | `C3`/`C7`/`D2` | 2 | Confirmado |
| DINT/UDINT/DWORD | `C4`/`C8`/`D3` | 4 | Códigos CIP estándar |
| LINT/ULINT/LWORD | `C5`/`C9`/`D4` | 8 | Códigos CIP estándar |
| REAL / LREAL | `CA` / `CB` | 4 / 8 | Confirmado REAL |
| TIME | `C5` (LINT) en **nanosegundos** (el intérprete guarda milisegundos) | 8 | Suposición: TIME de Sysmac es de 64 bits en ns; por comprobar qué código manda el NJ |
| STRING | `D0`, longitud en una palabra + UTF-8 | 2 + n | Suposición (la misma del cliente) |

## Desconocido
- El CRC de una estructura (0xA0): el NJ lo calcula del tipo; aquí va a 0 porque las estructuras se leen por miembros.
- ListIdentity por UDP (descubrir SoftNodes en la red): no se contesta.
