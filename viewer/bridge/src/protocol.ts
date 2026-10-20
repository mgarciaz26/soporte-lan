export const MsgType = {
  AuthRequest: 0x01,
  AuthResult: 0x02,
  ScreenInfo: 0x03,
  Frame: 0x04,
  Cursor: 0x05,
  MouseMove: 0x10,
  MouseButton: 0x11,
  MouseWheel: 0x12,
  Key: 0x13,
  Clipboard: 0x14,
  Ping: 0x20,
  Pong: 0x21,
} as const;

export interface Message {
  type: number;
  payload: Buffer;
}

export function encodeMessage(type: number, payload: Buffer): Buffer {
  const frame = Buffer.allocUnsafe(5 + payload.length);
  frame[0] = type;
  frame.writeUInt32LE(payload.length, 1);
  payload.copy(frame, 5);
  return frame;
}

/** Parser incremental del framing [u8 type][u32 length][payload]. */
export class MessageReader {
  private buffer: Buffer = Buffer.alloc(0);

  push(chunk: Buffer): Message[] {
    this.buffer = this.buffer.length === 0 ? chunk : Buffer.concat([this.buffer, chunk]);
    const messages: Message[] = [];

    while (this.buffer.length >= 5) {
      const type = this.buffer[0];
      const length = this.buffer.readUInt32LE(1);
      if (length > 32 * 1024 * 1024) throw new Error('Payload fuera de rango.');
      if (this.buffer.length < 5 + length) break;

      messages.push({ type, payload: Buffer.from(this.buffer.subarray(5, 5 + length)) });
      this.buffer = this.buffer.subarray(5 + length);
    }

    return messages;
  }

  takeRemaining(): Buffer {
    const rest = this.buffer;
    this.buffer = Buffer.alloc(0);
    return rest;
  }
}
