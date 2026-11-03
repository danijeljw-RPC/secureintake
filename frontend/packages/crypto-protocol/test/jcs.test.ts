import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { canonicalize, JcsError } from '../src/jcs.ts';

const vectors = JSON.parse(readFileSync(new URL('../../../../docs/specs/vectors/si-jcs-1.json', import.meta.url), 'utf8'));
const input = (c: { input?: string; inputHex?: string }) =>
  c.inputHex !== undefined ? Uint8Array.from(Buffer.from(c.inputHex, 'hex')) : new TextEncoder().encode(c.input);

for (const c of vectors.valid) {
  test(`valid: ${c.name}`, () => {
    const out = canonicalize(input(c));
    assert.equal(Buffer.from(out).toString('hex'), c.canonicalHex);
    assert.equal(Buffer.from(canonicalize(out)).toString('hex'), c.canonicalHex);
  });
}

for (const c of vectors.invalid) {
  test(`invalid: ${c.name}`, () => {
    assert.throws(() => canonicalize(input(c)), JcsError);
  });
}
