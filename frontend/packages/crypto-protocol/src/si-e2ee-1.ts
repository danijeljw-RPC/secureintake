/**
 * SI-E2EE-1 (docs/specs/S1-si-e2ee-1.md). PROVISIONAL, pending external review.
 * WebCrypto only, so the same module runs in browsers and in Node. ML-KEM-768 decapsulation is not here yet:
 * the portal will use @noble/post-quantum (doc 26 §3) in M2.
 */

const subtle = globalThis.crypto.subtle;
const ascii = (s: string): Uint8Array => new TextEncoder().encode(s);

export const SUITE_CODE = 0x0001;
export const CHUNK_SIZE = 4 * 1024 * 1024;
export const TAG_SIZE = 16;
const LABEL = {
  kem: ascii('SI-E2EE-1 kem'),
  wrap: ascii('SI-E2EE-1 wrap'),
  content: ascii('SI-E2EE-1 content'),
  nonce: ascii('SI-E2EE-1 nonce'),
  commit: ascii('SI-E2EE-1 commit'),
  manifest: ascii('SI-E2EE-1 manifest'),
  fingerprint: ascii('SI-E2EE-1 recipient key'),
  magic: ascii('SIE1'),
};

export class CryptoError extends Error {}

export const hex = (b: Uint8Array): string => [...b].map((x) => x.toString(16).padStart(2, '0')).join('');
export const unhex = (s: string): Uint8Array => Uint8Array.from(s.match(/../g) ?? [], (h) => parseInt(h, 16));

function concat(...parts: Uint8Array[]): Uint8Array {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let o = 0;
  for (const p of parts) {
    out.set(p, o);
    o += p.length;
  }
  return out;
}

function need(b: Uint8Array, length: number, what: string): void {
  if (b.length !== length) throw new CryptoError(`${what} must be ${length} bytes`);
}

const u16 = (n: number) => { const b = new Uint8Array(2); new DataView(b.buffer).setUint16(0, n); return b; };
const u32 = (n: number) => { const b = new Uint8Array(4); new DataView(b.buffer).setUint32(0, n); return b; };
const u64 = (n: bigint) => { const b = new Uint8Array(8); new DataView(b.buffer).setBigUint64(0, n); return b; };

export async function sha256(...parts: Uint8Array[]): Promise<Uint8Array> {
  return new Uint8Array(await subtle.digest('SHA-256', concat(...parts)));
}

export async function hkdf(ikm: Uint8Array, salt: Uint8Array, info: Uint8Array, length: number): Promise<Uint8Array> {
  const key = await subtle.importKey('raw', ikm, 'HKDF', false, ['deriveBits']);
  return new Uint8Array(await subtle.deriveBits({ name: 'HKDF', hash: 'SHA-256', salt, info }, key, length * 8));
}

// ── File header and manifest digest (S1 §6) ───────────────────────────────

export interface FileHeader {
  encoded: Uint8Array;
  manifestDigest: Uint8Array;
  fileId: Uint8Array;
  plaintextLength: bigint;
  chunkCount: number;
}

export async function fileHeader(fileId: Uint8Array, plaintextLength: bigint): Promise<FileHeader> {
  need(fileId, 16, 'fileId');
  const count = plaintextLength === 0n ? 1n : (plaintextLength - 1n) / BigInt(CHUNK_SIZE) + 1n;
  if (count > 0xffffffffn) throw new CryptoError('chunk-count limit exceeded');
  const encoded = concat(LABEL.magic, u16(SUITE_CODE), fileId, u32(CHUNK_SIZE), u64(plaintextLength), u32(Number(count)));
  const manifestDigest = await sha256(LABEL.manifest, new Uint8Array([0]), encoded);
  return { encoded, manifestDigest, fileId, plaintextLength, chunkCount: Number(count) };
}

export function chunkPlaintextLength(h: FileHeader, index: number): number {
  if (index < 0 || index >= h.chunkCount) throw new CryptoError('chunk index beyond chunk count');
  return index < h.chunkCount - 1 ? CHUNK_SIZE : Number(h.plaintextLength - BigInt(h.chunkCount - 1) * BigInt(CHUNK_SIZE));
}

// ── Content encryption (S1 §7) ────────────────────────────────────────────

export interface KeySchedule {
  contentKey: Uint8Array;
  nonceBase: Uint8Array;
  commitKey: Uint8Array;
}

export async function keySchedule(dek: Uint8Array, fileId: Uint8Array): Promise<KeySchedule> {
  need(dek, 32, 'DEK');
  return {
    contentKey: await hkdf(dek, fileId, LABEL.content, 32),
    nonceBase: await hkdf(dek, fileId, LABEL.nonce, 12),
    commitKey: await hkdf(dek, fileId, LABEL.commit, 32),
  };
}

export function nonceAndAad(ks: KeySchedule, h: FileHeader, index: number): { nonce: Uint8Array; aad: Uint8Array } {
  chunkPlaintextLength(h, index);
  const final = index === h.chunkCount - 1 ? 1 : 0;
  const nonce = ks.nonceBase.slice();
  const mask = concat(u32(index), new Uint8Array([final]));
  for (let i = 0; i < mask.length; i++) nonce[i] ^= mask[i];
  return { nonce, aad: concat(h.fileId, u32(index), new Uint8Array([final]), h.manifestDigest) };
}

export async function encryptChunk(ks: KeySchedule, h: FileHeader, index: number, plaintext: Uint8Array): Promise<Uint8Array> {
  need(plaintext, chunkPlaintextLength(h, index), `chunk ${index} plaintext`);
  const { nonce, aad } = nonceAndAad(ks, h, index);
  const key = await subtle.importKey('raw', ks.contentKey, 'AES-GCM', false, ['encrypt']);
  return new Uint8Array(await subtle.encrypt({ name: 'AES-GCM', iv: nonce, additionalData: aad, tagLength: 128 }, key, plaintext));
}

export async function decryptChunk(ks: KeySchedule, h: FileHeader, index: number, ciphertext: Uint8Array): Promise<Uint8Array> {
  need(ciphertext, chunkPlaintextLength(h, index) + TAG_SIZE, `chunk ${index} ciphertext`);
  const { nonce, aad } = nonceAndAad(ks, h, index);
  const key = await subtle.importKey('raw', ks.contentKey, 'AES-GCM', false, ['decrypt']);
  try {
    return new Uint8Array(await subtle.decrypt({ name: 'AES-GCM', iv: nonce, additionalData: aad, tagLength: 128 }, key, ciphertext));
  } catch {
    throw new CryptoError('chunk authentication failed');
  }
}

/** Local-only resume commitment (S1 §7.4). Never sent to the server. */
export async function resumeCommitment(ks: KeySchedule, index: number, plaintext: Uint8Array): Promise<Uint8Array> {
  const key = await subtle.importKey('raw', ks.commitKey, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return new Uint8Array(await subtle.sign('HMAC', key, concat(u32(index), plaintext)));
}

// ── X25519 and the hybrid combiner (S1 §5) ────────────────────────────────

const PKCS8_X25519 = unhex('302e020100300506032b656e04220420');
const SPKI_X25519 = unhex('302a300506032b656e032100');

async function x25519Private(sk: Uint8Array): Promise<CryptoKey> {
  need(sk, 32, 'X25519 private key');
  return subtle.importKey('pkcs8', concat(PKCS8_X25519, sk), { name: 'X25519' }, true, ['deriveBits']);
}

export async function x25519PublicKey(sk: Uint8Array): Promise<Uint8Array> {
  const jwk = await subtle.exportKey('jwk', await x25519Private(sk));
  return Uint8Array.from(atob(jwk.x!.replace(/-/g, '+').replace(/_/g, '/')), (c) => c.charCodeAt(0));
}

/** Rejects an all-zero result explicitly, whatever the platform does. */
export async function x25519(sk: Uint8Array, peer: Uint8Array): Promise<Uint8Array> {
  need(peer, 32, 'X25519 public key');
  let ss: Uint8Array;
  try {
    const pub = await subtle.importKey('spki', concat(SPKI_X25519, peer), { name: 'X25519' }, false, []);
    ss = new Uint8Array(await subtle.deriveBits({ name: 'X25519', public: pub }, await x25519Private(sk), 256));
  } catch {
    throw new CryptoError('X25519 rejected the peer public key');
  }
  if (ss.every((b) => b === 0)) throw new CryptoError('X25519 rejected the peer public key');
  return ss;
}

export async function combine(ssMlKem: Uint8Array, ssX25519: Uint8Array, ctX25519: Uint8Array, pkX25519: Uint8Array): Promise<Uint8Array> {
  for (const [b, n] of [[ssMlKem, 'ssMlKem'], [ssX25519, 'ssX25519'], [ctX25519, 'ctX25519'], [pkX25519, 'pkX25519']] as const) need(b, 32, n);
  return hkdf(concat(ssMlKem, ssX25519, ctX25519, pkX25519), new Uint8Array(0), LABEL.kem, 32);
}

export async function fingerprint(x25519Pk: Uint8Array, mlKemEk: Uint8Array): Promise<Uint8Array> {
  need(x25519Pk, 32, 'X25519 public key');
  need(mlKemEk, 1184, 'ML-KEM-768 encapsulation key');
  return sha256(LABEL.fingerprint, new Uint8Array([0]), u16(SUITE_CODE), x25519Pk, mlKemEk);
}

// ── DEK wrap (S1 §8) ──────────────────────────────────────────────────────

export function wrapAad(fileId: Uint8Array, recipientKeyId: Uint8Array, recipientFingerprint: Uint8Array): Uint8Array {
  need(fileId, 16, 'fileId');
  need(recipientKeyId, 16, 'recipientKeyId');
  need(recipientFingerprint, 32, 'recipientFingerprint');
  return concat(u16(SUITE_CODE), fileId, recipientKeyId, recipientFingerprint);
}

export const deriveKek = (ss: Uint8Array, fileId: Uint8Array): Promise<Uint8Array> => hkdf(ss, fileId, LABEL.wrap, 32);

export async function wrap(kek: Uint8Array, nonce: Uint8Array, dek: Uint8Array, aad: Uint8Array): Promise<Uint8Array> {
  need(nonce, 12, 'wrap nonce');
  need(dek, 32, 'DEK');
  const key = await subtle.importKey('raw', kek, 'AES-GCM', false, ['encrypt']);
  return concat(nonce, new Uint8Array(await subtle.encrypt({ name: 'AES-GCM', iv: nonce, additionalData: aad, tagLength: 128 }, key, dek)));
}

export async function unwrap(kek: Uint8Array, wrapped: Uint8Array, aad: Uint8Array): Promise<Uint8Array> {
  need(wrapped, 60, 'wrapped DEK');
  const key = await subtle.importKey('raw', kek, 'AES-GCM', false, ['decrypt']);
  try {
    return new Uint8Array(await subtle.decrypt({ name: 'AES-GCM', iv: wrapped.slice(0, 12), additionalData: aad, tagLength: 128 }, key, wrapped.slice(12)));
  } catch {
    throw new CryptoError('DEK unwrap failed');
  }
}
