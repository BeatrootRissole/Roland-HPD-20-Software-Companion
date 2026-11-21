# Changelog

## 1.6.2 — 2026-11-21

**Browser width is genuinely resizable**
- The Samples browser is now held in a vertical split container with a real drag handle, min widths on
  both sides and the browser pinned so it keeps its width when the window grows.
- Corrects 1.5.0: the splitter added there was created but never attached to the panel, so the browser
  stayed at a fixed width. The clip list inside the editor had a 5 px splitter that was awkward to hit;
  it is now 7 px.

## 1.6.1 — 2026-11-21

**Drag queued and resampled files into the waveform editor**
- Rows can now be dragged out of the conversion queue and dropped on the waveform editor to open them
  for editing. Once a file has been resampled, the drag carries the **converted 44.1 kHz WAV** rather
  than the original, so what you edit is exactly what would be written to the stick.
- Dragging a row that is not part of the current selection now drags that row, which is what the
  gesture looks like it should do.
- The queue tooltip explains the behaviour.
- Three new `--uitest` checks cover the payload: source path before conversion, converted path after,
  and that a drag with an empty queue yields nothing.

## 1.6.0 — 2026-11-21

**Trim silence across selected clips**
- New **Trim silence** button in the waveform editor: removes leading and trailing silence from every clip
  selected in the left-hand list, using the same **Silence dB** level as auto-slice and keeping a 5 ms
  margin so the attack and tail survive.
- The clip list is now multi-select, so a whole batch can be trimmed at once.
- One **Undo** (Ctrl+Z) reverses the entire batch, because the operation records a single history entry
  before touching any clip.
- The status line reports how much was removed, and says so plainly when nothing was below the threshold.
- Six new `--wavetest` checks: length after trimming, the margin, an unchanged clip, an all-silent clip
  collapsing to a stub, and channel-aware detection.

## 1.5.1 — 2026-11-21

**Audition fix: the periodic pop is gone**
- The player used to wait for one buffer to finish before writing the next, so the device ran dry between
  chunks — an audible skip and pop roughly four times a second. It now keeps four 80 ms buffers queued
  with the device at all times; measured completion spacing on a real clip is 35–82 ms with no gap above
  one chunk, i.e. no underrun.
- A 3 ms fade is applied at the start and end of the played span, so starting or stopping part way
  through a waveform no longer clicks.
- The playhead counter was rewritten as "frames written minus frames still in flight", which cannot
  drift; `--playtest` now reports completion spacing and prints whether an underrun occurred.
- Two new `--wavetest` checks cover the edge fade; the suite is now 49 checks.

## 1.5.0 — 2026-11-21

**Layout**
- Every pane in the Samples tab is resizable: the browser width, the queue/editor split, and the clip
  list inside the editor.

**Drag and drop**
- Files dragged from the browser onto the waveform editor are loaded (the first replaces the current
  clip, the rest append); dropped on the queue list they are queued. Drops from Windows Explorer are
  accepted too.

**Theme**
- A **Dark mode** switch in the top bar, outside the tabs, recolours every tab, the title bar and the
  waveform, and is remembered in `%APPDATA%\HPD20Companion\ui.ini`. Light mode keeps the native tab
  rendering.

**Cross-platform**
- New `cli/` project builds the audio, backup and editing engine as a command line tool with the .NET
  SDK on Windows, Linux and macOS: `convert`, `info`, `backup-info`, `backup-diff`, `formats`.
- Media Foundation decoding is compiled in on Windows only; elsewhere `convert` explains that
  compressed files need a Windows build or a prior conversion.
- `build.ps1` compiles the CLI with the in-box compiler as a portability check and runs five CLI
  checks, reported in `test-results/clitest.txt`.

## 1.4.0 — 2026-11-21

**Samples tab rebuilt as a single workspace**
- Windows Explorer-style browser replaces the Add files / Add folder buttons: a drive-and-folder tree
  with the audio files of the selected folder listed below, **Add** / **Add all**, and double-click to
  queue.
- The waveform editor is now embedded in the lower half of the tab instead of opening a separate
  window, with a draggable splitter between the queue and the editor.
- The main window opens larger (1200x800) so the denser layout fits without clipping.

**Audition**
- **Play**, **Stop** and a **jog** slider. Play starts at the cursor or plays the selection; the jog
  slider scrubs and, while playing, restarts the stream at the new position. Space toggles playback.
- Playback goes through winmm `waveOut` in ~250 ms chunks, so Stop reacts quickly; a green playhead and
  the time read-out track position while playing.
- Fixed a timing bug where the device-open callback was mistaken for buffer completion, which made
  playback finish early and truncate the audio.

**Tests**
- Five new `--wavetest` checks for the playback buffer (length, interleaving, start offset, clipping,
  empty clip). Suite total is now 218 checks.

## 1.3.1 — 2026-11-21

**Waveform editor: undo and redo**
- Every edit is reversible: Undo/Redo buttons showing the remaining depth, plus Ctrl+Z, Ctrl+Y and
  Ctrl+Shift+Z.
- The history is bounded by both step count (30) and memory (256 MB), so long clips cannot exhaust RAM.
- Undoing the whole session restores the original samples exactly.
- 11 new checks in `--wavetest`: history order, redo clearing, both caps, and an undo-everything audio
  comparison.

## 1.3.0 — 2026-11-21

**Waveform editor**
- New window: load or append clips, drag a selection, and chop with Trim / Cut / Copy / Paste / Delete /
  Silence.
- Slice into N equal pieces, or auto-slice at silences above a chosen gap, so a loop or a pack recorded
  as one file becomes separate clips.
- Recombine by reordering the clip list (Move up/down, Remove, Append), then **Send to samples** to
  render the list into the conversion queue.
- Fade in/out, Reverse and Normalize; zoomable waveform with a time ruler and tooltips on every control.
- Editing lives in `WaveOps` as pure functions, covered by the `--wavetest` suite.

## 1.2.0 — 2026-11-21

**Sample conversion**
- Reads AIFF / AIFF-C (big-endian, `sowt`, float), CAF (LPCM 16/24-bit), AU/SND, and WAV with A-law and
  µ-law, in addition to PCM and float WAV.
- MP3, FLAC, AAC/M4A, ALAC and WMA decode through Windows Media Foundation, so no codec is bundled.
  OGG/Opus work once the free "Web Media Extensions" package is installed.
- NCW is refused with an explanation: Steinberg's format has no Windows decoder.
- Undecodable files report the Windows error code plus the remedy for that format.
- New `--formattest` suite covering every container and probing the Media Foundation path.

**Test hygiene**
- Hardware and real-backup checks report SKIP instead of FAIL when the instrument or stick is absent.

## 1.1.0 — 2026-11-21

**Pads tab**
- Every element shows its current assignment: MIDI note on each pad, controller number on D-BEAM and
  HH CTRL, function on each foot switch.
- Loading a backup mirrors its selected kit onto the tiles (notes and instrument names), with a caption
  naming the kit and file.
- Hover read-out describing the element under the mouse.

**Help**
- Hover explanations on every control in every tab.

**Tooling**
- `--backupdiff a.HS0 b.HS0` compares two images field by field and byte by byte, naming the region of
  each difference.
- The Expert page names the header and system blocks instead of calling them unmapped.

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
