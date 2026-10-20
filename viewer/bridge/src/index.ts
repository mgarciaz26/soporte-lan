import tls from 'node:tls';
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http';
import { appendFileSync, createReadStream, existsSync, mkdirSync, statSync } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { spawn } from 'node:child_process';
import { WebSocketServer, WebSocket, type RawData } from 'ws';
import { Discovery, type Device } from './discovery.js';
import { MsgType, MessageReader, encodeMessage } from './protocol.js';

const bridgePort = Number(process.env.BRIDGE_PORT ?? 7788);
const discoveryPort = Number(process.env.DISCOVERY_PORT ?? 47820);
const shouldOpenBrowser = process.argv.includes('--open');

const logPath = join(
  process.env.LOCALAPPDATA ?? process.env.HOME ?? '.',
  'SoporteLAN',
  'viewer.log',
);

function log(message: string): void {
  const line = `[${new Date().toISOString()}] ${message}`;
  console.log(line);

  try {
    mkdirSync(dirname(logPath), { recursive: true });
    appendFileSync(logPath, line + '\n');
  } catch {
    // El registro es opcional: nunca debe tumbar el visor.
  }
}

function findUiRoot(): string | null {
  const here = process.argv[1] ? dirname(resolve(process.argv[1])) : process.cwd();

  const candidates = [
    process.env.UI_DIR,
    join(here, 'ui'),
    join(here, '..', 'ui', 'dist'),
    join(here, '..', '..', 'ui', 'dist'),
  ].filter((value): value is string => Boolean(value));

  for (const candidate of candidates) {
    if (existsSync(join(candidate, 'index.html'))) return resolve(candidate);
  }

  return null;
}

const uiRoot = findUiRoot();
const discovery = new Discovery();
discovery.start(discoveryPort);

const httpServer = createServer(serveStatic);
const server = new WebSocketServer({ server: httpServer });

const mimeTypes: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.ico': 'image/x-icon',
  '.woff2': 'font/woff2',
  '.woff': 'font/woff',
  '.map': 'application/json; charset=utf-8',
};

function serveStatic(request: IncomingMessage, response: ServerResponse): void {
  if (!uiRoot) {
    response.writeHead(500, { 'content-type': 'text/plain; charset=utf-8' });
    response.end('No se encontró la interfaz web (carpeta ui).');
    return;
  }

  const pathname = decodeURIComponent((request.url ?? '/').split('?')[0] ?? '/');
  let relative = pathname === '/' ? 'index.html' : pathname.replace(/^\/+/, '');

  if (relative.includes('..')) {
    response.writeHead(403, { 'content-type': 'text/plain; charset=utf-8' });
    response.end('Prohibido.');
    return;
  }

  let fullPath = join(uiRoot, relative);

  // Aplicación de una sola página: cualquier ruta desconocida sirve index.html.
  if (!existsSync(fullPath) || !statSync(fullPath).isFile()) {
    relative = 'index.html';
    fullPath = join(uiRoot, relative);
  }

  response.writeHead(200, {
    'content-type': mimeTypes[extname(fullPath).toLowerCase()] ?? 'application/octet-stream',
    'cache-control': relative === 'index.html' ? 'no-cache' : 'public, max-age=3600',
  });

  createReadStream(fullPath)
    .on('error', () => response.end())
    .pipe(response);
}

function openBrowser(): void {
  const url = `http://127.0.0.1:${bridgePort}`;

  try {
    if (process.platform === 'win32') {
      spawn('cmd', ['/c', 'start', '', url], {
        detached: true,
        stdio: 'ignore',
        windowsHide: true,
      }).unref();
    } else {
      spawn(process.platform === 'darwin' ? 'open' : 'xdg-open', [url], {
        detached: true,
        stdio: 'ignore',
      }).unref();
    }
  } catch (error) {
    log(`No se pudo abrir el navegador: ${(error as Error).message}`);
  }
}

httpServer.on('error', (error: NodeJS.ErrnoException) => {
  if (error.code === 'EADDRINUSE') {
    log(`El visor ya estaba en marcha en el puerto ${bridgePort}.`);
    if (shouldOpenBrowser) openBrowser();
    process.exit(0);
  }

  log(`Error del servidor: ${error.message}`);
  process.exit(1);
});

httpServer.listen(bridgePort, '127.0.0.1', () => {
  log(`Visor disponible en http://127.0.0.1:${bridgePort}`);
  log(`Descubrimiento UDP en el puerto ${discoveryPort}`);

  if (!uiRoot) log('Aviso: no se encontró la carpeta ui; solo funciona el WebSocket.');
  if (shouldOpenBrowser) openBrowser();
});

interface ConnectRequest {
  type: 'connect';
  ip: string;
  port: number;
  pin: string;
  fp?: string;
}

interface ClientMessage {
  type?: 'connect' | 'disconnect';
  ip?: string;
  port?: number;
  pin?: string;
  fp?: string;
}

const idleLimit = Number(process.env.BRIDGE_IDLE_SECONDS ?? 120) * 1000;
let clients = 0;
let idleTimer: NodeJS.Timeout | null = null;

server.on('connection', (ws) => {
  clients += 1;
  if (idleTimer) {
    clearTimeout(idleTimer);
    idleTimer = null;
  }

  log('Visor conectado.');

  const send = (payload: unknown) => {
    if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(payload));
  };

  const sendBinary = (payload: Buffer) => {
    if (ws.readyState === WebSocket.OPEN) ws.send(payload);
  };

  const unsubscribe = discovery.onChange((devices: Device[]) =>
    send({ type: 'devices', devices }),
  );

  let socket: tls.TLSSocket | null = null;
  let reader = new MessageReader();
  let authenticated = false;

  const status = (state: string, message?: string) =>
    send({ type: 'status', state, message });

  const fail = (message: string) => {
    socket?.destroy();
    socket = null;
    authenticated = false;
    status('error', message);
  };

  const startSession = (request: ConnectRequest) => {
    socket?.destroy();
    reader = new MessageReader();
    authenticated = false;

    const host = String(request.ip ?? '').trim();
    const port = Number(request.port) || 50000;

    if (!host) {
      fail('Falta la IP del equipo.');
      return;
    }

    const current = tls.connect({ host, port, rejectUnauthorized: false });
    socket = current;
    current.setNoDelay(true);

    current.once('secureConnect', () => {
      const certificate = current.getPeerCertificate();
      const actual = normalise(certificate.fingerprint256 ?? '');
      const expected = normalise(request.fp ?? '');

      if (expected && actual !== expected) {
        fail('La huella del certificado no coincide con la anunciada por el equipo.');
        return;
      }

      current.write(encodeMessage(MsgType.AuthRequest, Buffer.from(String(request.pin ?? ''), 'utf8')));
      status('authenticating');
    });

    current.on('data', (chunk: Buffer) => {
      if (authenticated) {
        sendBinary(chunk);
        return;
      }

      let messages;
      try {
        messages = reader.push(chunk);
      } catch (error) {
        fail(error instanceof Error ? error.message : 'Error de protocolo.');
        return;
      }

      for (let index = 0; index < messages.length; index++) {
        const message = messages[index];
        if (message.type !== MsgType.AuthResult) continue;

        if (message.payload[0] !== 1) {
          fail(message.payload.subarray(1).toString('utf8') || 'Autenticación rechazada.');
          return;
        }

        authenticated = true;

        // Lo que venga detrás del AUTH_RESULT (SCREEN_INFO) va tal cual al visor.
        for (let next = index + 1; next < messages.length; next++) {
          sendBinary(encodeMessage(messages[next].type, messages[next].payload));
        }

        const rest = reader.takeRemaining();
        if (rest.length > 0) sendBinary(rest);

        status('ready');
        return;
      }
    });

    current.on('error', (error) => fail(error.message));

    current.on('close', () => {
      authenticated = false;
      status('closed');
    });
  };

  ws.on('message', (data: RawData, isBinary: boolean) => {
    if (isBinary) {
      if (socket && authenticated) socket.write(toBuffer(data));
      return;
    }

    let message: ClientMessage;
    try {
      message = JSON.parse(data.toString()) as ClientMessage;
    } catch {
      return;
    }

    switch (message.type) {
      case 'connect':
        startSession(message as ConnectRequest);
        break;
      case 'disconnect':
        socket?.destroy();
        socket = null;
        authenticated = false;
        status('closed');
        break;
    }
  });

  ws.on('close', () => {
    unsubscribe();
    socket?.destroy();
    socket = null;
    log('Visor desconectado.');

    clients -= 1;
    if (clients === 0) {
      idleTimer = setTimeout(() => {
        log('No queda ningún visor abierto; cerrando el bridge.');
        shutdown();
      }, idleLimit);
    }
  });

  ws.on('error', (error) => log(`WebSocket: ${error.message}`));
});

function normalise(fingerprint: string): string {
  return fingerprint.replaceAll(':', '').toUpperCase();
}

function toBuffer(data: RawData): Buffer {
  if (Buffer.isBuffer(data)) return data;
  if (Array.isArray(data)) return Buffer.concat(data);
  return Buffer.from(data);
}

function shutdown(): void {
  discovery.stop();
  server.close();
  httpServer.close();
  process.exit(0);
}

process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
