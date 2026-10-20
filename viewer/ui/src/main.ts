import './style.css';
import { MsgType, MessageReader, encodeMessage, type Message } from './protocol';
import { toVirtualKey } from './keyboard';

interface Device {
  ip: string;
  name: string;
  port: number;
  fp: string;
  os: string;
  monitors: number;
  lastSeen: number;
}

interface RemoteMonitor {
  primary: boolean;
  x: number;
  y: number;
  width: number;
  height: number;
}

function element<T extends HTMLElement>(id: string): T {
  const node = document.getElementById(id);
  if (!node) throw new Error(`Falta el elemento #${id}`);
  return node as T;
}

const bridgeStatus = element<HTMLSpanElement>('bridge-status');
const deviceList = element<HTMLUListElement>('devices');
const ipInput = element<HTMLInputElement>('ip');
const portInput = element<HTMLInputElement>('port');
const pinInput = element<HTMLInputElement>('pin');
const connectButton = element<HTMLButtonElement>('connect');
const logElement = element<HTMLDivElement>('log');
const stage = element<HTMLElement>('stage');
const placeholder = element<HTMLDivElement>('placeholder');
const canvas = element<HTMLCanvasElement>('screen');
const cursorElement = element<HTMLDivElement>('cursor');
const toolbar = element<HTMLDivElement>('toolbar');
const sessionLabel = element<HTMLSpanElement>('session-label');
const monitorSelect = element<HTMLSelectElement>('monitor');
const disconnectButton = element<HTMLButtonElement>('disconnect');
const fullscreenButton = element<HTMLButtonElement>('fullscreen');

const context = canvas.getContext('2d');
if (!context) throw new Error('El navegador no soporta canvas 2D.');
const ctx = context;

const bridgeUrl =
  window.location.port === '5173'
    ? 'ws://127.0.0.1:7788'
    : window.location.protocol.startsWith('http')
      ? `${window.location.protocol === 'https:' ? 'wss:' : 'ws:'}//${window.location.host}`
      : 'ws://127.0.0.1:7788';

let socket: WebSocket | null = null;
let reader = new MessageReader();
let ready = false;
let selected: Device | null = null;
let currentIp = '';
let screen = {
  width: 0,
  height: 0,
  originX: 0,
  originY: 0,
  selected: 255,
  monitors: [] as RemoteMonitor[],
};
let renderChain: Promise<void> = Promise.resolve();
const pressedKeys = new Set<number>();
const imageCache = new Map<string, ImageData>();

let pendingMove: { x: number; y: number } | null = null;
let moveScheduled = false;

// ---------------------------------------------------------------------------
// Conexión con el bridge local
// ---------------------------------------------------------------------------

function connectBridge(): void {
  socket = new WebSocket(bridgeUrl);
  socket.binaryType = 'arraybuffer';

  socket.addEventListener('open', () => setBridgeStatus('en línea', 'online'));

  socket.addEventListener('close', () => {
    setBridgeStatus('sin bridge', 'offline');
    resetSession();
    window.setTimeout(connectBridge, 2000);
  });

  socket.addEventListener('error', () => setBridgeStatus('error', 'offline'));

  socket.addEventListener('message', (event) => {
    if (typeof event.data === 'string') {
      handleControl(JSON.parse(event.data) as Record<string, unknown>);
      return;
    }

    const chunk = new Uint8Array(event.data as ArrayBuffer);
    for (const message of reader.push(chunk)) handleMessage(message);
  });
}

function handleControl(message: Record<string, unknown>): void {
  switch (message.type) {
    case 'devices':
      renderDevices((message.devices as Device[]) ?? []);
      break;
    case 'status':
      handleStatus(String(message.state), message.message as string | undefined);
      break;
  }
}

function handleStatus(state: string, message?: string): void {
  switch (state) {
    case 'authenticating':
      log('Autenticando con el equipo…');
      break;

    case 'ready':
      ready = true;
      reader = new MessageReader();
      canvas.classList.remove('hidden');
      toolbar.classList.remove('hidden');
      placeholder.classList.add('hidden');
      sessionLabel.textContent = currentIp;
      log(`Sesión establecida con ${currentIp}.`, 'ok');
      canvas.focus();
      break;

    case 'error':
      log(message ?? 'Error de conexión.', 'error');
      resetSession();
      break;

    case 'closed':
      if (ready) log('La sesión terminó.');
      resetSession();
      break;
  }
}

function resetSession(): void {
  releaseKeys();
  ready = false;
  canvas.classList.add('hidden');
  toolbar.classList.add('hidden');
  cursorElement.classList.add('hidden');
  placeholder.classList.remove('hidden');
  placeholder.querySelector('h2')!.textContent = 'Sin sesión';
  ctx.clearRect(0, 0, canvas.width, canvas.height);

  screen = { width: 0, height: 0, originX: 0, originY: 0, selected: 255, monitors: [] };
  monitorSelect.replaceChildren();
  monitorSelect.classList.add('hidden');
}

function setBridgeStatus(text: string, state: 'online' | 'offline'): void {
  bridgeStatus.textContent = text;
  bridgeStatus.className = `status ${state}`;
}

// ---------------------------------------------------------------------------
// Monitores
// ---------------------------------------------------------------------------

function monitorBounds(): { width: number; height: number } {
  if (screen.monitors.length === 0) return { width: screen.width, height: screen.height };

  let left = Infinity;
  let top = Infinity;
  let right = 0;
  let bottom = 0;

  for (const monitor of screen.monitors) {
    left = Math.min(left, monitor.x);
    top = Math.min(top, monitor.y);
    right = Math.max(right, monitor.x + monitor.width);
    bottom = Math.max(bottom, monitor.y + monitor.height);
  }

  return { width: right - left, height: bottom - top };
}

function renderMonitors(): void {
  monitorSelect.replaceChildren();

  if (screen.monitors.length <= 1) {
    monitorSelect.classList.add('hidden');
    return;
  }

  const bounds = monitorBounds();

  const all = document.createElement('option');
  all.value = '255';
  all.textContent = `Todas · ${bounds.width}×${bounds.height}`;
  monitorSelect.append(all);

  screen.monitors.forEach((monitor, index) => {
    const option = document.createElement('option');
    option.value = String(index);
    option.textContent =
      `Pantalla ${index + 1} · ${monitor.width}×${monitor.height}` +
      (monitor.primary ? ' · principal' : '');
    monitorSelect.append(option);
  });

  monitorSelect.value = String(screen.selected);
  monitorSelect.classList.remove('hidden');
}

function updateSessionLabel(): void {
  const count = screen.monitors.length;
  sessionLabel.textContent =
    count > 0 ? `${currentIp} · ${count} ${count === 1 ? 'pantalla' : 'pantallas'}` : currentIp;
}

// ---------------------------------------------------------------------------
// Dispositivos
// ---------------------------------------------------------------------------

function renderDevices(devices: Device[]): void {
  deviceList.replaceChildren();

  if (devices.length === 0) {
    const empty = document.createElement('li');
    empty.className = 'empty';
    empty.textContent = 'Ninguno todavía…';
    deviceList.append(empty);
    return;
  }

  for (const device of devices) {
    const item = document.createElement('li');
    item.className = 'device';
    if (device.ip === selected?.ip) item.classList.add('active');

    const name = document.createElement('div');
    name.className = 'device-name';
    name.textContent = device.name;

    const meta = document.createElement('div');
    meta.className = 'device-meta';
    const monitors = device.monitors || 1;
    const screens = monitors > 1 ? ` · ${monitors} pantallas` : '';
    meta.textContent = `${device.ip}${screens} · ${device.os || 'Windows'}`;

    item.append(name, meta);
    item.addEventListener('click', () => selectDevice(device));
    deviceList.append(item);
  }
}

function selectDevice(device: Device): void {
  selected = device;
  ipInput.value = device.ip;
  portInput.value = String(device.port);
  pinInput.focus();
  pinInput.select();

  for (const node of deviceList.querySelectorAll('.device')) node.classList.remove('active');
  const index = [...deviceList.children].findIndex((child) =>
    child.querySelector('.device-name')?.textContent === device.name,
  );
  if (index >= 0) deviceList.children[index].classList.add('active');
}

// ---------------------------------------------------------------------------
// Mensajes binarios del agente
// ---------------------------------------------------------------------------

function handleMessage(message: Message): void {
  switch (message.type) {
    case MsgType.ScreenInfo: {
      const view = new DataView(message.payload.buffer, message.payload.byteOffset, message.payload.byteLength);
      const monitors: RemoteMonitor[] = [];
      let selected = 255;

      // A partir de la v1.1 el payload incluye la lista de monitores.
      if (message.payload.byteLength >= 12) {
        selected = view.getUint8(10);
        const count = view.getUint8(11);
        let offset = 12;

        for (let index = 0; index < count && offset + 9 <= message.payload.byteLength; index++) {
          monitors.push({
            primary: (message.payload[offset] & 1) !== 0,
            x: view.getInt16(offset + 1, true),
            y: view.getInt16(offset + 3, true),
            width: view.getUint16(offset + 5, true),
            height: view.getUint16(offset + 7, true),
          });

          offset += 9;
        }
      }

      screen = {
        width: view.getUint16(0, true),
        height: view.getUint16(2, true),
        originX: view.getInt16(6, true),
        originY: view.getInt16(8, true),
        selected,
        monitors,
      };

      canvas.width = screen.width;
      canvas.height = screen.height;
      ctx.imageSmoothingEnabled = false;
      ctx.clearRect(0, 0, canvas.width, canvas.height);
      cursorElement.classList.add('hidden');

      renderMonitors();
      updateSessionLabel();
      break;
    }

    case MsgType.Frame:
      queueFrame(message.payload);
      break;

    case MsgType.Cursor: {
      if (message.payload.length < 5) return;
      if (message.payload[4] !== 1) {
        cursorElement.classList.add('hidden');
        return;
      }

      const view = new DataView(message.payload.buffer, message.payload.byteOffset, message.payload.byteLength);
      positionCursor(view.getInt16(0, true), view.getInt16(2, true));
      break;
    }
  }
}

function queueFrame(payload: Uint8Array): void {
  const copy = payload.slice();
  renderChain = renderChain.then(() => drawFrame(copy)).catch((error) => console.error(error));
}

async function drawFrame(payload: Uint8Array): Promise<void> {
  const data = payload.buffer.slice(
    payload.byteOffset,
    payload.byteOffset + payload.byteLength,
  ) as ArrayBuffer;

  const stream = new Blob([data]).stream().pipeThrough(new DecompressionStream('deflate'));
  const buffer = new Uint8Array(await new Response(stream).arrayBuffer());
  const view = new DataView(buffer.buffer, buffer.byteOffset, buffer.byteLength);

  const count = view.getUint16(4, true);
  let offset = 6;

  for (let index = 0; index < count; index++) {
    const x = view.getUint16(offset, true);
    const y = view.getUint16(offset + 2, true);
    const width = view.getUint16(offset + 4, true);
    const height = view.getUint16(offset + 6, true);
    const flags = buffer[offset + 8];
    offset += 9;

    if (flags === 0) {
      const r = buffer[offset];
      const g = buffer[offset + 1];
      const b = buffer[offset + 2];
      offset += 3;
      ctx.fillStyle = `rgb(${r},${g},${b})`;
      ctx.fillRect(x, y, width, height);
      continue;
    }

    const image = getImageData(width, height);
    const pixels = image.data;
    let target = 0;

    for (let pixel = 0; pixel < width * height; pixel++) {
      pixels[target++] = buffer[offset++];
      pixels[target++] = buffer[offset++];
      pixels[target++] = buffer[offset++];
      pixels[target++] = 255;
    }

    ctx.putImageData(image, x, y);
  }
}

function getImageData(width: number, height: number): ImageData {
  const key = `${width}x${height}`;
  let image = imageCache.get(key);

  if (!image) {
    image = ctx.createImageData(width, height);
    imageCache.set(key, image);
  }

  return image;
}

function positionCursor(x: number, y: number): void {
  if (canvas.classList.contains('hidden') || canvas.width === 0) return;

  const canvasRect = canvas.getBoundingClientRect();
  const stageRect = stage.getBoundingClientRect();

  cursorElement.classList.remove('hidden');
  cursorElement.style.transform = `translate(${canvasRect.left - stageRect.left + x * (canvasRect.width / canvas.width)}px, ${
    canvasRect.top - stageRect.top + y * (canvasRect.height / canvas.height)
  }px)`;
}

// ---------------------------------------------------------------------------
// Entrada
// ---------------------------------------------------------------------------

function send(type: number, payload: Uint8Array): void {
  if (!ready || socket?.readyState !== WebSocket.OPEN) return;
  socket.send(encodeMessage(type, payload));
}

function toRemote(event: MouseEvent): { x: number; y: number } {
  const rect = canvas.getBoundingClientRect();
  return {
    x: Math.round((event.clientX - rect.left) * (canvas.width / rect.width) + screen.originX),
    y: Math.round((event.clientY - rect.top) * (canvas.height / rect.height) + screen.originY),
  };
}

function sendMouseMove(x: number, y: number): void {
  const payload = new Uint8Array(8);
  const view = new DataView(payload.buffer);
  view.setInt32(0, x, true);
  view.setInt32(4, y, true);
  send(MsgType.MouseMove, payload);
}

function sendMouseButton(button: number, down: boolean): void {
  send(MsgType.MouseButton, new Uint8Array([button, down ? 1 : 0]));
}

function sendWheel(delta: number): void {
  const payload = new Uint8Array(2);
  new DataView(payload.buffer).setInt16(0, delta, true);
  send(MsgType.MouseWheel, payload);
}

function sendKey(virtualKey: number, down: boolean): void {
  const payload = new Uint8Array(3);
  new DataView(payload.buffer).setUint16(0, virtualKey, true);
  payload[2] = down ? 1 : 0;
  send(MsgType.Key, payload);
}

function releaseKeys(): void {
  for (const virtualKey of pressedKeys) sendKey(virtualKey, false);
  pressedKeys.clear();
}

function mapButton(button: number): number {
  if (button === 0) return 0;
  if (button === 2) return 1;
  if (button === 1) return 2;
  return -1;
}

canvas.addEventListener('mousemove', (event) => {
  const point = toRemote(event);
  pendingMove = point;

  if (moveScheduled) return;
  moveScheduled = true;

  requestAnimationFrame(() => {
    moveScheduled = false;
    if (pendingMove) sendMouseMove(pendingMove.x, pendingMove.y);
    pendingMove = null;
  });
});

canvas.addEventListener('mousedown', (event) => {
  canvas.focus();
  const button = mapButton(event.button);
  if (button >= 0) sendMouseButton(button, true);
  event.preventDefault();
});

window.addEventListener('mouseup', (event) => {
  const button = mapButton(event.button);
  if (button >= 0) sendMouseButton(button, false);
});

canvas.addEventListener('wheel', (event) => {
  event.preventDefault();
  sendWheel(event.deltaY < 0 ? 120 : -120);
}, { passive: false });

canvas.addEventListener('contextmenu', (event) => event.preventDefault());

canvas.addEventListener('keydown', (event) => {
  const virtualKey = toVirtualKey(event.code);
  if (virtualKey === undefined) return;

  event.preventDefault();
  pressedKeys.add(virtualKey);
  sendKey(virtualKey, true);
});

canvas.addEventListener('keyup', (event) => {
  const virtualKey = toVirtualKey(event.code);
  if (virtualKey === undefined) return;

  event.preventDefault();
  pressedKeys.delete(virtualKey);
  sendKey(virtualKey, false);
});

window.addEventListener('blur', releaseKeys);

// ---------------------------------------------------------------------------
// Interfaz
// ---------------------------------------------------------------------------

function connect(): void {
  const ip = ipInput.value.trim();
  const port = Number(portInput.value) || 50000;
  const pin = pinInput.value.trim();

  if (!ip) {
    log('Escribe la IP del equipo.', 'error');
    ipInput.focus();
    return;
  }

  if (!pin) {
    log('Escribe el PIN que aparece en el equipo.', 'error');
    pinInput.focus();
    return;
  }

  currentIp = ip;
  reader = new MessageReader();
  resetSession();
  placeholder.querySelector('h2')!.textContent = 'Conectando…';

  socket?.send(JSON.stringify({
    type: 'connect',
    ip,
    port,
    pin,
    fp: selected?.ip === ip ? selected.fp : '',
  }));
}

function log(text: string, kind: 'info' | 'ok' | 'error' = 'info'): void {
  const line = document.createElement('div');
  line.className = `log-line ${kind}`;
  line.textContent = `${new Date().toLocaleTimeString()}  ${text}`;
  logElement.append(line);

  while (logElement.children.length > 200) logElement.firstElementChild?.remove();
  logElement.scrollTop = logElement.scrollHeight;
}

connectButton.addEventListener('click', connect);
pinInput.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') connect();
});

disconnectButton.addEventListener('click', () => {
  socket?.send(JSON.stringify({ type: 'disconnect' }));
  resetSession();
  log('Desconectado.');
});

fullscreenButton.addEventListener('click', () => {
  if (document.fullscreenElement) void document.exitFullscreen();
  else void stage.requestFullscreen();
});

monitorSelect.addEventListener('change', () => {
  const value = Number(monitorSelect.value);
  send(MsgType.SelectMonitor, new Uint8Array([value]));
  log(value === 255 ? 'Mostrando todas las pantallas…' : `Mostrando la pantalla ${value + 1}…`);
});

connectBridge();
log('Bridge local: ' + bridgeUrl);
