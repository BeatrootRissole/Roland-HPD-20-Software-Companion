using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hpd20Ctrl
{
    /// <summary>Numeric checks for the waveform editor's editing operations.</summary>
    public static class WaveTest
    {
        public static int Run(string reportPath)
        {
            StringBuilder log = new StringBuilder();
            int failures = 0;
            Action<string, bool> check = delegate(string name, bool ok)
            {
                if (!ok) failures++;
                log.AppendLine((ok ? "PASS  " : "FAIL  ") + name);
            };

            // a 1 second ramp at 44100 Hz mono, sample i = i / 44100
            Segment ramp = Ramp(44100, 44100);
            check("source segment is 1 s", ramp.Frames == 44100 && ramp.Rate == 44100);

            // ---- trim
            Segment trimmed = WaveOps.Trim(ramp, 1000, 2000);
            check("trim keeps the requested length", trimmed.Frames == 1000);
            check("trim starts at the right sample", Math.Abs(trimmed.Channels[0][0] - ramp.Channels[0][1000]) < 1e-6);
            check("trim ends at the right sample", Math.Abs(trimmed.Channels[0][999] - ramp.Channels[0][1999]) < 1e-6);

            // ---- delete closes the gap
            Segment deleted = WaveOps.Delete(ramp, 1000, 2000);
            check("delete removes exactly the selection", deleted.Frames == 43100);
            check("delete joins the two sides", Math.Abs(deleted.Channels[0][999] - ramp.Channels[0][999]) < 1e-6 &&
                                                  Math.Abs(deleted.Channels[0][1000] - ramp.Channels[0][2000]) < 1e-6);

            // ---- insert
            Segment half = WaveOps.Trim(ramp, 0, 500);
            Segment inserted = WaveOps.Insert(ramp, half, 2000);
            check("insert lengthens the segment", inserted.Frames == 44600);
            check("insert places the copy at the cursor",
                  Math.Abs(inserted.Channels[0][2000] - ramp.Channels[0][0]) < 1e-6 &&
                  Math.Abs(inserted.Channels[0][2499] - ramp.Channels[0][499]) < 1e-6);
            check("insert keeps the tail", Math.Abs(inserted.Channels[0][2500] - ramp.Channels[0][2000]) < 1e-6);

            // ---- replace
            Segment replaced = WaveOps.Replace(ramp, 1000, 2000, half);
            check("replace swaps the selection for the clipboard", replaced.Frames == 43600);
            check("replace puts the clipboard content in place",
                  Math.Abs(replaced.Channels[0][1000] - ramp.Channels[0][0]) < 1e-6);

            // ---- silence / fades / reverse / normalise
            Segment silenced = WaveOps.Silence(ramp, 100, 200);
            check("silence zeroes only the selection", silenced.Channels[0][150] == 0f && silenced.Channels[0][99] != 0f);

            Segment fadeIn = WaveOps.FadeIn(ramp, 0, 1000);
            check("fade in starts at zero", Math.Abs(fadeIn.Channels[0][0]) < 1e-6f);
            check("fade in ends at full", Math.Abs(fadeIn.Channels[0][999] - ramp.Channels[0][999]) < 1e-4f);

            Segment fadeOut = WaveOps.FadeOut(ramp, 0, 1000);
            check("fade out ends near zero", Math.Abs(fadeOut.Channels[0][999]) < 1e-4f);

            Segment reversed = WaveOps.Reverse(ramp);
            check("reverse flips the ends", Math.Abs(reversed.Channels[0][0] - ramp.Channels[0][ramp.Frames - 1]) < 1e-6);

            Segment quiet = Scale(ramp, 0.1f);
            Segment loud = WaveOps.Normalize(quiet, 0.98f);
            check("normalise brings the peak up", Math.Abs(WaveOps.Peak(loud) - 0.98f) < 0.01f);

            // ---- slicing
            List<Segment> parts = WaveOps.SliceInto(ramp, 4);
            int total = 0;
            foreach (Segment p in parts) total += p.Frames;
            check("slice into 4 gives 4 pieces", parts.Count == 4);
            check("slice into 4 keeps every sample", total == ramp.Frames);
            check("slice pieces are contiguous",
                  Math.Abs(parts[1].Channels[0][0] - ramp.Channels[0][parts[0].Frames]) < 1e-6);

            // ---- auto slice on silence: three bursts
            Segment bursts = Bursts(44100, 3, 2000, 4000);
            List<Segment> found = WaveOps.SliceBySilence(bursts, -40f, 50);
            check("auto-slice finds three bursts", found.Count == 3);
            if (found.Count == 3)
                check("auto-slice keeps burst lengths", Math.Abs(found[0].Frames - 2000) <= 2 && Math.Abs(found[2].Frames - 2000) <= 2);

            // ---- concat across rates and channel counts
            Segment a = Tone(44100, 1000, 0.5f, 1);
            Segment b = Tone(22050, 500, 0.5f, 1);          // 500 frames at 22.05k = same 22.6 ms
            Segment joined = WaveOps.Concat(new List<Segment> { a, b });
            check("concat uses the first rate", joined.Rate == 44100);
            check("concat resamples the second clip", joined.Frames == 1000 + 1000);

            Segment stereo = Tone(44100, 500, 0.5f, 2);
            Segment mixed = WaveOps.Concat(new List<Segment> { a, stereo });
            check("concat downmixes when channel counts differ", mixed.ChannelCount == 1);
            check("concat total length is right", mixed.Frames == 1500);

            // ---- match
            Segment resampled = WaveOps.Match(b, 44100, 1);
            check("match resamples to the target rate", resampled.Rate == 44100 && resampled.Frames == 1000);
            Segment upmix = WaveOps.Match(a, 44100, 2);
            check("match upmixes to two channels", upmix.ChannelCount == 2);

            // ---- end to end: edit, render, convert
            string dir = Path.Combine(Path.GetTempPath(), "hpd20-wavetest");
            Directory.CreateDirectory(dir);
            Segment edited = WaveOps.Concat(new List<Segment> { WaveOps.Trim(ramp, 0, 5000), WaveOps.Trim(ramp, 20000, 25000) });
            string raw = Path.Combine(dir, "edited.wav");
            WavFile.Write16(raw, edited.Channels, edited.Rate);
            string outFile = Path.Combine(dir, "converted.wav");
            SampleResult r = SampleProcessor.Process(raw, outFile, new ResampleOptions());
            check("edited clip converts to the HPD-20 spec", r.Ok);
            if (r.Ok)
            {
                WavData d = WavFile.Read(outFile);
                check("converted edit is 44.1 kHz mono 16-bit",
                      d.SampleRate == 44100 && d.Channels == 1 && d.FormatDescription.Contains("16"));
                check("converted edit is the expected length", Math.Abs(d.DurationSeconds - 0.2268) < 0.01);
            }

            // ---- undo / redo history
            UndoHistory<Segment> history = new UndoHistory<Segment>();
            Segment v0 = ramp;
            Segment v1 = WaveOps.Trim(ramp, 0, 1000);
            Segment v2 = WaveOps.Reverse(v1);
            history.Push(v0);
            history.Push(v1);
            check("history starts with two steps", history.UndoDepth == 2 && history.CanUndo && !history.CanRedo);

            Segment back = history.Undo(v2);
            check("undo returns the previous version", back.Frames == v1.Frames && back.Channels[0][0] == v1.Channels[0][0]);
            check("undo moves a step to redo", history.UndoDepth == 1 && history.RedoDepth == 1);

            Segment again = history.Undo(back);
            check("undo again reaches the original", again.Frames == v0.Frames && again.Channels[0][10] == v0.Channels[0][10]);

            Segment forward = history.Redo(again);
            check("redo returns the first edit", forward.Frames == v1.Frames);
            check("redo depth shrinks", history.RedoDepth == 1 && history.UndoDepth == 1);

            history.Push(forward);
            check("a new edit clears the redo stack", !history.CanRedo);

            UndoHistory<Segment> capped = new UndoHistory<Segment>();
            capped.Limit = 3;
            for (int i = 0; i < 5; i++) capped.Push(ramp);
            check("history keeps only the limit", capped.UndoDepth == 3);

            UndoHistory<Segment> byBytes = new UndoHistory<Segment>();
            byBytes.Measure = delegate(Segment s) { return 100L << 20; };   // 100 MB each
            byBytes.ByteLimit = 150L << 20;
            for (int i = 0; i < 5; i++) byBytes.Push(ramp);
            check("history trims by memory too", byBytes.UndoDepth <= 2);

            // an edit sequence must undo back to identical audio
            UndoHistory<Segment> session = new UndoHistory<Segment>();
            Segment work = ramp;
            session.Push(work);
            work = WaveOps.Trim(work, 5000, 30000);
            session.Push(work);
            work = WaveOps.FadeIn(work, 0, 2000);
            session.Push(work);
            work = WaveOps.Silence(work, 3000, 4000);
            check("edited clip differs from the original", work.Frames != ramp.Frames || work.Channels[0][10] != ramp.Channels[0][10]);
            while (session.CanUndo) work = session.Undo(work);
            bool identical = work.Frames == ramp.Frames;
            if (identical)
                for (int i = 0; i < ramp.Frames; i++)
                    if (work.Channels[0][i] != ramp.Channels[0][i]) { identical = false; break; }
            check("undoing every step restores the original audio", identical);
            // ---- playback buffer
            Segment stereoPiece = Tone(44100, 1000, 0.5f, 2);
            short[] pcm = PlaybackBuffer.ToPcm16(stereoPiece, 100, 600);
            check("playback buffer has the right length", pcm.Length == 500 * 2);
            check("playback buffer interleaves channels", pcm[0] == pcm[1]);
            check("playback buffer starts at the requested sample",
                  Math.Abs(pcm[0] - (short)Math.Round(stereoPiece.Channels[0][100] * 32767f)) <= 1);

            Segment hot = Scale(Tone(44100, 100, 0.5f, 1), 4f);
            short[] clipped = PlaybackBuffer.ToPcm16(hot, 0, 100);
            bool withinRange = true;
            foreach (short v in clipped) if (v > 32767 || v < -32768) withinRange = false;
            check("playback buffer clips instead of wrapping", withinRange);

            short[] none = PlaybackBuffer.ToPcm16(null, 0, 10);
            check("playback buffer handles an empty clip", none.Length == 0);

            short[] fading = PlaybackBuffer.ToPcm16(Tone(44100, 4410, 0.8f, 1), 0, 4410);
            PlaybackBuffer.FadeEdges(fading, 1, 44100, 3.0);
            check("edge fade silences the first and last sample", fading[0] == 0 && Math.Abs(fading[fading.Length - 1]) <= 2);
            int midPeak = 0;
            for (int i = fading.Length / 3; i < fading.Length * 2 / 3; i++) midPeak = Math.Max(midPeak, Math.Abs((int)fading[i]));
            check("edge fade keeps the middle untouched", midPeak > 20000);

            // ---- trim silence at the ends
            Segment padded = Padded(44100, 0.2, 0.5, 0.3);      // 0.2 s silence, 0.5 s tone, 0.3 s silence
            check("padded clip is one second", Math.Abs(padded.Seconds - 1.0) < 0.01);
            Segment trimmedEnds = WaveOps.TrimSilence(padded, -40f, 5);
            check("trim silence keeps the sound only", Math.Abs(trimmedEnds.Seconds - 0.51) < 0.02);
            check("trim silence starts just before the tone",
                  Math.Abs(trimmedEnds.Seconds * 1000 - 510) < 20);

            Segment noSilence = Tone(44100, 4410, 0.5f, 1);
            Segment unchanged = WaveOps.TrimSilence(noSilence, -40f, 5);
            check("a clip with no silence is left alone", unchanged.Frames == noSilence.Frames);

            Segment silent = new Segment("silent", 44100, new float[][] { new float[44100] });
            Segment collapsed = WaveOps.TrimSilence(silent, -40f, 5);
            check("an all-silent clip collapses to a stub", collapsed.Frames > 0 && collapsed.Frames <= 441);

            float[][] twoCh = new float[2][];
            twoCh[0] = new float[4410];                                   // silent left
            twoCh[1] = Tone(44100, 4410, 0.5f, 1).Channels[0];            // loud right
            Segment stereoPad = new Segment("stereo", 44100, twoCh);
            Segment stereoTrim = WaveOps.TrimSilence(stereoPad, -40f, 5);
            check("trim silence looks at all channels", stereoTrim.Frames >= 4410);

            log.AppendLine();
            log.AppendLine(failures == 0 ? "WAVE CHECKS PASSED" : failures + " WAVE CHECK(S) FAILED");
            Console.Write(log.ToString());
            if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, log.ToString(), Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }

        /// <summary>tone surrounded by silence, in seconds.</summary>
        private static Segment Padded(int rate, double lead, double tone, double tail)
        {
            int a = (int)(rate * lead), b = (int)(rate * tone), c = (int)(rate * tail);
            float[] s = new float[a + b + c];
            for (int i = 0; i < b; i++) s[a + i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / rate));
            return new Segment("padded", rate, new float[][] { s });
        }

        private static Segment Ramp(int rate, int frames)
        {
            float[] s = new float[frames];
            for (int i = 0; i < frames; i++) s[i] = (float)i / frames;
            return new Segment("ramp", rate, new float[][] { s });
        }

        private static Segment Tone(int rate, int frames, float amplitude, int channels)
        {
            float[][] c = new float[channels][];
            for (int ch = 0; ch < channels; ch++) c[ch] = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float v = (float)(amplitude * Math.Sin(2 * Math.PI * 440 * i / rate));
                for (int ch = 0; ch < channels; ch++) c[ch][i] = v;
            }
            return new Segment("tone", rate, c);
        }

        private static Segment Scale(Segment s, float gain)
        {
            float[][] c = new float[s.ChannelCount][];
            for (int ch = 0; ch < s.ChannelCount; ch++)
            {
                c[ch] = new float[s.Frames];
                for (int i = 0; i < s.Frames; i++) c[ch][i] = s.Channels[ch][i] * gain;
            }
            return new Segment(s.Name, s.Rate, c);
        }

        /// <summary>bursts of tone separated by silence, e.g. a drum loop recorded as one file.</summary>
        private static Segment Bursts(int rate, int count, int burstFrames, int silenceFrames)
        {
            int total = count * burstFrames + (count - 1) * silenceFrames;
            float[] s = new float[total];
            int at = 0;
            for (int b = 0; b < count; b++)
            {
                for (int i = 0; i < burstFrames; i++)
                    s[at + i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / rate));
                at += burstFrames;
                at += b == count - 1 ? 0 : silenceFrames;
            }
            return new Segment("bursts", rate, new float[][] { s });
        }
    }
}


