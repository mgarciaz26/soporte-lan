// Prueba de interfaz del selector de monitores.
// Requiere el agente en marcha (PIN fijo) y el bridge en http://127.0.0.1:7788.
//
//   node tools/test-ui-monitor.mjs [captura.png]

import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(new URL('../viewer/package.json', import.meta.url));
const WebSocket = require('ws');

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const shotPath = resolve(process.argv[2] ?? join(root, 'visor-monitor.png'));
const url = process.env.VIEWER_URL ?? 'http://127.0.0.1:7788';
const debugPort = 9341;

const candidates = [
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
];

const executable = candidates.find((path) => require('node:fs').existsSync(path));
if (!executable) {
  console.error('No encuentro Edge ni Chrome.');
  process.exit(1);
}

const profile = mkdtempSync(join(tmpdir(), 'soporte-ui-'));
const browser = spawn(
  executable,
  [
    '--headless=new',
    `--remote-debugging-port=${debugPort}`,
    `--user-data-dir=${profile}`,
    '--window-size=1400,900',
    '--hide-scrollbars',
    '--no-first-run',
    url,
  ],
  { stdio: 'ignore' },
);

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

async function page() {
  for (let attempt = 0; attempt < 60; attempt++) {
    try {
      const list = await fetch(`http://127.0.0.1:${debugPort}/json/list`).then((response) => response.json());
      const target = list.find((item) => item.type === 'page' && item.webSocketDebuggerUrl);
      if (target) return target;
    } catch {
      // Aún arrancando.
    }
    await sleep(200);
  }
  throw new Error('El navegador no expuso ninguna pestaña.');
}

const target = await page();
const socket = new WebSocket(target.webSocketDebuggerUrl);
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

async function evaluate(expression) {
  const result = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  return result?.result?.value;
}

const snapshot = () =>
  evaluate(`(() => {
    const select = document.getElementById('monitor');
    const canvas = document.getElementById('screen');
    return {
      label: document.getElementById('session-label').textContent,
      selectHidden: select.classList.contains('hidden'),
      options: [...select.options].map((option) => option.textContent),
      selected: select.value,
      canvas: [canvas.width, canvas.height],
      log: document.getElementById('log').textContent,
    };
  })()`);

let failures = 0;

function check(name, actual, expected) {
  const ok = JSON.stringify(actual) === JSON.stringify(expected);
  if (!ok) failures += 1;
  console.log(`${ok ? 'OK ' : 'MAL'} ${name}: ${JSON.stringify(actual)}${ok ? '' : ` (esperado ${JSON.stringify(expected)})`}`);
}

function sizeOf(label) {
  const match = /(\d+)×(\d+)/.exec(label ?? '');
  return match ? [Number(match[1]), Number(match[2])] : null;
}

await send('Page.enable');
await sleep(1500);

await evaluate(`(() => {
  document.getElementById('ip').value = '127.0.0.1';
  document.getElementById('port').value = '50000';
  document.getElementById('pin').value = '123456';
  document.getElementById('connect').click();
  return 'conectando';
})()`);

await sleep(3000);

const initial = await snapshot();
console.log('Opciones:', JSON.stringify(initial.options));

const count = initial.options.length - 1;
check('etiqueta de sesión', initial.label, `127.0.0.1 · ${count} ${count === 1 ? 'pantalla' : 'pantallas'}`);
check('selector visible', initial.selectHidden, false);
check('opción por defecto', initial.selected, '255');
check('lienzo (todas)', initial.canvas, sizeOf(initial.options[0]));
check('número de opciones', initial.options.length, count + 1);

if (initial.canvas[0] === 0) {
  console.error('La sesión no llegó a establecerse. Registro:\n' + initial.log);
  process.exit(2);
}

async function select(value) {
  await evaluate(`(() => {
    const select = document.getElementById('monitor');
    select.value = '${value}';
    select.dispatchEvent(new Event('change', { bubbles: true }));
    return select.value;
  })()`);
  await sleep(2000);
  return snapshot();
}

const first = await select('0');
check('lienzo (pantalla 1)', first.canvas, sizeOf(initial.options[1]));
check('valor (pantalla 1)', first.selected, '0');

// Captura: se intercepta la descarga para comprobar el PNG sin escribir en disco.
await evaluate(`(() => {
  window.__capture = null;
  const original = HTMLAnchorElement.prototype.click;
  HTMLAnchorElement.prototype.click = function () {
    if (this.download) {
      window.__capture = { name: this.download, href: this.href };
      return;
    }
    return original.call(this);
  };
  return 'ok';
})()`);

await evaluate(`(() => { document.getElementById('capture').click(); return 'ok'; })()`);
await sleep(1500);

const capture = await evaluate(`(async () => {
  if (!window.__capture) return null;
  const response = await fetch(window.__capture.href);
  const blob = await response.blob();
  const bitmap = await createImageBitmap(blob);
  return {
    name: window.__capture.name,
    type: blob.type,
    bytes: blob.size,
    size: [bitmap.width, bitmap.height],
  };
})()`);

console.log('Captura:', JSON.stringify(capture));
check('captura generada', capture !== null, true);
check('tipo de captura', capture?.type, 'image/png');
check('tamaño de captura', capture?.size, first.canvas);
check('nombre .png', /^captura-.*\.png$/.test(capture?.name ?? ''), true);

const afterCapture = await snapshot();
check('aviso en el registro', afterCapture.log.includes('Captura guardada'), true);

const shot = await send('Page.captureScreenshot', { format: 'png' });
mkdirSync(dirname(shotPath), { recursive: true });
writeFileSync(shotPath, Buffer.from(shot.data, 'base64'));

if (count > 1) {
  const second = await select('1');
  check('lienzo (pantalla 2)', second.canvas, sizeOf(initial.options[2]));
  check('valor (pantalla 2)', second.selected, '1');
}

const all = await select('255');
check('lienzo (todas, vuelta)', all.canvas, sizeOf(initial.options[0]));

socket.close();
browser.kill();
await sleep(400);
rmSync(profile, { recursive: true, force: true });

console.log(failures === 0 ? '\nTODO OK' : `\n${failures} comprobación(es) fallidas`);
process.exit(failures === 0 ? 0 : 1);
