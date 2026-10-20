// Valida la ruta de entrada sin mover el cursor: lee la posición actual del
// equipo remoto y envía un MOUSE_MOVE exactamente a esa misma coordenada.
//
//   node tools/test-input.mjs 127.0.0.1:50000 123456

import tls from 'node:tls';

const [target = '127.0.0.1:50000', pin = '123456'] = process.argv.slice(2);
const [host, portText] = target.split(':');

const Msg = { AuthRequest: 0x01, AuthResult: 0x02, ScreenInfo: 0x03, Cursor: 0x05, MouseMove: 0x10 };

const socket = tls.connect({ host, port: Number(portText), rejectUnauthorized: false }, () => {
  socket.write(encode(Msg.AuthRequest, Buffer.from(pin)));
});

let pending = Buffer.alloc(0);
let origin = { x: 0, y: 0 };
let sent = false;

socket.on('data', (chunk) => {
  pending = Buffer.concat([pending, chunk]);

  while (pending.length >= 5) {
    const type = pending[0];
    const length = pending.readUInt32LE(1);
    if (pending.length < 5 + length) break;

    const payload = pending.subarray(5, 5 + length);
    pending = pending.subarray(5 + length);
    handle(type, payload);
  }
});

function handle(type, payload) {
  if (type === Msg.AuthResult && payload[0] !== 1) {
    console.error('Auth rechazada:', payload.subarray(1).toString());
    process.exit(2);
  }

  if (type === Msg.ScreenInfo) {
    origin = { x: payload.readInt16LE(6), y: payload.readInt16LE(8) };
  }

  if (type === Msg.Cursor && !sent) {
    sent = true;
    const x = payload.readInt16LE(0) + origin.x;
    const y = payload.readInt16LE(2) + origin.y;

    const move = Buffer.alloc(8);
    move.writeInt32LE(x, 0);
    move.writeInt32LE(y, 4);
    socket.write(encode(Msg.MouseMove, move));

    console.log(`MOUSE_MOVE enviado a (${x}, ${y}) — misma posición, sin movimiento visible.`);
    setTimeout(() => {
      console.log('Ruta de entrada OK: el agente sigue en sesión.');
      socket.end();
      process.exit(0);
    }, 600);
  }
}

function encode(type, payload) {
  const header = Buffer.alloc(5);
  header[0] = type;
  header.writeUInt32LE(payload.length, 1);
  return Buffer.concat([header, payload]);
}

setTimeout(() => {
  console.error('No se recibió posición del cursor.');
  process.exit(3);
}, 15000);
