# Protocolo Soporte LAN v1

Todos los enteros son **little-endian**. Los textos son UTF-8 sin terminador.

## Mensaje

```
[u8 type][u32 payloadLength][payload]
```

El payload máximo aceptado es de 32 MiB.

## Tipos

| Código | Nombre | Dirección | Payload |
|--------|--------|-----------|---------|
| 0x01 | `AUTH_REQUEST` | visor → agente | PIN en texto (6 dígitos) |
| 0x02 | `AUTH_RESULT` | agente → visor | `[u8 ok][mensaje utf8]` |
| 0x03 | `SCREEN_INFO` | agente → visor | `[u16 width][u16 height][u16 tileSize][i16 originX][i16 originY]` |
| 0x04 | `FRAME` | agente → visor | deflate (ver abajo) |
| 0x05 | `CURSOR` | agente → visor | `[i16 x][i16 y][u8 visible]` (relativo al origen) |
| 0x10 | `MOUSE_MOVE` | visor → agente | `[i32 x][i32 y]` (coordenadas absolutas de pantalla virtual) |
| 0x11 | `MOUSE_BUTTON` | visor → agente | `[u8 botón: 0=izq 1=der 2=medio][u8 down]` |
| 0x12 | `MOUSE_WHEEL` | visor → agente | `[i16 delta]` (múltiplos de 120) |
| 0x13 | `KEY` | visor → agente | `[u16 virtualKey][u8 down]` |
| 0x14 | `CLIPBOARD` | ambos | texto utf8 (reservado, v2) |
| 0x20 | `PING` | ambos | vacío |
| 0x21 | `PONG` | ambos | vacío |

## FRAME

El payload completo está comprimido con **zlib** (`DeflateStream` de .NET,
`DecompressionStream('deflate')` en el navegador).

Contenido descomprimido:

```
[u32 frameId][u16 tileCount]
repetido tileCount veces:
  [u16 x][u16 y][u16 w][u16 h][u8 flags]
  flags = 0 → sólido:  [u8 r][u8 g][u8 b]
  flags = 1 → crudo:   w*h*3 bytes en RGB
```

Los tiles miden 64×64 salvo los del borde. Solo se envían los tiles que
cambiaron respecto al frame anterior. Si no cambió nada, no se envía `FRAME`.

## Flujo de una sesión

1. TCP a `ip:port` (por defecto 50000).
2. TLS. El visor **no** valida la CA: compara la huella SHA-256 del
   certificado con la que anunció el agente por UDP.
3. `AUTH_REQUEST` con el PIN.
4. `AUTH_RESULT`. Si `ok=0`, el agente cierra.
5. `SCREEN_INFO`.
6. `FRAME` / `CURSOR` de forma continua.
7. El visor envía `MOUSE_*` y `KEY` cuando el técnico interactúa.

## Descubrimiento

El agente envía por UDP al puerto **47820** (broadcast dirigido a cada
interfaz y a 255.255.255.255) cada 5 segundos:

```json
{"app":"soporte-lan","v":1,"name":"PC-01","port":50000,"fp":"A1B2...","os":"Windows 10"}
```

`fp` es la huella SHA-256 del certificado en hexadecimal mayúsculas, sin
dos puntos.
