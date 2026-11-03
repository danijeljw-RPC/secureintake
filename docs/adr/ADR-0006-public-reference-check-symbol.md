# ADR-0006: Public reference check symbol

## Context

F9 defines `SI-XXXXX-XXXXX` with 9 random Crockford symbols and 1 check symbol, but not the check algorithm. Crockford's own mod-37 check uses symbols outside the base32 alphabet.

## Decision

Check value = `Σ (2i + 1) · v_i mod 32` over the nine random symbols (i = 0…8), encoded as one Crockford symbol. Parsing is case-insensitive and maps I/L→1 and O→0.

## Consequences

- Detects every single-symbol substitution (odd weights are coprime with 32; tested exhaustively).
- Detects adjacent transpositions except where the two values differ by exactly 16. Acceptable for a label that grants nothing.
- To be written into S10.

## Status

Proposed, 2026-11-03.
