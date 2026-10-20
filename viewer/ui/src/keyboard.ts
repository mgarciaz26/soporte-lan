/**
 * Traduce KeyboardEvent.code a código virtual de Windows (VK_*). Se usa
 * `code` y no `key` para que el teclado funcione igual con cualquier layout.
 */
const keys: Record<string, number> = {
  Backspace: 0x08,
  Tab: 0x09,
  Enter: 0x0d,
  ShiftLeft: 0xa0,
  ShiftRight: 0xa1,
  ControlLeft: 0xa2,
  ControlRight: 0xa3,
  AltLeft: 0xa4,
  AltRight: 0xa5,
  Pause: 0x13,
  CapsLock: 0x14,
  Escape: 0x1b,
  Space: 0x20,
  PageUp: 0x21,
  PageDown: 0x22,
  End: 0x23,
  Home: 0x24,
  ArrowLeft: 0x25,
  ArrowUp: 0x26,
  ArrowRight: 0x27,
  ArrowDown: 0x28,
  PrintScreen: 0x2c,
  Insert: 0x2d,
  Delete: 0x2e,
  MetaLeft: 0x5b,
  MetaRight: 0x5c,
  ContextMenu: 0x5d,
  NumLock: 0x90,
  ScrollLock: 0x91,
  Semicolon: 0xba,
  Equal: 0xbb,
  Comma: 0xbc,
  Minus: 0xbd,
  Period: 0xbe,
  Slash: 0xbf,
  Backquote: 0xc0,
  BracketLeft: 0xdb,
  Backslash: 0xdc,
  BracketRight: 0xdd,
  Quote: 0xde,
  NumpadMultiply: 0x6a,
  NumpadAdd: 0x6b,
  NumpadSubtract: 0x6d,
  NumpadDecimal: 0x6e,
  NumpadDivide: 0x6f,
  NumpadEnter: 0x0d,
};

for (let index = 0; index < 26; index++) {
  keys[`Key${String.fromCharCode(65 + index)}`] = 0x41 + index;
}

for (let index = 0; index < 10; index++) {
  keys[`Digit${index}`] = 0x30 + index;
  keys[`Numpad${index}`] = 0x60 + index;
}

for (let index = 1; index <= 12; index++) {
  keys[`F${index}`] = 0x6f + index;
}

export function toVirtualKey(code: string): number | undefined {
  return keys[code];
}
