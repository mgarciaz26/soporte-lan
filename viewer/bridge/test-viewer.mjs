// Prueba del bridge: simula al visor web sin abrir navegador.
//   node test-viewer.mjs [ip] [puerto] [pin]

import WebSocket from 'ws';
import zlib from 'node:zlib';

const [ip = '127.0.0.1', port = '50000', pin = '123456'] = process.argv.slice(2);

const socket = new WebSocket('ws://127.0.0.1:7788');
socket.binaryType = 'nodebuffer';

let buffer = Buffer.alloc(0);
let gotScreen = false;

socket.on('open', () => {
  console.log('bridge conectado');
  socket.send(JSON.stringify({ type: 'connect', ip, port: Number(port), pin }));
});

socket.on('message', (data, isBinary) => {
  if (!isBinary) {
    const message = JSON.parse(data.toString());
    if (message.type === 'devices') {
      console.log(`dispositivos: ${message.devices.map((d) => `${d.name}@${d.ip}`).join(', ') || '(ninguno)'}`);
    } else {
      console.log('control:', message.type, message.state ?? '', message.message ?? '');
    }
    return;
  }

  buffer = Buffer.concat([buffer, data]);
  while (buffer.length >= 5) {
    const type = buffer[0];
    const length = buffer.readUInt32LE(1);
    if (buffer.length < 5 + length) break;

    const payload = buffer.subarray(5, 5 + length);
    buffer = buffer.subarray(5 + length);

    if (type === 0x03) {
      gotScreen = true;
      console.log(`SCREEN_INFO: ${payload.readUInt16LE(0)}x${payload.readUInt16LE(2)}`);
    }

    if (type === 0x04) {
      const raw = zlib.inflateSync(payload);
      console.log(`FRAME: ${raw.readUInt16LE(4)} tiles, ${(payload.length / 1024).toFixed(1)} KiB`);
      socket.close();
      process.exit(gotScreen ? 0 : 4);
    }
  }
});

socket.on('error', (error) => {
  console.error('error:', error.message);
  process.exit(1);
});

setTimeout(() => {
  console.error('timeout');
  process.exit(3);
}, 20000);
