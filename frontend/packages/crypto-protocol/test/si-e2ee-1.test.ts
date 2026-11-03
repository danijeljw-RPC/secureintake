import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as s1 from '../src/si-e2ee-1.ts';

const V = JSON.parse(readFileSync(new URL('../../../../docs/specs/vectors/si-e2ee-1.json', import.meta.url), 'utf8'));
const { unhex, hex } = s1;
const pattern = (n: number) => Uint8Array.from({ length: n }, (_, k) => k % 251);

test('X25519 RFC 7748', async () => {
  for (const v of V.x25519) {
    assert.equal(hex(await s1.x25519PublicKey(unhex(v.aliceSk))), v.alicePk);
    assert.equal(hex(await s1.x25519(unhex(v.aliceSk), unhex(v.bobPk))), v.shared);
    assert.equal(hex(await s1.x25519(unhex(v.bobSk), unhex(v.alicePk))), v.shared);
  }
});

test('X25519 rejects keys that give an all-zero secret', async () => {
  const sk = unhex(V.x25519[0].aliceSk);
  for (const pk of V.x25519RejectedPublicKeys) {
    await assert.rejects(s1.x25519(sk, unhex(pk)), s1.CryptoError);
  }
});

test('combiner', async () => {
  for (const v of V.combiner) {
    assert.equal(hex(await s1.combine(unhex(v.ssMlKem), unhex(v.ssX25519), unhex(v.ctX25519), unhex(v.pkX25519))), v.sharedSecret);
  }
});

test('fingerprint', async () => {
  for (const v of V.fingerprint) assert.equal(hex(await s1.fingerprint(unhex(v.x25519Pk), unhex(v.mlKemEk))), v.fingerprint);
});

test('key wrap', async () => {
  for (const v of V.keyWrap) {
    const kek = await s1.deriveKek(unhex(v.kemSharedSecret), unhex(v.fileId));
    assert.equal(hex(kek), v.kek);
    const aad = s1.wrapAad(unhex(v.fileId), unhex(v.recipientKeyId), unhex(v.recipientFingerprint));
    assert.equal(hex(aad), v.aad);
    assert.equal(hex(await s1.wrap(kek, unhex(v.nonce), unhex(v.dek), aad)), v.wrapped);
    assert.equal(hex(await s1.unwrap(kek, unhex(v.wrapped), aad)), v.dek);
  }
});

test('files: header, digest, key schedule, nonces, AAD, ciphertext, commitments', async () => {
  for (const v of V.files) {
    const h = await s1.fileHeader(unhex(v.fileId), BigInt(v.plaintextLength));
    assert.equal(hex(h.encoded), v.header);
    assert.equal(hex(h.manifestDigest), v.manifestDigest);
    const ks = await s1.keySchedule(unhex(v.dek), unhex(v.fileId));
    assert.equal(hex(ks.contentKey), v.contentKey);
    assert.equal(hex(ks.nonceBase), v.nonceBase);
    assert.equal(hex(ks.commitKey), v.commitKey);
    const plain = pattern(v.plaintextLength);
    assert.equal(v.chunks.length, h.chunkCount);
    for (const c of v.chunks) {
      const { nonce, aad } = s1.nonceAndAad(ks, h, c.index);
      assert.equal(hex(nonce), c.nonce);
      assert.equal(hex(aad), c.aad);
      const chunk = plain.subarray(c.index * s1.CHUNK_SIZE, c.index * s1.CHUNK_SIZE + c.plaintextLength);
      const ct = await s1.encryptChunk(ks, h, c.index, chunk);
      assert.equal(hex(await s1.sha256(ct)), c.ciphertextSha256);
      if (c.ciphertext) assert.equal(hex(ct), c.ciphertext);
      assert.equal(hex(await s1.resumeCommitment(ks, c.index, chunk)), c.resumeCommitment);
      assert.deepEqual(await s1.decryptChunk(ks, h, c.index, ct), chunk);
    }
  }
});

test('hybrid envelope: X25519 half, combiner and unwrap (ML-KEM decapsulation arrives in M2)', async () => {
  const v = V.hybridEnvelope;
  assert.ok(v, 'vector file has no hybrid section');
  const ct = unhex(v.kemCiphertext);
  assert.equal(hex(await s1.x25519PublicKey(unhex(v.x25519Sk))), v.x25519Pk);
  assert.equal(hex(await s1.x25519PublicKey(unhex(v.ephemeralX25519Sk))), hex(ct.subarray(0, 32)));
  assert.equal(hex(await s1.fingerprint(unhex(v.x25519Pk), unhex(v.mlKemEk))), v.fingerprint);
  const ssX = await s1.x25519(unhex(v.x25519Sk), ct.subarray(0, 32));
  const ss = await s1.combine(unhex(v.ssMlKem), ssX, ct.subarray(0, 32), unhex(v.x25519Pk));
  assert.equal(hex(ss), v.sharedSecret);
  const aad = s1.wrapAad(unhex(v.fileId), unhex(v.recipientKeyId), unhex(v.fingerprint));
  assert.equal(hex(await s1.unwrap(await s1.deriveKek(ss, unhex(v.fileId)), unhex(v.wrappedDek), aad)), v.dek);
});

// ── Negative tests (S1 §11) ────────────────────────────────────────────────

const file = V.files.find((f: { plaintextLength: number }) => f.plaintextLength === 33);

async function setup(length = 33) {
  const h = await s1.fileHeader(unhex(file.fileId), BigInt(length));
  const ks = await s1.keySchedule(unhex(file.dek), unhex(file.fileId));
  return { h, ks };
}

test('negative: modified ciphertext or tag', async () => {
  const { h, ks } = await setup();
  const ct = unhex(file.chunks[0].ciphertext);
  for (const pos of [0, 16, ct.length - 1]) {
    const t = ct.slice();
    t[pos] ^= 1;
    await assert.rejects(s1.decryptChunk(ks, h, 0, t), s1.CryptoError);
  }
});

test('negative: wrong DEK', async () => {
  const { h } = await setup();
  const ks = await s1.keySchedule(new Uint8Array(32).fill(9), unhex(file.fileId));
  await assert.rejects(s1.decryptChunk(ks, h, 0, unhex(file.chunks[0].ciphertext)), s1.CryptoError);
});

test('negative: altered AAD via another file ID or another declared length', async () => {
  const { ks } = await setup();
  const otherFile = await s1.fileHeader(new Uint8Array(16).fill(7), 33n);
  await assert.rejects(s1.decryptChunk(ks, otherFile, 0, unhex(file.chunks[0].ciphertext)), s1.CryptoError);
  const twoChunk = V.files.find((f: { plaintextLength: number }) => f.plaintextLength === s1.CHUNK_SIZE + 5);
  const h2 = await s1.fileHeader(unhex(twoChunk.fileId), BigInt(twoChunk.plaintextLength));
  const ks2 = await s1.keySchedule(unhex(twoChunk.dek), unhex(twoChunk.fileId));
  const finalCt = unhex(twoChunk.chunks[1].ciphertext);
  // Final chunk presented under a header where it would be non-final / at another index.
  const h3 = await s1.fileHeader(unhex(twoChunk.fileId), BigInt(2 * s1.CHUNK_SIZE + 5));
  await assert.rejects(s1.decryptChunk(ks2, h3, 1, finalCt), s1.CryptoError);
  await assert.rejects(s1.decryptChunk(ks2, h2, 0, finalCt), s1.CryptoError);
});

test('negative: final-chunk handling and bounds', async () => {
  const { h, ks } = await setup();
  assert.throws(() => s1.nonceAndAad(ks, h, 1), s1.CryptoError);
  await assert.rejects(s1.encryptChunk(ks, h, 0, new Uint8Array(32)), s1.CryptoError);
  await assert.rejects(s1.fileHeader(new Uint8Array(16), 0xffffffffn * BigInt(s1.CHUNK_SIZE) + 1n), s1.CryptoError);
});

test('negative: wrap AAD fields, wrong KEK, modified wrap', async () => {
  const v = V.keyWrap[0];
  const kek = await s1.deriveKek(unhex(v.kemSharedSecret), unhex(v.fileId));
  const fid = unhex(v.fileId), kid = unhex(v.recipientKeyId), fp = unhex(v.recipientFingerprint);
  const wrapped = unhex(v.wrapped);
  const flip = (b: Uint8Array, i: number) => { const c = b.slice(); c[i] ^= 1; return c; };
  await assert.rejects(s1.unwrap(kek, wrapped, s1.wrapAad(flip(fid, 0), kid, fp)), s1.CryptoError);
  await assert.rejects(s1.unwrap(kek, wrapped, s1.wrapAad(fid, flip(kid, 15), fp)), s1.CryptoError);
  await assert.rejects(s1.unwrap(kek, wrapped, s1.wrapAad(fid, kid, flip(fp, 31))), s1.CryptoError);
  await assert.rejects(s1.unwrap(flip(kek, 0), wrapped, s1.wrapAad(fid, kid, fp)), s1.CryptoError);
  for (const i of [0, 12, 59]) await assert.rejects(s1.unwrap(kek, flip(wrapped, i), s1.wrapAad(fid, kid, fp)), s1.CryptoError);
});
