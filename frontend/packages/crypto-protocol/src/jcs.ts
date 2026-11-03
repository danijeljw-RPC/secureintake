/**
 * SI-JCS-1: the Secure Intake profile of RFC 8785 (docs/specs/SI-JCS-1-canonical-json.md, ADR-0003).
 * Independent of the .NET implementation; both must pass docs/specs/vectors/si-jcs-1.json.
 *
 * JSON.parse can't be used: it silently keeps the last duplicate key and converts numbers to doubles,
 * losing the original token. This is a strict parser that keeps number tokens verbatim.
 */

export const MAX_DEPTH = 64;
const MAX_SAFE = 9007199254740991n;

type Value = null | boolean | string | NumberToken | Value[] | Map<string, Value>;

class NumberToken {
  readonly token: string;
  constructor(token: string) {
    this.token = token;
  }
}

export class JcsError extends Error {}

/** Canonicalises UTF-8 JSON bytes. Throws JcsError for anything outside the profile. */
export function canonicalize(input: Uint8Array): Uint8Array {
  let text: string;
  try {
    // fatal: reject invalid and overlong UTF-8. ignoreBOM: keep a BOM so the parser rejects it.
    text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(input);
  } catch {
    throw new JcsError('invalid UTF-8');
  }
  const p = new Parser(text);
  p.ws();
  const value = p.value(0);
  p.ws();
  if (p.i !== text.length) throw new JcsError('trailing content');
  return new TextEncoder().encode(serialize(value));
}

class Parser {
  i = 0;
  readonly s: string;
  constructor(s: string) {
    this.s = s;
  }

  ws(): void {
    while (this.i < this.s.length && ' \t\n\r'.includes(this.s[this.i])) this.i++;
  }

  value(depth: number): Value {
    const c = this.s[this.i];
    if (c === '{' || c === '[') {
      if (depth + 1 > MAX_DEPTH) throw new JcsError('nesting too deep');
      return c === '{' ? this.object(depth + 1) : this.array(depth + 1);
    }
    if (c === '"') return this.string();
    if (c === '-' || (c >= '0' && c <= '9')) return this.number();
    for (const [lit, v] of [['true', true], ['false', false], ['null', null]] as const) {
      if (this.s.startsWith(lit, this.i)) {
        this.i += lit.length;
        return v;
      }
    }
    throw new JcsError(`unexpected input at ${this.i}`);
  }

  object(depth: number): Map<string, Value> {
    const m = new Map<string, Value>();
    this.i++;
    this.ws();
    if (this.s[this.i] === '}') {
      this.i++;
      return m;
    }
    for (;;) {
      this.ws();
      if (this.s[this.i] !== '"') throw new JcsError('expected property name');
      const key = this.string();
      if (m.has(key)) throw new JcsError(`duplicate property ${JSON.stringify(key)}`);
      this.ws();
      if (this.s[this.i++] !== ':') throw new JcsError('expected colon');
      this.ws();
      m.set(key, this.value(depth));
      this.ws();
      const c = this.s[this.i++];
      if (c === '}') return m;
      if (c !== ',') throw new JcsError('expected comma or }');
    }
  }

  array(depth: number): Value[] {
    const a: Value[] = [];
    this.i++;
    this.ws();
    if (this.s[this.i] === ']') {
      this.i++;
      return a;
    }
    for (;;) {
      this.ws();
      a.push(this.value(depth));
      this.ws();
      const c = this.s[this.i++];
      if (c === ']') return a;
      if (c !== ',') throw new JcsError('expected comma or ]');
    }
  }

  string(): string {
    this.i++;
    let out = '';
    for (;;) {
      if (this.i >= this.s.length) throw new JcsError('unterminated string');
      const c = this.s[this.i++];
      if (c === '"') break;
      if (c.charCodeAt(0) < 0x20) throw new JcsError('raw control character in string');
      if (c !== '\\') {
        out += c;
        continue;
      }
      const e = this.s[this.i++];
      const simple: Record<string, string> = { '"': '"', '\\': '\\', '/': '/', b: '\b', f: '\f', n: '\n', r: '\r', t: '\t' };
      if (e in simple) out += simple[e];
      else if (e === 'u') {
        const hex = this.s.slice(this.i, this.i + 4);
        if (!/^[0-9a-fA-F]{4}$/.test(hex)) throw new JcsError('bad \\u escape');
        out += String.fromCharCode(parseInt(hex, 16));
        this.i += 4;
      } else throw new JcsError('bad escape');
    }
    if (!out.isWellFormed()) throw new JcsError('lone surrogate');
    return out;
  }

  number(): NumberToken {
    const m = /^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?/.exec(this.s.slice(this.i));
    if (!m) throw new JcsError('bad number');
    this.i += m[0].length;
    if (m[2] !== undefined || m[3] !== undefined) throw new JcsError('fraction or exponent not allowed');
    if (m[0] === '-0') throw new JcsError('negative zero not allowed');
    const n = BigInt(m[0]);
    if (n > MAX_SAFE || n < -MAX_SAFE) throw new JcsError('integer outside ±(2^53 − 1)');
    return new NumberToken(m[0]);
  }
}

function serialize(v: Value): string {
  if (v === null) return 'null';
  if (v === true) return 'true';
  if (v === false) return 'false';
  if (typeof v === 'string') return quote(v);
  if (v instanceof NumberToken) return v.token;
  if (Array.isArray(v)) return '[' + v.map(serialize).join(',') + ']';
  // Default sort compares UTF-16 code units, exactly as RFC 8785 §3.2.3 requires.
  const keys = [...v.keys()].sort();
  return '{' + keys.map((k) => quote(k) + ':' + serialize(v.get(k)!)).join(',') + '}';
}

function quote(s: string): string {
  let out = '"';
  for (const ch of s) {
    const c = ch.codePointAt(0)!;
    if (ch === '"') out += '\\"';
    else if (ch === '\\') out += '\\\\';
    else if (ch === '\b') out += '\\b';
    else if (ch === '\f') out += '\\f';
    else if (ch === '\n') out += '\\n';
    else if (ch === '\r') out += '\\r';
    else if (ch === '\t') out += '\\t';
    else if (c < 0x20) out += '\\u' + c.toString(16).padStart(4, '0');
    else out += ch;
  }
  return out + '"';
}
