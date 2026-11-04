# FORMAT – EtherNet/IP (CIP), mensajes explícitos con un Omron NJ/NX

_Fabricante / versiones vistas:_ OMRON NJ101-9000, firmware 1.47, puerto EtherNet/IP integrado rev. 2.09 (ListIdentity),
con el programa de Natucer18 (2026-10-27, en la oficina del autor, `Tools/CipProbe`).

## Estructura
- TCP 44818. Encapsulado de 24 bytes (little endian): orden, longitud, sesión, estado, contexto (8), opciones.
- `RegisterSession` (0x65, versión 1) → sesión; `SendRRData` (0x6F) con dos elementos: dirección nula (0x0000) y datos sin
  conexión (0x00B2) con la petición CIP; `ListIdentity` (0x63) por la misma conexión TCP o por difusión UDP.
- Petición CIP: servicio, tamaño de la ruta en palabras, ruta, datos. Respuesta: servicio | 0x80, reservado, estado general,
  palabras de estado adicional, datos.
- Ruta de una variable: segmentos simbólicos `0x91 len texto [relleno]`; índices con segmentos de elemento (0x28/0x29/0x2A).

## Campos conocidos
| Qué | Formato | Certeza |
|---|---|---|
| Ruta hasta la CPU | Se puede hablar **directamente** con el Message Router del NJ; también acepta Unconnected Send (0x52) con ruta `1,0` | Confirmado (NJ101) |
| Miembros de estructura | `Var.Miembro.Sub` vale **tramo a tramo** (un 0x91 por tramo) **y** con el nombre entero en un 0x91 | Confirmado (NJ101) |
| Read Tag (0x4C), datos de la petición | número de elementos, UINT = 1 | Confirmado |
| Respuesta de Read Tag | tipo (1 byte), longitud de la información adicional (1 byte, 0 en los simples), información adicional, valor | Confirmado |
| BOOL | `C1 00` + **2 bytes** (`0000` falso) | Confirmado (falso); verdadero, por ver |
| INT | `C3 00` + 2 bytes (`006C` = 27648) | Confirmado |
| UINT | `C7 00` + 2 bytes | Confirmado |
| WORD | `D2 00` + 2 bytes | Confirmado |
| REAL | `CA 00` + 4 bytes | Confirmado |
| Variable que no existe | estado general **0x04** (no 0x05), sin datos | Confirmado (NJ101) |
| Write Tag (0x4D) | tipo, información adicional, UINT 1, valor con el mismo tamaño que al leer | Confirmado (NJ101): INT `RawMax` = 27648 (el mismo valor), estado 0 |
| Escribir con «Publish Only» | **Se puede**: `RawMax` es una global retentiva publicada «Publish Only» (EMITTER_RULES V13) | Confirmado (NJ101, Write Tag con estado 0) |
| Multiple Service Packet (0x0A) a la clase 2 instancia 1 | número, desplazamientos, peticiones; respuesta con estado 0 | Confirmado (NJ101): 5 variables en 13–22 ms (la primera vuelta, 41 ms) |
| Elemento de un array dentro de una estructura (`Sim.Used[0]`) | segmentos simbólicos `Sim`, `Used` y de elemento `28 00`; se lee (`D2 00` + 2 bytes) y **se escribe** (Write Tag con `D2 00`, UINT 1, 2 bytes, agrupadas en Multiple Service Packet) | Confirmado (NJ101, 2026-10-29, Natucer20 con el banco de SoftNodes: Sysmac monitoriza `Sim.Used[0] = 16#FFC0` y `Sim.Bits[0]` pasando de `16#E7C0` a `16#E7C2` al girar un selector en SoftNodes) |
| Escribir un valor distinto del actual | Igual que el mismo valor: estado 0 y el PLC lo tiene en el ciclo siguiente | Confirmado (NJ101, 2026-10-29, las palabras de `Sim`) |

## Desconocido
- Escribir un BOOL (se supone igual que el INT: `C1 00`, 2 bytes).
- STRING (se supone longitud en una palabra y los caracteres), arrays enteros, LINT/LREAL, TIME/DATE, estructuras enteras (0xA0 + CRC).
- Tamaño máximo de una respuesta agrupada (se parte por mitades si el PLC dice 0x11).
- Cuántas sesiones admite la CPU a la vez con Sysmac y el HMI conectados.

## Get_Attribute_Single en el NJ101 (2026-10-27, `CipProbe 192.168.1.39 atributo …`, Multiple Service Packet)

| Atributo | Respuesta | Qué es |
|---|---|---|
| `0x01/1/1` | `2F00` | Fabricante 47 (Omron) |
| `0x01/1/2` | `0C00` | Tipo de equipo 12 |
| `0x01/1/3` | `8006` | Producto 1664 |
| `0x01/1/4` | `0209` | Revisión 2.09 (del puerto EtherNet/IP) |
| `0x01/1/6` | `D2FED401` | Serie `01D4FED2` (little-endian) |
| `0x01/1/7` | `054E4A313031` | SHORT_STRING «NJ101» (longitud delante) |
| `0xF5/1/5` | `2701A8C0 00FFFFFF …` | Objeto TCP/IP: IP 192.168.1.39, máscara 255.255.255.0 |
| `0x01/1/0x50` | estado `0x14` | Atributo que no existe: solo esa respuesta falla |
| `0x99/1/1` | estado `0x05` | Clase que no existe |

La ruta lógica de 8 bits (`20 cc 24 ii 30 aa`) y las respuestas mezcladas en un mismo paquete funcionan como en un equipo cualquiera.

## Tiempos en el NJ101 (2026-10-27, Natucer18)

- Leer o escribir por nombre: unos **2 ms por variable**, en lotes de 10, 20 o 40 igual (407 BOOL: leer 0,85–0,93 s; escribir 0,71–0,85 s). Lo que
  cuesta es cada variable, no cada paquete.
- Con nombres largos (`SimUse_e_EV_EV_EntradaExtrusora_D2_Cerrada`) un lote de 40 cabe en un mensaje sin conexión.

