// Prueba del cambio de monitor: conecta, lee la lista de pantallas y va
// seleccionando cada una para comprobar que SCREEN_INFO cambia de región.
//
//   node tools/test-monitor.mjs 127.0.0.1:50000 123456

import tls from 'node:tls';

const [target = '127.0.0.1:50000', pin = '123456'] = process.argv.slice(2);
const [host, portText] = target.split(':');
const port = Number(portText);

const Msg = {
  AuthRequest: 0x01,
  AuthResult: 0x02,
  ScreenInfo: 0x03,
  Frame: 0x04,
  SelectMonitor: 0x15,
};

const socket = tls.connect({ host, port, rejectUnauthorized: false }, () => {
  socket.write(encode(Msg.AuthRequest, Buffer.from(pin)));
});

socket.on('error', (error) => {
  console.error('Error:', error.message);
  process.exit(1);
});

let pending = Buffer.alloc(0);
let phase = 'initial';
let monitors = [];
let expected = null;
let frames = 0;

socket.on('data', (chunk) => {
  pending = Buffer.concat([pending, chunk]);

  while (pending.length >= 5) {
    const type = pending[0];
    const length = pending.readUInt32LE(1);
    if (pending.length < 5 + length) break;

    const payload = Buffer.from(pending.subarray(5, 5 + length));
    pending = pending.subarray(5 + length);
    handle(type, payload);
  }
});

function handle(type, payload) {
  if (type === Msg.AuthResult) {
    if (payload[0] !== 1) {
      console.error('AUTH rechazado:', payload.subarray(1).toString('utf8'));
      process.exit(2);
    }
    console.log('AUTH aceptado');
    return;
  }

  if (type === Msg.Frame) {
    frames += 1;
    if (phase === 'finish' && frames > 0) done();
    return;
  }

  if (type !== Msg.ScreenInfo) return;

  const width = payload.readUInt16LE(0);
  const height = payload.readUInt16LE(2);
  const originX = payload.readInt16LE(6);
  const originY = payload.readInt16LE(8);
  const selected = payload.length >= 12 ? payload[10] : 255;

  if (payload.length >= 12) {
    const count = payload[11];
    let offset = 12;
    monitors = [];

    for (let index = 0; index < count; index++, offset += 9) {
      monitors.push({
        primary: (payload[offset] & 1) !== 0,
        x: payload.readInt16LE(offset + 1),
        y: payload.readInt16LE(offset + 3),
        width: payload.readUInt16LE(offset + 5),
        height: payload.readUInt16LE(offset + 7),
      });
    }
  }

  console.log(
    `SCREEN_INFO: ${width}x${height} en (${originX},${originY}) · ` +
    `${selected === 255 ? 'todas' : `pantalla ${selected}`} · ${monitors.length} monitor(es)`,
  );

  if (phase === 'initial') {
    for (const [index, monitor] of monitors.entries()) {
      console.log(
        `  [${index}] ${monitor.width}x${monitor.height} en (${monitor.x},${monitor.y})` +
        (monitor.primary ? ' principal' : ''),
      );
    }

    if (monitors.length === 0) {
      console.error('El agente no envió la lista de monitores.');
      process.exit(3);
    }

    expected = {
      x: Math.min(...monitors.map((monitor) => monitor.x)),
      y: Math.min(...monitors.map((monitor) => monitor.y)),
      width: Math.max(...monitors.map((monitor) => monitor.x + monitor.width)) -
        Math.min(...monitors.map((monitor) => monitor.x)),
      height: Math.max(...monitors.map((monitor) => monitor.y + monitor.height)) -
        Math.min(...monitors.map((monitor) => monitor.y)),
    };

    check('todas', width, height, originX, originY);
    phase = 'select-0';
    console.log('→ Seleccionando pantalla 0');
    socket.write(encode(Msg.SelectMonitor, Buffer.from([0])));
    return;
  }

  if (phase === 'select-0') {
    expected = monitors[0];
    check('pantalla 0', width, height, originX, originY);
    phase = 'select-all';
    console.log('→ Volviendo a todas');
    socket.write(encode(Msg.SelectMonitor, Buffer.from([255])));
    return;
  }

  if (phase === 'select-all') {
    expected = {
      x: Math.min(...monitors.map((monitor) => monitor.x)),
      y: Math.min(...monitors.map((monitor) => monitor.y)),
      width: Math.max(...monitors.map((monitor) => monitor.x + monitor.width)) -
        Math.min(...monitors.map((monitor) => monitor.x)),
      height: Math.max(...monitors.map((monitor) => monitor.y + monitor.height)) -
        Math.min(...monitors.map((monitor) => monitor.y)),
    };
    check('todas', width, height, originX, originY);

    if (monitors.length > 1) {
      phase = 'select-1';
      console.log('→ Seleccionando pantalla 1');
      socket.write(encode(Msg.SelectMonitor, Buffer.from([1])));
    } else {
      console.log('Solo hay una pantalla: no se prueba el cambio a otra.');
      finish();
    }
    return;
  }

  if (phase === 'select-1') {
    expected = monitors[1];
    check('pantalla 1', width, height, originX, originY);
    phase = 'finish';
    console.log('→ Esperando un frame de la pantalla 1…');
    return;
  }
}

function check(label, width, height, originX, originY) {
  const ok =
    width === expected.width &&
    height === expected.height &&
    originX === expected.x &&
    originY === expected.y;

  if (!ok) {
    console.error(
      `FALLO (${label}): recibido ${width}x${height} en (${originX},${originY}), ` +
      `esperado ${expected.width}x${expected.height} en (${expected.x},${expected.y})`,
    );
    process.exit(4);
  }

  console.log(`OK (${label}): ${width}x${height} en (${originX},${originY})`);
}

function finish() {
  phase = 'finish';
  console.log('→ Esperando un frame…');
}

function done() {
  console.log(`OK: ${frames} frame(s) recibidos tras el cambio.`);
  socket.end();
  process.exit(0);
}

function encode(type, payload) {
  const header = Buffer.alloc(5);
  header[0] = type;
  header.writeUInt32LE(payload.length, 1);
  return Buffer.concat([header, payload]);
}

setTimeout(() => {
  console.error(`Tiempo de espera agotado (fase: ${phase}).`);
  process.exit(5);
}, 20000);
