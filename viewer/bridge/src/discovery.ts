import dgram from 'node:dgram';

export interface Device {
  ip: string;
  name: string;
  port: number;
  fp: string;
  os: string;
  monitors: number;
  lastSeen: number;
}

/**
 * Escucha los anuncios UDP del agente. No hay servidor central: cada agente
 * emite su presencia cada 5 s y aquí se descartan los que dejan de emitir.
 */
export class Discovery {
  private readonly devices = new Map<string, Device>();
  private readonly socket = dgram.createSocket({ type: 'udp4', reuseAddr: true });
  private readonly listeners = new Set<(devices: Device[]) => void>();
  private timer?: NodeJS.Timeout;

  start(port: number): void {
    this.socket.on('error', (error) => console.error('[discovery]', error.message));

    this.socket.on('message', (buffer, remote) => {
      try {
        const data = JSON.parse(buffer.toString('utf8')) as Record<string, unknown>;
        if (data.app !== 'soporte-lan') return;

        this.devices.set(remote.address, {
          ip: remote.address,
          name: String(data.name ?? remote.address),
          port: Number(data.port) || 50000,
          fp: String(data.fp ?? ''),
          os: String(data.os ?? ''),
          monitors: Number(data.monitors) || 1,
          lastSeen: Date.now(),
        });

        this.emit();
      } catch {
        // Paquete ajeno o malformado: se ignora.
      }
    });

    this.socket.bind(port, () => console.log(`[discovery] escuchando UDP ${port}`));
    this.timer = setInterval(() => this.expire(), 5000);
  }

  onChange(listener: (devices: Device[]) => void): () => void {
    this.listeners.add(listener);
    listener(this.list());
    return () => this.listeners.delete(listener);
  }

  list(): Device[] {
    return [...this.devices.values()].sort((a, b) => a.name.localeCompare(b.name));
  }

  private expire(): void {
    const limit = Date.now() - 15000;
    let changed = false;

    for (const [ip, device] of this.devices) {
      if (device.lastSeen < limit) {
        this.devices.delete(ip);
        changed = true;
      }
    }

    if (changed) this.emit();
  }

  private emit(): void {
    const devices = this.list();
    for (const listener of this.listeners) listener(devices);
  }

  stop(): void {
    if (this.timer) clearInterval(this.timer);
    this.socket.close();
  }
}
