// Genera docs/img/visor.png con datos de ejemplo, sin exponer equipos reales.
// Requiere el bridge en marcha:  node dist-visor/visor.cjs  (o npm run dev)
//
//   node tools/captura-visor.mjs [salida.png]

import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(new URL('../viewer/package.json', import.meta.url));
const WebSocket = require('ws');

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const output = resolve(process.argv[2] ?? join(root, 'docs', 'img', 'visor.png'));
const url = process.env.VIEWER_URL ?? 'http://127.0.0.1:7788';
const debugPort = 9333;

const candidates = [
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
];

const executable = candidates.find((path) => {
  try {
    return require('node:fs').existsSync(path);
  } catch {
    return false;
  }
});

if (!executable) {
  console.error('No encuentro Edge ni Chrome.');
  process.exit(1);
}

const profile = mkdtempSync(join(tmpdir(), 'soporte-shot-'));
const browser = spawn(
  executable,
  [
    '--headless=new',
    `--remote-debugging-port=${debugPort}`,
    `--user-data-dir=${profile}`,
    '--window-size=1400,900',
    '--force-device-scale-factor=2',
    '--hide-scrollbars',
    '--no-first-run',
    url,
  ],
  { stdio: 'ignore' },
);

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

async function target() {
  for (let attempt = 0; attempt < 50; attempt++) {
    try {
      const list = await fetch(`http://127.0.0.1:${debugPort}/json/list`).then((r) => r.json());
      const page = list.find((item) => item.type === 'page' && item.webSocketDebuggerUrl);
      if (page) return page;
    } catch {
      // Aún arrancando.
    }
    await sleep(200);
  }
  throw new Error('El navegador no expuso ninguna pestaña.');
}

const page = await target();
const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((done, fail) => {
  socket.once('open', done);
  socket.once('error', fail);
});

let counter = 0;
const pending = new Map();

socket.on('message', (raw) => {
  const message = JSON.parse(raw.toString());
  const resolver = pending.get(message.id);
  if (resolver) {
    pending.delete(message.id);
    resolver(message.result);
  }
});

const send = (method, params = {}) =>
  new Promise((done) => {
    const id = ++counter;
    pending.set(id, done);
    socket.send(JSON.stringify({ id, method, params }));
  });

await send('Page.enable');
await sleep(2500);

// Datos de ejemplo: la lista real se sustituye solo para la captura.
await send('Runtime.evaluate', {
  expression: `(() => {
    const list = document.getElementById('devices');
    if (!list) return 'sin lista';

    const item = document.createElement('li');
    item.className = 'device';

    const name = document.createElement('div');
    name.className = 'device-name';
    name.textContent = 'PC-RECEPCION';

    const meta = document.createElement('div');
    meta.className = 'device-meta';
    meta.textContent = '192.168.1.42 · Windows 11 Pro 24H2';

    item.append(name, meta);
    list.replaceChildren(item);
    return 'ok';
  })()`,
});

await sleep(400);

const shot = await send('Page.captureScreenshot', { format: 'png' });
mkdirSync(dirname(output), { recursive: true });
writeFileSync(output, Buffer.from(shot.data, 'base64'));
console.log(`Captura: ${output}`);

socket.close();
browser.kill();
await sleep(400);
rmSync(profile, { recursive: true, force: true });
process.exit(0);
