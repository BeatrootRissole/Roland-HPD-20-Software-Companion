using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hpd20Ctrl
{
    /// <summary>Decoded audio: one float array per channel, nominally -1..+1.</summary>
    public sealed class WavData
    {
        public int SampleRate;
        public int Channels;
        public float[][] Samples;
        public int Frames;
        public string FormatDescription;

        public double DurationSeconds
        {
            get { return SampleRate > 0 ? (double)Frames / SampleRate : 0; }
        }
    }

    public static class WavFile
    {
        public static WavData Read(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 44) throw new InvalidDataException("file too small");
            if (Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE")
                throw new InvalidDataException("not a RIFF/WAVE file");

            int channels = 0, sampleRate = 0, bits = 0, format = 0;
            int dataOffset = -1, dataLength = 0;

            int pos = 12;
            while (pos + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, pos, 4);
                int size = BitConverter.ToInt32(b, pos + 4);
                int body = pos + 8;
                if (size < 0 || body + size > b.Length) size = b.Length - body;

                if (id == "fmt ")
                {
                    format = BitConverter.ToUInt16(b, body);
                    channels = BitConverter.ToUInt16(b, body + 2);
                    sampleRate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToUInt16(b, body + 14);
                    if (format == 0xFFFE && size >= 40)
                    {
                        // WAVE_FORMAT_EXTENSIBLE: real format is the first two bytes of the subformat GUID
                        format = BitConverter.ToUInt16(b, body + 24);
                    }
                }
                else if (id == "data")
                {
                    dataOffset = body;
                    dataLength = size;
                }
                pos = body + size + (size & 1);
                if (dataOffset >= 0 && channels > 0) break;
            }

            if (dataOffset < 0 || channels <= 0 || sampleRate <= 0)
                throw new InvalidDataException("missing fmt/data chunk");

            int bytesPerSample = bits / 8;
            if (bytesPerSample <= 0) throw new InvalidDataException("bad bit depth");
            int frameBytes = bytesPerSample * channels;
            int frames = dataLength / frameBytes;
            if (frames <= 0) throw new InvalidDataException("no audio frames");

            string desc;
            switch (format)
            {
                case 1: desc = "PCM " + bits + "-bit"; break;
                case 3: desc = "float " + bits + "-bit"; break;
                default: throw new InvalidDataException("unsupported WAV format " + format);
            }

            float[][] ch = new float[channels][];
            for (int c = 0; c < channels; c++) ch[c] = new float[frames];

            for (int i = 0; i < frames; i++)
            {
                int o = dataOffset + i * frameBytes;
                for (int c = 0; c < channels; c++)
                {
                    int p = o + c * bytesPerSample;
                    float v;
                    if (format == 3)
                    {
                        if (bits == 32) v = BitConverter.ToSingle(b, p);
                        else v = (float)BitConverter.ToDouble(b, p);
                    }
                    else
                    {
                        switch (bits)
                        {
                            case 8: v = (b[p] - 128) / 128f; break;
                            case 16: v = BitConverter.ToInt16(b, p) / 32768f; break;
                            case 24:
                                int v24 = b[p] | (b[p + 1] << 8) | (b[p + 2] << 16);
                                if ((v24 & 0x800000) != 0) v24 |= unchecked((int)0xFF000000);
                                v = v24 / 8388608f;
                                break;
                            case 32: v = BitConverter.ToInt32(b, p) / 2147483648f; break;
                            default: throw new InvalidDataException("unsupported bit depth " + bits);
                        }
                    }
                    ch[c][i] = v;
                }
            }

            WavData d = new WavData();
            d.SampleRate = sampleRate;
            d.Channels = channels;
            d.Samples = ch;
            d.Frames = frames;
            d.FormatDescription = desc;
            return d;
        }

        public static void Write16(string path, float[][] channels, int sampleRate)
        {
            int ch = channels.Length;
            int frames = channels[0].Length;
            int dataBytes = frames * ch * 2;

            using (BinaryWriter w = new BinaryWriter(File.Create(path)))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataBytes);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);
                w.Write((ushort)1);
                w.Write((ushort)ch);
                w.Write(sampleRate);
                w.Write(sampleRate * ch * 2);
                w.Write((ushort)(ch * 2));
                w.Write((ushort)16);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataBytes);
                for (int i = 0; i < frames; i++)
                {
                    for (int c = 0; c < ch; c++)
                    {
                        float v = channels[c][i];
                        if (v > 1f) v = 1f;
                        if (v < -1f) v = -1f;
                        w.Write((short)Math.Round(v * 32767f));
                    }
                }
            }
        }
    }

    public static class Resampler
    {
        private const int HalfTaps = 12;          // 24-tap kernel
        private const double CutoffScale = 0.94;  // keep a little headroom below Nyquist

        /// <summary>Windowed-sinc resampling (Blackman window). Good enough for sample material
        /// and much better than nearest/linear when the ratio is far from 1.</summary>
        public static float[] Resample(float[] input, int srcRate, int dstRate)
        {
            if (srcRate == dstRate || input.Length == 0) return input;
            double ratio = (double)dstRate / srcRate;
            int outLen = (int)Math.Round(input.Length * ratio);
            if (outLen <= 0) return new float[0];

            double cutoff = CutoffScale * Math.Min(1.0, ratio);
            float[] output = new float[outLen];
            float[] kernel = new float[HalfTaps * 2 + 1];

            for (int n = 0; n < outLen; n++)
            {
                double t = n / ratio;
                int i0 = (int)Math.Floor(t) - HalfTaps + 1;
                double sum = 0, weightSum = 0;
                for (int k = 0; k < kernel.Length; k++)
                {
                    int idx = i0 + k;
                    double x = (idx - t);
                    double sinc = 1.0;
                    double a = cutoff * x;
                    if (Math.Abs(a) > 1e-9) sinc = Math.Sin(Math.PI * a) / (Math.PI * a);
                    double w = 0.5 + 0.5 * Math.Cos(Math.PI * x / HalfTaps);   // Hann-ish taper
                    double h = cutoff * sinc * (x >= -HalfTaps && x <= HalfTaps ? w : 0);
                    kernel[k] = (float)h;
                    weightSum += h;
                    if (idx >= 0 && idx < input.Length) sum += input[idx] * h;
                }
                if (Math.Abs(weightSum) > 1e-9) sum /= weightSum;
                output[n] = (float)sum;
            }
            return output;
        }
    }

    public sealed class ResampleOptions
    {
        public bool Mono = true;
        public bool Normalize = true;
        public bool TrimSilence = true;
        public bool FadeEdges = true;
        public double MaxSeconds = 360.0;   // HPD-20 limit: 6 minutes per file
        public int TargetRate = 44100;

        public ResampleOptions Clone()
        {
            ResampleOptions o = (ResampleOptions)MemberwiseClone();
            return o;
        }
    }

    public sealed class SampleResult
    {
        public string SourcePath;
        public string OutputPath;
        public bool Ok;
        public string Message;
        public double SourceSeconds;
        public double OutputSeconds;
        public string SourceFormat;
        public bool Truncated;
        public bool WasResampled;
    }

    public static class SampleProcessor
    {
        public static SampleResult Process(string sourcePath, string outputPath, ResampleOptions o)
        {
            SampleResult r = new SampleResult();
            r.SourcePath = sourcePath;
            r.OutputPath = outputPath;
            try
            {
                WavData d = WavFile.Read(sourcePath);
                r.SourceSeconds = d.DurationSeconds;
                r.SourceFormat = d.SampleRate + " Hz, " + d.Channels + " ch, " + d.FormatDescription;

                float[][] ch = d.Samples;
                if (o.Mono && ch.Length > 1)
                {
                    float[] mix = new float[d.Frames];
                    for (int i = 0; i < d.Frames; i++)
                    {
                        float s = 0;
                        for (int c = 0; c < ch.Length; c++) s += ch[c][i];
                        mix[i] = s / ch.Length;
                    }
                    ch = new float[][] { mix };
                }

                if (d.SampleRate != o.TargetRate)
                {
                    for (int c = 0; c < ch.Length; c++) ch[c] = Resampler.Resample(ch[c], d.SampleRate, o.TargetRate);
                    r.WasResampled = true;
                }

                int limit = (int)Math.Round(o.MaxSeconds * o.TargetRate);
                if (ch[0].Length > limit)
                {
                    for (int c = 0; c < ch.Length; c++)
                    {
                        float[] t = new float[limit];
                        Array.Copy(ch[c], t, limit);
                        ch[c] = t;
                    }
                    r.Truncated = true;
                }

                if (o.TrimSilence) Trim(ch, o.TargetRate);
                if (o.FadeEdges) Fade(ch, o.TargetRate);
                if (o.Normalize) Normalize(ch);

                WavFile.Write16(outputPath, ch, o.TargetRate);
                r.OutputSeconds = (double)ch[0].Length / o.TargetRate;
                r.Ok = true;
                r.Message = (r.WasResampled ? "resampled to 44.1 kHz" : "44.1 kHz") +
                            (o.Mono && d.Channels > 1 ? ", mono" : ", " + ch.Length + " ch") +
                            (o.Normalize ? ", normalised" : "") +
                            (o.TrimSilence ? ", trimmed" : "") +
                            (r.Truncated ? ", TRUNCATED at " + o.MaxSeconds + "s" : "");
                if (!o.Normalize && !r.WasResampled && !o.TrimSilence) r.Message = "already compliant";
                return r;
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Message = ex.Message;
                return r;
            }
        }

        private static void Normalize(float[][] ch)
        {
            float peak = 0;
            for (int c = 0; c < ch.Length; c++)
                for (int i = 0; i < ch[c].Length; i++)
                {
                    float a = Math.Abs(ch[c][i]);
                    if (a > peak) peak = a;
                }
            if (peak <= 1e-6f) return;
            float gain = 0.985f / peak;
            for (int c = 0; c < ch.Length; c++)
                for (int i = 0; i < ch[c].Length; i++) ch[c][i] *= gain;
        }

        private static void Trim(float[][] ch, int rate)
        {
            int n = ch[0].Length;
            float threshold = 0.001f;       // ~ -60 dB
            int start = 0, end = n - 1;
            while (start < n && Loudness(ch, start) < threshold) start++;
            while (end > start && Loudness(ch, end) < threshold) end--;
            if (start >= end) return;
            int pre = Math.Min(start, rate / 100);       // keep 10 ms
            int post = Math.Min(n - 1 - end, rate / 100);
            start -= pre;
            end += post;
            if (start <= 0 && end >= n - 1) return;
            int len = end - start + 1;
            for (int c = 0; c < ch.Length; c++)
            {
                float[] t = new float[len];
                Array.Copy(ch[c], start, t, 0, len);
                ch[c] = t;
            }
        }

        private static float Loudness(float[][] ch, int i)
        {
            float m = 0;
            for (int c = 0; c < ch.Length; c++)
            {
                float a = Math.Abs(ch[c][i]);
                if (a > m) m = a;
            }
            return m;
        }

        private static void Fade(float[][] ch, int rate)
        {
            int n = ch[0].Length;
            int f = Math.Min(rate / 200, n / 4);        // 5 ms
            if (f <= 1) return;
            for (int c = 0; c < ch.Length; c++)
            {
                for (int i = 0; i < f; i++)
                {
                    float g = (float)i / f;
                    ch[c][i] *= g;
                    ch[c][n - 1 - i] *= g;
                }
            }
        }
    }

    /// <summary>Checks the resampler against generated WAVs of several source formats.</summary>
    public static class AudioSelfTest
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
            string dir = Path.Combine(Path.GetTempPath(), "hpd20-audiotest");
            Directory.CreateDirectory(dir);
            ResampleOptions opt = new ResampleOptions();
            opt.Mono = true; opt.Normalize = true; opt.TrimSilence = true; opt.FadeEdges = true; opt.MaxSeconds = 360;

            // 1. 48 kHz stereo 16-bit -> 44.1 kHz mono
            string src1 = Path.Combine(dir, "src48stereo.wav");
            int frames = 48000;
            float[][] st = new float[2][];
            st[0] = new float[frames]; st[1] = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                double t = (double)i / 48000.0;
                float v = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * t));
                st[0][i] = v;
                st[1][i] = v * 0.5f;
            }
            WavFile.Write16(src1, st, 48000);
            string out1 = Path.Combine(dir, "out48.wav");
            SampleResult r1 = SampleProcessor.Process(src1, out1, opt);
            check("48k stereo processed", r1.Ok);
            check("48k resampled flag", r1.WasResampled);
            if (r1.Ok)
            {
                WavData d = WavFile.Read(out1);
                check("output rate 44100", d.SampleRate == 44100);
                check("output mono", d.Channels == 1);
                check("output 16-bit", d.FormatDescription.Contains("16"));
                check("duration preserved (~1 s)", Math.Abs(d.DurationSeconds - 1.0) < 0.05);
                float peak = 0;
                for (int i = 0; i < d.Frames; i++) peak = Math.Max(peak, Math.Abs(d.Samples[0][i]));
                check("normalised peak > 0.9", peak > 0.9f);
            }

            // 2. already compliant 44.1 kHz mono 16-bit is not resampled
            string src2 = Path.Combine(dir, "src441mono.wav");
            float[][] mono = new float[1][];
            mono[0] = new float[44100];
            for (int i = 0; i < 44100; i++) mono[0][i] = (float)(0.3 * Math.Sin(2 * Math.PI * 220 * i / 44100.0));
            WavFile.Write16(src2, mono, 44100);
            string out2 = Path.Combine(dir, "out44.wav");
            SampleResult r2 = SampleProcessor.Process(src2, out2, opt);
            check("44.1k processed", r2.Ok);
            check("44.1k not resampled", !r2.WasResampled);
            if (r2.Ok)
            {
                WavData d = WavFile.Read(out2);
                check("44.1k stays 44100/mono", d.SampleRate == 44100 && d.Channels == 1);
            }

            // 3. 8-bit PCM source is decoded
            string src3 = Path.Combine(dir, "src8bit.wav");
            WriteRaw8Bit(src3, 22050, 1, 22050);
            SampleResult r3 = SampleProcessor.Process(src3, Path.Combine(dir, "out8.wav"), opt);
            check("8-bit source decoded and resampled", r3.Ok && r3.WasResampled);

            // 4. 24-bit PCM stereo source is decoded
            string src4 = Path.Combine(dir, "src24bit.wav");
            WriteRaw24Bit(src4, 32000, 2, 16000);
            SampleResult r4 = SampleProcessor.Process(src4, Path.Combine(dir, "out24.wav"), opt);
            check("24-bit stereo source decoded", r4.Ok);
            if (r4.Ok)
            {
                WavData d = WavFile.Read(Path.Combine(dir, "out24.wav"));
                check("24-bit output is 44.1k mono", d.SampleRate == 44100 && d.Channels == 1);
            }

            // 5. over-length file is truncated to the limit
            ResampleOptions shortOpt = opt.Clone();
            shortOpt.MaxSeconds = 0.5;
            string src5 = Path.Combine(dir, "src2s.wav");
            float[][] two = new float[1][];
            two[0] = new float[88200];
            for (int i = 0; i < 88200; i++) two[0][i] = (float)(0.4 * Math.Sin(2 * Math.PI * 100 * i / 44100.0));
            WavFile.Write16(src5, two, 44100);
            SampleResult r5 = SampleProcessor.Process(src5, Path.Combine(dir, "out_trunc.wav"), shortOpt);
            check("over-length file truncated", r5.Ok && r5.Truncated);
            if (r5.Ok) check("truncated to 0.5 s", Math.Abs(r5.OutputSeconds - 0.5) < 0.02);

            // 6. leading/trailing silence is trimmed
            string src6 = Path.Combine(dir, "srcsilence.wav");
            float[][] sil = new float[1][];
            sil[0] = new float[88200];
            for (int i = 22050; i < 44100; i++) sil[0][i] = (float)(0.4 * Math.Sin(2 * Math.PI * 300 * i / 44100.0));
            WavFile.Write16(src6, sil, 44100);
            SampleResult r6 = SampleProcessor.Process(src6, Path.Combine(dir, "out_trim.wav"), opt);
            check("silence trimmed", r6.Ok && r6.OutputSeconds < 0.75 && r6.OutputSeconds > 0.4);

            log.AppendLine();
            log.AppendLine(failures == 0 ? "AUDIO CHECKS PASSED" : failures + " AUDIO CHECK(S) FAILED");
            Console.Write(log.ToString());
            if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, log.ToString(), Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }

        private static void WriteRaw8Bit(string path, int rate, int channels, int frames)
        {
            int dataBytes = frames * channels;
            using (BinaryWriter w = new BinaryWriter(File.Create(path)))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataBytes);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
                w.Write((ushort)1); w.Write((ushort)channels); w.Write(rate);
                w.Write(rate * channels); w.Write((ushort)channels); w.Write((ushort)8);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataBytes);
                for (int i = 0; i < frames; i++)
                    w.Write((byte)(128 + 100 * Math.Sin(2 * Math.PI * 200 * i / rate)));
            }
        }

        private static void WriteRaw24Bit(string path, int rate, int channels, int frames)
        {
            int dataBytes = frames * channels * 3;
            using (BinaryWriter w = new BinaryWriter(File.Create(path)))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataBytes);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
                w.Write((ushort)1); w.Write((ushort)channels); w.Write(rate);
                w.Write(rate * channels * 3); w.Write((ushort)(channels * 3)); w.Write((ushort)24);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataBytes);
                for (int i = 0; i < frames; i++)
                {
                    int v = (int)(2000000 * Math.Sin(2 * Math.PI * 150 * i / rate));
                    for (int c = 0; c < channels; c++)
                    {
                        w.Write((byte)(v & 0xFF));
                        w.Write((byte)((v >> 8) & 0xFF));
                        w.Write((byte)((v >> 16) & 0xFF));
                    }
                }
            }
        }
    }
}

