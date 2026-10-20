// Cliente de prueba del protocolo. No forma parte del producto: sirve para
// validar TLS, autenticación y decodificación de frames desde la terminal.
//
//   node tools/test-client.mjs 127.0.0.1:50000 123456 [salida.bmp]

import tls from 'node:tls';
import zlib from 'node:zlib';
import fs from 'node:fs';

const [target = '127.0.0.1:50000', pin = '123456', output = 'frame.bmp'] = process.argv.slice(2);
const [host, portText] = target.split(':');
const port = Number(portText);

const Msg = {
  AuthRequest: 0x01,
  AuthResult: 0x02,
  ScreenInfo: 0x03,
  Frame: 0x04,
  Cursor: 0x05,
};

const socket = tls.connect({ host, port, rejectUnauthorized: false }, () => {
  const cert = socket.getPeerCertificate();
  console.log('TLS OK, huella:', cert.fingerprint256);
  socket.write(encode(Msg.AuthRequest, Buffer.from(pin)));
});

socket.on('error', (error) => {
  console.error('Error:', error.message);
  process.exit(1);
});

let pending = Buffer.alloc(0);
let screen = null;
let done = false;

socket.on('data', (chunk) => {
  pending = Buffer.concat([pending, chunk]);

  while (pending.length >= 5) {
    const type = pending[0];
    const length = pending.readUInt32LE(1);
    if (pending.length < 5 + length) break;

    const payload = pending.subarray(5, 5 + length);
    pending = pending.subarray(5 + length);

    handle(type, Buffer.from(payload));
  }
});

function handle(type, payload) {
  switch (type) {
    case Msg.AuthResult: {
      const ok = payload[0] === 1;
      console.log('AUTH:', ok ? 'aceptado' : 'rechazado', '-', payload.subarray(1).toString('utf8'));
      if (!ok) process.exit(2);
      break;
    }

    case Msg.ScreenInfo: {
      const width = payload.readUInt16LE(0);
      const height = payload.readUInt16LE(2);
      const tile = payload.readUInt16LE(4);
      const originX = payload.readInt16LE(6);
      const originY = payload.readInt16LE(8);
      screen = { width, height, tile, originX, originY };
      console.log(`Pantalla: ${width}x${height}, tile ${tile}, origen (${originX}, ${originY})`);
      break;
    }

    case Msg.Frame: {
      if (!screen || done) return;
      done = true;

      const started = Date.now();
      const raw = zlib.inflateSync(payload);
      const frameId = raw.readUInt32LE(0);
      const count = raw.readUInt16LE(4);

      const rgb = Buffer.alloc(screen.width * screen.height * 3, 0);
      let offset = 6;

      for (let i = 0; i < count; i++) {
        const x = raw.readUInt16LE(offset);
        const y = raw.readUInt16LE(offset + 2);
        const w = raw.readUInt16LE(offset + 4);
        const h = raw.readUInt16LE(offset + 6);
        const flags = raw[offset + 8];
        offset += 9;

        if (flags === 0) {
          const r = raw[offset];
          const g = raw[offset + 1];
          const b = raw[offset + 2];
          offset += 3;

          for (let row = 0; row < h; row++) {
            let target = ((y + row) * screen.width + x) * 3;
            for (let column = 0; column < w; column++) {
              rgb[target++] = r;
              rgb[target++] = g;
              rgb[target++] = b;
            }
          }
        } else {
          for (let row = 0; row < h; row++) {
            let target = ((y + row) * screen.width + x) * 3;
            for (let column = 0; column < w; column++) {
              rgb[target++] = raw[offset++];
              rgb[target++] = raw[offset++];
              rgb[target++] = raw[offset++];
            }
          }
        }
      }

      fs.writeFileSync(output, toBmp(rgb, screen.width, screen.height));
      console.log(
        `Frame #${frameId}: ${count} tiles, ${(payload.length / 1024).toFixed(1)} KiB comprimidos, ` +
        `${Date.now() - started} ms -> ${output}`,
      );

      socket.end();
      process.exit(0);
      break;
    }

    case Msg.Cursor:
      break;
  }
}

function encode(type, payload) {
  const header = Buffer.alloc(5);
  header[0] = type;
  header.writeUInt32LE(payload.length, 1);
  return Buffer.concat([header, payload]);
}

function toBmp(rgb, width, height) {
  const rowSize = Math.ceil((width * 3) / 4) * 4;
  const pixelSize = rowSize * height;
  const file = Buffer.alloc(54 + pixelSize, 0);

  file.write('BM', 0);
  file.writeUInt32LE(54 + pixelSize, 2);
  file.writeUInt32LE(54, 10);
  file.writeUInt32LE(40, 14);
  file.writeInt32LE(width, 18);
  file.writeInt32LE(height, 22);
  file.writeUInt16LE(1, 26);
  file.writeUInt16LE(24, 28);
  file.writeUInt32LE(pixelSize, 34);

  for (let y = 0; y < height; y++) {
    const source = (height - 1 - y) * width * 3;
    const target = 54 + y * rowSize;
    for (let x = 0; x < width; x++) {
      file[target + x * 3] = rgb[source + x * 3 + 2];
      file[target + x * 3 + 1] = rgb[source + x * 3 + 1];
      file[target + x * 3 + 2] = rgb[source + x * 3];
    }
  }

  return file;
}

setTimeout(() => {
  console.error('Tiempo de espera agotado.');
  process.exit(3);
}, 20000);
