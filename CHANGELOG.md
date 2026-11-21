# Changelog

## 1.0.0 — 2026-11-21

First public version.

**Pads**
- Interactive top view of the playing surface with the geometry taken from the manual's pad diagram:
  M5 centre, M1/M2 nearest the player, M3/M4 above, S1–S8 across the top half of the rim.
- D-BEAM across the top edge, TRIG IN (head + rim), HH CTRL and both foot switches clickable.
- Learn mode captures each pad's note number from the instrument.
- Factory note defaults (M1=60 … S8=72, D-BEAM=73, Ext Head=74, Ext Rim=75, HH=76) taken from the
  factory backup.
- Per-pad local sample labels and user-instrument numbers.

**Kits**
- All 200 kits with the documented bank/program mapping, kit follow, All Sound Off and Reset
  Controllers, local kit labels.

**Samples**
- Batch resampling of any PCM or float WAV to 44.1 kHz / 16-bit, mono or stereo, 6:00 maximum.
- 24-tap windowed-sinc resampling, optional mono downmix, normalise, silence trim, edge fades,
  truncation.
- USB writer that puts short ASCII-named copies in the root (the only place the HPD-20 looks) and an
  organised pack folder alongside, with index and read-me. Numbering continues across packs.

**Backup editor**
- Reads and writes `BKUP-nnn.HS0` images: 200 kits, 3,400 pads (two layers, 850 named instruments),
  15 chain names, header information.
- Lossless numeric ranges: an unedited file saves byte for byte identically.
- MD5 trailer recomputed on every save; mismatched and undersized files refused.
- Expert page for direct byte access to the undecoded areas.
- USB write-out that picks the next free backup slot.

**MIDI**
- WinMM-based MIDI in/out with SysEx receive buffers, bounded long-message waits and safe teardown.
- Monitor with hex/decoded view, raw SysEx console with Roland checksum helper, Identity Reply
  parsing.

**Hardware verification**
- `--hwtest` against a connected HPD-20: ports found, identity reply parsed (Roland 41H,
  family 7802H), note sent, kit change sent, 2 ms teardown.

**Verification**
- Eight self-test suites, 154 checks, including 11 against a real HPD-20 and a byte-for-byte
  round trip of Roland's factory backup image.
