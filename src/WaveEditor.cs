using System;
using System.Collections.Generic;

namespace Hpd20Ctrl
{
    /// <summary>One piece of audio held by the waveform editor: channels of float samples at a rate.</summary>
    public sealed class Segment
    {
        public string Name = "clip";
        public int Rate = 44100;
        public float[][] Channels;
        public int Frames;

        public Segment() { }

        public Segment(string name, int rate, float[][] channels)
        {
            Name = name;
            Rate = rate;
            Channels = channels;
            Frames = channels != null && channels.Length > 0 ? channels[0].Length : 0;
        }

        public int ChannelCount { get { return Channels == null ? 0 : Channels.Length; } }
        public double Seconds { get { return Rate > 0 ? (double)Frames / Rate : 0; } }

        public Segment Clone()
        {
            float[][] c = new float[ChannelCount][];
            for (int i = 0; i < ChannelCount; i++) c[i] = (float[])Channels[i].Clone();
            return new Segment(Name, Rate, c);
        }

        public static Segment From(WavData d, string name)
        {
            return new Segment(name, d.SampleRate, d.Samples);
        }

        public WavData ToWavData()
        {
            WavData d = new WavData();
            d.SampleRate = Rate;
            d.Channels = ChannelCount;
            d.Samples = Channels;
            d.Frames = Frames;
            d.FormatDescription = "waveform editor";
            return d;
        }
    }

    /// <summary>
    /// Pure editing operations on <see cref="Segment"/> values. No UI here, so every operation can be
    /// tested numerically without a window.
    /// </summary>
    public static class WaveOps
    {
        /// <summary>Copies [start, end) into a new segment.</summary>
        public static Segment Trim(Segment s, int start, int end)
        {
            start = Clamp(start, 0, s.Frames);
            end = Clamp(end, 0, s.Frames);
            if (end < start) { int t = start; start = end; end = t; }
            int n = end - start;
            float[][] c = new float[s.ChannelCount][];
            for (int ch = 0; ch < s.ChannelCount; ch++)
            {
                c[ch] = new float[n];
                Array.Copy(s.Channels[ch], start, c[ch], 0, n);
            }
            return new Segment(s.Name, s.Rate, c);
        }

        /// <summary>Removes [start, end) and closes the gap.</summary>
        public static Segment Delete(Segment s, int start, int end)
        {
            start = Clamp(start, 0, s.Frames);
            end = Clamp(end, 0, s.Frames);
            if (end <= start) return s.Clone();
            int n = s.Frames - (end - start);
            float[][] c = new float[s.ChannelCount][];
            for (int ch = 0; ch < s.ChannelCount; ch++)
            {
                c[ch] = new float[n];
                Array.Copy(s.Channels[ch], 0, c[ch], 0, start);
                Array.Copy(s.Channels[ch], end, c[ch], start, s.Frames - end);
            }
            return new Segment(s.Name, s.Rate, c);
        }

        /// <summary>Inserts one segment into another at a sample position.</summary>
        public static Segment Insert(Segment dest, Segment src, int at)
        {
            src = Match(src, dest.Rate, dest.ChannelCount);
            at = Clamp(at, 0, dest.Frames);
            int n = dest.Frames + src.Frames;
            float[][] c = new float[dest.ChannelCount][];
            for (int ch = 0; ch < dest.ChannelCount; ch++)
            {
                c[ch] = new float[n];
                Array.Copy(dest.Channels[ch], 0, c[ch], 0, at);
                Array.Copy(src.Channels[ch], 0, c[ch], at, src.Frames);
                Array.Copy(dest.Channels[ch], at, c[ch], at + src.Frames, dest.Frames - at);
            }
            return new Segment(dest.Name, dest.Rate, c);
        }

        /// <summary>Replaces [start, end) with the source segment.</summary>
        public static Segment Replace(Segment dest, int start, int end, Segment src)
        {
            return Insert(Delete(dest, start, end), src, Clamp(start, 0, dest.Frames));
        }

        public static Segment Silence(Segment s, int start, int end)
        {
            Segment r = s.Clone();
            start = Clamp(start, 0, r.Frames);
            end = Clamp(end, 0, r.Frames);
            for (int ch = 0; ch < r.ChannelCount; ch++)
                for (int i = start; i < end; i++) r.Channels[ch][i] = 0f;
            return r;
        }

        public static Segment FadeIn(Segment s, int start, int end)
        {
            Segment r = s.Clone();
            start = Clamp(start, 0, r.Frames);
            end = Clamp(end, 0, r.Frames);
            int n = end - start;
            if (n <= 1) return r;
            for (int ch = 0; ch < r.ChannelCount; ch++)
                for (int i = 0; i < n; i++) r.Channels[ch][start + i] *= (float)i / (n - 1);
            return r;
        }

        public static Segment FadeOut(Segment s, int start, int end)
        {
            Segment r = s.Clone();
            start = Clamp(start, 0, r.Frames);
            end = Clamp(end, 0, r.Frames);
            int n = end - start;
            if (n <= 1) return r;
            for (int ch = 0; ch < r.ChannelCount; ch++)
                for (int i = 0; i < n; i++) r.Channels[ch][start + i] *= 1f - (float)i / (n - 1);
            return r;
        }

        public static Segment Reverse(Segment s)
        {
            Segment r = s.Clone();
            for (int ch = 0; ch < r.ChannelCount; ch++) Array.Reverse(r.Channels[ch]);
            return r;
        }

        public static Segment Normalize(Segment s, float target)
        {
            Segment r = s.Clone();
            float peak = Peak(r);
            if (peak <= 1e-6f) return r;
            float gain = target / peak;
            for (int ch = 0; ch < r.ChannelCount; ch++)
                for (int i = 0; i < r.Frames; i++) r.Channels[ch][i] *= gain;
            return r;
        }

        public static float Peak(Segment s)
        {
            float peak = 0;
            for (int ch = 0; ch < s.ChannelCount; ch++)
                for (int i = 0; i < s.Frames; i++)
                {
                    float a = Math.Abs(s.Channels[ch][i]);
                    if (a > peak) peak = a;
                }
            return peak;
        }

        /// <summary>Joins segments end to end, resampling and mixing to a common rate and channel count.</summary>
        public static Segment Concat(IList<Segment> segments)
        {
            if (segments == null || segments.Count == 0) return new Segment("empty", 44100, new float[][] { new float[0] });
            if (segments.Count == 1) return segments[0].Clone();

            int rate = segments[0].Rate;
            int channels = 1;
            foreach (Segment s in segments) channels = Math.Max(channels, 1);
            // keep the widest channel count only when every piece matches it, otherwise mono
            bool allSameChannels = true;
            foreach (Segment s in segments) if (s.ChannelCount != segments[0].ChannelCount) allSameChannels = false;
            channels = allSameChannels ? segments[0].ChannelCount : 1;

            List<Segment> prepared = new List<Segment>();
            int total = 0;
            foreach (Segment s in segments)
            {
                Segment m = Match(s, rate, channels);
                prepared.Add(m);
                total += m.Frames;
            }

            float[][] c = new float[channels][];
            for (int ch = 0; ch < channels; ch++) c[ch] = new float[total];
            int at = 0;
            foreach (Segment m in prepared)
            {
                for (int ch = 0; ch < channels; ch++)
                    Array.Copy(m.Channels[ch], 0, c[ch], at, m.Frames);
                at += m.Frames;
            }
            return new Segment(segments[0].Name, rate, c);
        }

        /// <summary>Splits a segment into equal parts (the last part takes the remainder).</summary>
        public static List<Segment> SliceInto(Segment s, int parts)
        {
            List<Segment> list = new List<Segment>();
            if (parts < 1) parts = 1;
            int size = s.Frames / parts;
            if (size < 1) size = 1;
            for (int i = 0; i < parts; i++)
            {
                int start = i * size;
                int end = (i == parts - 1) ? s.Frames : Math.Min(s.Frames, start + size);
                if (start >= s.Frames) break;
                Segment p = Trim(s, start, end);
                p.Name = s.Name + " #" + (i + 1);
                list.Add(p);
            }
            return list;
        }

        /// <summary>
        /// Removes silence from the start and end of a segment, keeping a small margin so the attack and
        /// the tail are not clipped. A clip with no audio above the threshold collapses to a short silence.
        /// </summary>
        public static Segment TrimSilence(Segment s, float thresholdDb, int marginMs)
        {
            if (s == null || s.Frames == 0) return s == null ? null : s.Clone();
            float threshold = (float)Math.Pow(10.0, thresholdDb / 20.0);

            int first = -1, last = -1;
            for (int i = 0; i < s.Frames; i++)
            {
                if (Level(s, i) > threshold)
                {
                    if (first < 0) first = i;
                    last = i;
                }
            }

            if (first < 0)
            {
                // nothing above the threshold: leave a minimal clip rather than an empty one
                int keep = Math.Max(1, s.Rate * Math.Max(1, marginMs) / 1000);
                return Trim(s, 0, Math.Min(s.Frames, keep));
            }

            int margin = Math.Max(0, s.Rate * marginMs / 1000);
            int start = Math.Max(0, first - margin);
            int end = Math.Min(s.Frames, last + 1 + margin);
            return Trim(s, start, end);
        }

        /// <summary>Splits at every silence longer than minGapMs below the threshold (dBFS).</summary>
        public static List<Segment> SliceBySilence(Segment s, float thresholdDb, int minGapMs)
        {
            float threshold = (float)Math.Pow(10.0, thresholdDb / 20.0);
            int minGap = Math.Max(1, s.Rate * minGapMs / 1000);
            List<Segment> list = new List<Segment>();

            int i = 0;
            while (i < s.Frames)
            {
                // skip silence
                while (i < s.Frames && Level(s, i) <= threshold) i++;
                if (i >= s.Frames) break;
                int start = i;
                int silentRun = 0;
                while (i < s.Frames)
                {
                    if (Level(s, i) <= threshold)
                    {
                        silentRun++;
                        if (silentRun >= minGap) break;
                    }
                    else silentRun = 0;
                    i++;
                }
                int end = Math.Min(s.Frames, i - silentRun);
                if (end <= start) end = Math.Min(s.Frames, start + 1);
                Segment p = Trim(s, start, end);
                p.Name = s.Name + " #" + (list.Count + 1);
                list.Add(p);
            }
            if (list.Count == 0) list.Add(s.Clone());
            return list;
        }

        /// <summary>Copies a segment to [rate] and [channels], resampling or downmixing as needed.</summary>
        public static Segment Match(Segment s, int rate, int channels)
        {
            float[][] work = s.Channels;

            if (s.ChannelCount != channels && channels == 1)
            {
                float[] mix = new float[s.Frames];
                for (int i = 0; i < s.Frames; i++)
                {
                    float v = 0;
                    for (int ch = 0; ch < s.ChannelCount; ch++) v += s.Channels[ch][i];
                    mix[i] = v / s.ChannelCount;
                }
                work = new float[][] { mix };
            }
            else if (s.ChannelCount != channels && channels > 1)
            {
                float[][] up = new float[channels][];
                for (int ch = 0; ch < channels; ch++) up[ch] = s.Channels[Math.Min(ch, s.ChannelCount - 1)];
                work = up;
            }

            if (s.Rate != rate)
            {
                float[][] res = new float[work.Length][];
                for (int ch = 0; ch < work.Length; ch++) res[ch] = Resampler.Resample(work[ch], s.Rate, rate);
                return new Segment(s.Name, rate, res);
            }
            return new Segment(s.Name, rate, work);
        }

        private static float Level(Segment s, int i)
        {
            float m = 0;
            for (int ch = 0; ch < s.ChannelCount; ch++)
            {
                float a = Math.Abs(s.Channels[ch][i]);
                if (a > m) m = a;
            }
            return m;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }

    /// <summary>
    /// Bounded undo/redo history. States are pushed <em>before</em> a change, so Undo() returns the
    /// state to restore and hands the current one to the redo stack.
    /// </summary>
    public sealed class UndoHistory<T>
    {
        private readonly List<T> _undo = new List<T>();
        private readonly List<T> _redo = new List<T>();
        private readonly List<long> _sizes = new List<long>();

        public int Limit = 30;                 // steps kept
        public long ByteLimit = 256L << 20;    // ...and a memory ceiling
        public Func<T, long> Measure;          // optional size estimate per state

        public bool CanUndo { get { return _undo.Count > 0; } }
        public bool CanRedo { get { return _redo.Count > 0; } }
        public int UndoDepth { get { return _undo.Count; } }
        public int RedoDepth { get { return _redo.Count; } }

        public void Push(T state)
        {
            _undo.Add(state);
            _sizes.Add(Measure != null ? Measure(state) : 0);
            _redo.Clear();
            Trim();
        }

        /// <summary>Returns the state to restore; the caller's current state goes to the redo stack.</summary>
        public T Undo(T current)
        {
            if (_undo.Count == 0) return current;
            _redo.Add(current);
            int last = _undo.Count - 1;
            T state = _undo[last];
            _undo.RemoveAt(last);
            _sizes.RemoveAt(last);
            return state;
        }

        public T Redo(T current)
        {
            if (_redo.Count == 0) return current;
            _undo.Add(current);
            _sizes.Add(Measure != null ? Measure(current) : 0);
            int last = _redo.Count - 1;
            T state = _redo[last];
            _redo.RemoveAt(last);
            Trim();
            return state;
        }

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
            _sizes.Clear();
        }

        private void Trim()
        {
            while (_undo.Count > Math.Max(1, Limit)) DropOldest();
            if (Measure == null) return;
            long total = 0;
            foreach (long s in _sizes) total += s;
            while (_undo.Count > 1 && total > ByteLimit)
            {
                total -= _sizes[0];
                DropOldest();
            }
        }

        private void DropOldest()
        {
            _undo.RemoveAt(0);
            _sizes.RemoveAt(0);
        }
    }
}
