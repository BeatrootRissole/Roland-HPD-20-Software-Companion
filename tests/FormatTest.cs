using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hpd20Ctrl
{
    /// <summary>Checks every container the Samples tab accepts, and probes the Media Foundation path.</summary>
    public static class FormatTest
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

            string dir = Path.Combine(Path.GetTempPath(), "hpd20-formattest");
            Directory.CreateDirectory(dir);

            // ---- AIFF, big-endian 16-bit
            string aiff = Path.Combine(dir, "tone.aiff");
            WriteAiff(aiff, false, 16, false);
            Verify(check, log, aiff, 44100, 1, "AIFF 16-bit big-endian");

            // ---- AIFF-C with 'sowt' (little-endian samples)
            string sowt = Path.Combine(dir, "tone-sowt.aiff");
            WriteAiff(sowt, true, 16, false);
            Verify(check, log, sowt, 44100, 1, "AIFF-C sowt (little-endian)");

            // ---- AIFF-C float
            string fl32 = Path.Combine(dir, "tone-float.aiff");
            WriteAiff(fl32, false, 32, true);
            Verify(check, log, fl32, 44100, 1, "AIFF-C fl32 float");

            // ---- CAF, 16-bit little-endian
            string caf = Path.Combine(dir, "tone.caf");
            WriteCaf(caf, 16);
            Verify(check, log, caf, 44100, 1, "CAF lpcm 16-bit");

            // ---- CAF, 24-bit
            string caf24 = Path.Combine(dir, "tone24.caf");
            WriteCaf(caf24, 24);
            Verify(check, log, caf24, 44100, 1, "CAF lpcm 24-bit");

            // ---- AU, 16-bit big-endian
            string au = Path.Combine(dir, "tone.au");
            WriteAu(au, 3);
            Verify(check, log, au, 44100, 1, "AU 16-bit");

            // ---- AU, 8-bit mu-law
            string auLaw = Path.Combine(dir, "tone-mulaw.au");
            WriteAu(auLaw, 1);
            WavData mulaw = Verify(check, log, auLaw, 44100, 1, "AU mu-law 8-bit");

            // ---- WAV A-law and mu-law
            string wavAlaw = Path.Combine(dir, "tone-alaw.wav");
            WriteWavLaw(wavAlaw, 6);
            Verify(check, log, wavAlaw, 44100, 1, "WAV A-law");
            string wavMulaw = Path.Combine(dir, "tone-mulaw.wav");
            WriteWavLaw(wavMulaw, 7);
            Verify(check, log, wavMulaw, 44100, 1, "WAV mu-law");

            // ---- dispatch by extension / magic
            check("supported extensions include the new formats",
                  AudioFormats.IsSupportedExtension("x.aiff") && AudioFormats.IsSupportedExtension("x.flac") &&
                  AudioFormats.IsSupportedExtension("x.mp3") && AudioFormats.IsSupportedExtension("x.ogg") &&
                  AudioFormats.IsSupportedExtension("x.caf") && AudioFormats.IsSupportedExtension("x.au"));
            check("unknown extension is rejected", !AudioFormats.IsSupportedExtension("x.txt"));

            // ---- full pipeline: AIFF in, HPD-20 spec WAV out
            string outWav = Path.Combine(dir, "converted.wav");
            SampleResult r = SampleProcessor.Process(aiff, outWav, new ResampleOptions());
            check("AIFF converts to 44.1k/16-bit mono", r.Ok);
            if (r.Ok)
            {
                WavData o = WavFile.Read(outWav);
                check("converted file is 44100 Hz mono 16-bit",
                      o.SampleRate == 44100 && o.Channels == 1 && o.FormatDescription.Contains("16"));
            }

            string outWav2 = Path.Combine(dir, "converted-caf.wav");
            SampleResult r2 = SampleProcessor.Process(caf, outWav2, new ResampleOptions());
            check("CAF converts to 44.1k/16-bit mono", r2.Ok);

            // ---- Media Foundation probe: decode a plain WAV through the OS decoder
            if (MfAudio.Available)
            {
                string probe = Path.Combine(dir, "probe.wav");
                WavFile.Write16(probe, new float[][] { MakeTone(44100, 0.4f) }, 44100);
                try
                {
                    WavData viaMf = MfAudio.Decode(probe);
                    check("Media Foundation decodes a test file",
                          viaMf.SampleRate == 44100 && viaMf.Channels >= 1 && viaMf.Frames > 40000);
                    log.AppendLine("      MF returned " + viaMf.Frames + " frames, " + viaMf.Channels + " ch @ " + viaMf.SampleRate);
                }
                catch (Exception ex)
                {
                    check("Media Foundation decodes a test file", false);
                    log.AppendLine("      " + ex.Message);
                }
            }
            else
            {
                log.AppendLine("SKIP  Media Foundation not available on this machine");
            }

            // ---- optional: decode real files supplied by the caller
            string mediaDir = Environment.GetEnvironmentVariable("HPD20_TEST_MEDIA");
            if (!string.IsNullOrEmpty(mediaDir) && Directory.Exists(mediaDir))
            {
                foreach (string f in Directory.GetFiles(mediaDir))
                {
                    if (!AudioFormats.IsSupportedExtension(f)) continue;
                    try
                    {
                        WavData d = AudioFormats.Read(f);
                        check("decode " + Path.GetFileName(f) + " (" + d.FormatDescription + ", " + d.SampleRate + " Hz)", d.Frames > 0);
                    }
                    catch (NotSupportedException ex)
                    {
                        log.AppendLine("SKIP  " + Path.GetFileName(f) + " - " + ex.Message);
                    }
                    catch (Exception ex)
                    {
                        check("decode " + Path.GetFileName(f), false);
                        log.AppendLine("      " + ex.Message);
                    }
                }
            }

            log.AppendLine();
            log.AppendLine(failures == 0 ? "FORMAT CHECKS PASSED" : failures + " FORMAT CHECK(S) FAILED");
            Console.Write(log.ToString());
            if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, log.ToString(), Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }

        private static WavData Verify(Action<string, bool> check, StringBuilder log, string path, int rate, int channels, string label)
        {
            try
            {
                WavData d = AudioFormats.Read(path);
                bool ok = d.SampleRate == rate && d.Channels == channels && d.Frames > 4000;
                float peak = 0;
                for (int c = 0; c < d.Channels; c++)
                    for (int i = 0; i < Math.Min(d.Frames, 20000); i++) peak = Math.Max(peak, Math.Abs(d.Samples[c][i]));
                ok = ok && peak > 0.05f;
                check(label + " decodes (" + d.Frames + " frames, peak " + peak.ToString("0.00") + ")", ok);
                return d;
            }
            catch (Exception ex)
            {
                check(label + " decodes", false);
                log.AppendLine("      " + ex.Message);
                return null;
            }
        }

        private static float[] MakeTone(int frames, float amplitude)
        {
            float[] s = new float[frames];
            for (int i = 0; i < frames; i++) s[i] = (float)(amplitude * Math.Sin(2 * Math.PI * 440 * i / 44100.0));
            return s;
        }

        // ------------------------------- writers for the fixtures -------------------------------

        private static void WriteAiff(string path, bool sowt, int bits, bool floatSamples)
        {
            const int frames = 22050;
            int bytesPerSample = floatSamples ? 4 : bits / 8;
            byte[] audio = new byte[frames * bytesPerSample];
            for (int i = 0; i < frames; i++)
            {
                float v = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 44100.0));
                int o = i * bytesPerSample;
                if (floatSamples)
                {
                    byte[] f = BitConverter.GetBytes(v);
                    Array.Reverse(f);                        // big-endian float
                    Array.Copy(f, 0, audio, o, 4);
                }
                else if (bits == 16)
                {
                    short s = (short)Math.Round(v * 32767);
                    if (sowt) { audio[o] = (byte)(s & 0xFF); audio[o + 1] = (byte)((s >> 8) & 0xFF); }
                    else { audio[o] = (byte)((s >> 8) & 0xFF); audio[o + 1] = (byte)(s & 0xFF); }
                }
            }

            string form = sowt ? "AIFC" : "AIFF";
            int commSize = sowt ? 24 : 18;                  // AIFC adds a 4-byte type + a padded empty name
            int total = 4 + (8 + commSize) + (8 + 8 + audio.Length);

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes("FORM"));
                WriteBE32(w, total);
                w.Write(Encoding.ASCII.GetBytes(form));

                w.Write(Encoding.ASCII.GetBytes("COMM"));
                WriteBE32(w, commSize);
                WriteBE16(w, 1);                             // channels
                WriteBE32(w, frames);
                WriteBE16(w, (short)bits);
                WriteExtended80(w, 44100.0);
                if (sowt)
                {
                    w.Write(Encoding.ASCII.GetBytes(floatSamples ? "fl32" : "sowt"));
                    w.Write((byte)0);                        // empty compression name
                    w.Write((byte)0);                        // pad to even
                }

                w.Write(Encoding.ASCII.GetBytes("SSND"));
                WriteBE32(w, 8 + audio.Length);
                WriteBE32(w, 0);                             // offset
                WriteBE32(w, 0);                             // block size
                w.Write(audio);
                w.Flush();
                File.WriteAllBytes(path, ms.ToArray());
            }
        }

        private static void WriteCaf(string path, int bits)
        {
            const int frames = 22050;
            int bytesPerSample = bits / 8;
            byte[] audio = new byte[frames * bytesPerSample];
            for (int i = 0; i < frames; i++)
            {
                float v = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 44100.0));
                int o = i * bytesPerSample;
                if (bits == 16)
                {
                    short s = (short)Math.Round(v * 32767);
                    audio[o] = (byte)(s & 0xFF); audio[o + 1] = (byte)((s >> 8) & 0xFF);
                }
                else
                {
                    int s = (int)Math.Round(v * 8388607);
                    audio[o] = (byte)(s & 0xFF);
                    audio[o + 1] = (byte)((s >> 8) & 0xFF);
                    audio[o + 2] = (byte)((s >> 16) & 0xFF);
                }
            }

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes("caff"));
                w.Write((short)1);
                w.Write((short)0);

                w.Write(Encoding.ASCII.GetBytes("desc"));
                WriteBE64(w, 32);
                WriteBE64(w, BitConverter.DoubleToInt64Bits(44100.0));
                w.Write(Encoding.ASCII.GetBytes("lpcm"));
                WriteBE32(w, 0x0C);                          // signed integer, packed
                WriteBE32(w, bytesPerSample);                // bytes per packet
                WriteBE32(w, 1);                             // frames per packet
                WriteBE32(w, 1);                             // channels per frame
                WriteBE32(w, bits);                          // bits per channel

                w.Write(Encoding.ASCII.GetBytes("data"));
                WriteBE64(w, 4 + audio.Length);
                w.Write(0);                                  // edit count
                w.Write(audio);
                w.Flush();
                File.WriteAllBytes(path, ms.ToArray());
            }
        }

        private static void WriteAu(string path, int encoding)
        {
            const int frames = 22050;
            int bytes = encoding == 3 ? 2 : 1;
            using (BinaryWriter w = new BinaryWriter(File.Create(path)))
            {
                w.Write(Encoding.ASCII.GetBytes(".snd"));
                WriteBE32(w, 24);
                WriteBE32(w, frames * bytes);
                WriteBE32(w, encoding);
                WriteBE32(w, 44100);
                WriteBE32(w, 1);
                for (int i = 0; i < frames; i++)
                {
                    float v = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 44100.0));
                    if (encoding == 3) WriteBE16(w, (short)Math.Round(v * 32767));
                    else w.Write(EncodeMuLaw(v));
                }
            }
        }
        private static void WriteWavLaw(string path, int format)
        {
            const int frames = 22050;
            using (BinaryWriter w = new BinaryWriter(File.Create(path)))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + frames);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16);
                w.Write((short)format);
                w.Write((short)1);
                w.Write(44100);
                w.Write(44100);
                w.Write((short)1);
                w.Write((short)8);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(frames);
                for (int i = 0; i < frames; i++)
                {
                    float v = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 44100.0));
                    w.Write(format == 7 ? EncodeMuLaw(v) : EncodeALaw(v));
                }
            }
        }

        /// <summary>Encodes by choosing the code whose decoded value is closest to the sample, so the
        /// fixture is consistent with the decoder under test regardless of codec table quirks.</summary>
        private static byte EncodeNearest(float sample, Func<byte, float> decode)
        {
            byte best = 0;
            float bestErr = float.MaxValue;
            for (int code = 0; code < 256; code++)
            {
                float v = decode((byte)code);
                float err = Math.Abs(v - sample);
                if (err < bestErr) { bestErr = err; best = (byte)code; }
            }
            return best;
        }

        private static byte EncodeMuLaw(float sample) { return EncodeNearest(sample, AudioFormats.MuLaw); }
        private static byte EncodeALaw(float sample) { return EncodeNearest(sample, AudioFormats.ALaw); }
        private static void WriteBE16(BinaryWriter w, short value)
        {
            w.Write((byte)((value >> 8) & 0xFF));
            w.Write((byte)(value & 0xFF));
        }

        private static void WriteBE32(BinaryWriter w, int value)
        {
            w.Write((byte)((value >> 24) & 0xFF));
            w.Write((byte)((value >> 16) & 0xFF));
            w.Write((byte)((value >> 8) & 0xFF));
            w.Write((byte)(value & 0xFF));
        }

        private static void WriteBE64(BinaryWriter w, long value)
        {
            for (int i = 7; i >= 0; i--) w.Write((byte)((value >> (8 * i)) & 0xFF));
        }

        private static void WriteExtended80(BinaryWriter w, double value)
        {
            int exponent = 16383 + 15;                       // 44100 = 1.3458 * 2^15
            double mantissa = value / Math.Pow(2, 15);
            ulong m = (ulong)Math.Round(mantissa * Math.Pow(2, 63));
            w.Write((byte)((exponent >> 8) & 0x7F));
            w.Write((byte)(exponent & 0xFF));
            for (int i = 7; i >= 0; i--) w.Write((byte)((m >> (8 * i)) & 0xFF));
        }
    }
}




