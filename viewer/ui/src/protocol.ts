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
  payload: Uint8Array;
}

export function encodeMessage(type: number, payload: Uint8Array): Uint8Array {
  const frame = new Uint8Array(5 + payload.length);
  frame[0] = type;
  new DataView(frame.buffer).setUint32(1, payload.length, true);
  frame.set(payload, 5);
  return frame;
}

/** Parser incremental del framing [u8 type][u32 length][payload]. */
export class MessageReader {
  private buffer = new Uint8Array(0);

  push(chunk: Uint8Array): Message[] {
    const merged = new Uint8Array(this.buffer.length + chunk.length);
    merged.set(this.buffer);
    merged.set(chunk, this.buffer.length);
    this.buffer = merged;

    const messages: Message[] = [];
    let offset = 0;

    while (this.buffer.length - offset >= 5) {
      const view = new DataView(this.buffer.buffer, this.buffer.byteOffset + offset, 5);
      const type = this.buffer[offset];
      const length = view.getUint32(1, true);

      if (this.buffer.length - offset < 5 + length) break;

      messages.push({
        type,
        payload: this.buffer.slice(offset + 5, offset + 5 + length),
      });

      offset += 5 + length;
    }

    if (offset > 0) this.buffer = this.buffer.slice(offset);
    return messages;
  }
}
