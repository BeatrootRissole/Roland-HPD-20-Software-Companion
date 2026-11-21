using System;
using System.IO;
using System.Text;

namespace Hpd20Ctrl
{
    /// <summary>
    /// Reads any audio file the app can convert: WAV (incl. A-law/Âµ-law), AIFF/AIFF-C, CAF (LPCM),
    /// AU/SND (incl. A-law/Âµ-law), and anything else through Media Foundation (MP3, FLAC, OGG, AAC,
    /// ALAC, WMA...).
    /// </summary>
    public static class AudioFormats
    {
        public static readonly string[] Extensions = new string[]
        {
            "wav", "wave", "aif", "aiff", "aifc", "caf", "au", "snd",
            "mp3", "flac", "ogg", "oga", "opus", "m4a", "aac", "wma", "alac", "mp4"
        };

        public static string Filter
        {
            get
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("Audio files (");
                sb.Append(string.Join(";", Array.ConvertAll(Extensions, e => "*." + e)));
                sb.Append(")|");
                sb.Append(string.Join(";", Array.ConvertAll(Extensions, e => "*." + e)));
                sb.Append("|All files (*.*)|*.*");
                return sb.ToString();
            }
        }

        public static bool IsSupportedExtension(string path)
        {
            string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return Array.IndexOf(Extensions, ext) >= 0;
        }

        /// <summary>Decodes a file into interleaved-by-channel float samples.</summary>
        public static WavData Read(string path)
        {
            byte[] head = new byte[12];
            using (FileStream fs = File.OpenRead(path))
            {
                int n = fs.Read(head, 0, head.Length);
                if (n < 12) throw new InvalidDataException("file too small");
            }
            string magic = Encoding.ASCII.GetString(head, 0, 4);
            string magic8 = Encoding.ASCII.GetString(head, 8, 4);

            if (magic == "RIFF" && magic8 == "WAVE") return ReadWav(path);
            if (magic == "FORM" && (magic8 == "AIFF" || magic8 == "AIFC")) return ReadAiff(path);
            if (magic == "caff") return ReadCaf(path);
            if (magic == ".snd") return ReadAu(path);

            string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (ext == "ncw")
                throw new NotSupportedException("NCW is Steinberg's proprietary format with no Windows decoder. " +
                                                "Export the sample as WAV from the Steinberg software and convert that.");

#if WINDOWS
            // everything else: let Windows decode it
            WavData d = MfAudio.Decode(path);
            d.FormatDescription = "decoded by Windows Media Foundation";
            return d;
#else
            throw new NotSupportedException(
                "compressed formats (MP3, FLAC, AAC, WMA) are decoded through Windows Media Foundation, which is not " +
                "available on this platform. Convert the file to WAV, AIFF, CAF or AU first, or run the Windows app.");
#endif
        }

        // ------------------------------- WAV -------------------------------

        private static WavData ReadWav(string path)
        {
            return WavFile.Read(path);   // shared with the existing writer/reader
        }

        // ------------------------------- AIFF -------------------------------

        private static WavData ReadAiff(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            int channels = 0, bits = 0, frames = 0, dataOffset = -1, dataLength = 0;
            double rate = 0;
            string compression = "NONE";

            int pos = 12;
            while (pos + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, pos, 4);
                int size = BE32(b, pos + 4);
                int body = pos + 8;
                if (size < 0 || body + size > b.Length) size = b.Length - body;

                if (id == "COMM")
                {
                    channels = BE16(b, body);
                    frames = BE32(b, body + 2);
                    bits = BE16(b, body + 6);
                    rate = Extended80(b, body + 8);
                    if (size >= 22) compression = Encoding.ASCII.GetString(b, body + 18, 4);
                }
                else if (id == "SSND")
                {
                    int offset = BE32(b, body);
                    dataOffset = body + 8 + offset;
                    dataLength = size - 8 - offset;
                }
                pos = body + size + (size & 1);
                if (dataOffset >= 0 && channels > 0) break;
            }
            if (channels <= 0 || rate <= 0 || dataOffset < 0)
                throw new InvalidDataException("not a readable AIFF file");

            bool littleEndian = compression == "sowt" || compression == "SOWT";
            bool isFloat = compression == "fl32" || compression == "FL32" || compression == "fl64" || compression == "FL64";
            int bytesPerSample = isFloat ? (compression == "fl32" || compression == "FL32" ? 4 : 8) : bits / 8;
            if (bytesPerSample <= 0) throw new InvalidDataException("unsupported AIFF bit depth");

            int frameBytes = bytesPerSample * channels;
            int available = Math.Min(dataLength, b.Length - dataOffset) / frameBytes;
            if (frames <= 0 || frames > available) frames = available;
            if (frames <= 0) throw new InvalidDataException("AIFF contains no audio frames");

            float[][] ch = new float[channels][];
            for (int c = 0; c < channels; c++) ch[c] = new float[frames];

            for (int i = 0; i < frames; i++)
            {
                int o = dataOffset + i * frameBytes;
                for (int c = 0; c < channels; c++)
                {
                    int p = o + c * bytesPerSample;
                    float v;
                    if (isFloat)
                    {
                        int raw = littleEndian ? LE32(b, p) : BE32(b, p);
                        v = BitConverter.ToSingle(BitConverter.GetBytes(raw), 0);
                    }
                    else if (bits == 8)
                    {
                        v = (sbyte)b[p] / 128f;              // AIFF 8-bit is signed
                    }
                    else if (bits == 16)
                    {
                        short s = littleEndian ? (short)LE16(b, p) : (short)BE16(b, p);
                        v = s / 32768f;
                    }
                    else if (bits == 24)
                    {
                        int v24 = littleEndian
                            ? (b[p] | (b[p + 1] << 8) | (b[p + 2] << 16))
                            : ((b[p] << 16) | (b[p + 1] << 8) | b[p + 2]);
                        if ((v24 & 0x800000) != 0) v24 |= unchecked((int)0xFF000000);
                        v = v24 / 8388608f;
                    }
                    else if (bits == 32)
                    {
                        int s = littleEndian ? LE32(b, p) : BE32(b, p);
                        v = s / 2147483648f;
                    }
                    else throw new InvalidDataException("unsupported AIFF bit depth " + bits);
                    ch[c][i] = v;
                }
            }

            WavData d = new WavData();
            d.SampleRate = (int)Math.Round(rate);
            d.Channels = channels;
            d.Samples = ch;
            d.Frames = frames;
            d.FormatDescription = "AIFF (" + compression.Trim() + ") " + bits + "-bit";
            return d;
        }

        // ------------------------------- CAF -------------------------------

        private static WavData ReadCaf(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            int pos = 8;    // "caff" + version + flags
            int channels = 0, bits = 0;
            double rate = 0;
            bool isFloat = false, bigEndian = false;
            string formatId = "";
            int dataOffset = -1, dataLength = 0;

            while (pos + 12 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, pos, 4);
                long size = BE64(b, pos + 4);
                int body = pos + 12;
                if (size < 0 || size > int.MaxValue || body + size > b.Length) size = b.Length - body;

                if (id == "desc" && size >= 32)
                {
                    rate = BitConverter.ToDouble(BigEndianBytes(b, body, 8), 0);
                    formatId = Encoding.ASCII.GetString(b, body + 8, 4);
                    int flags = BE32(b, body + 12);
                    int bytesPerPacket = BE32(b, body + 16);
                    int framesPerPacket = BE32(b, body + 20);
                    // CAF desc: rate(8) formatID(4) flags(4) bytesPerPacket(4) framesPerPacket(4)
                    //            channelsPerFrame(4) bitsPerChannel(4) = 32 bytes
                    if (size >= 32)
                    {
                        channels = BE32(b, body + 24);
                        bits = BE32(b, body + 28);
                    }
                    isFloat = (flags & 1) != 0;
                    bigEndian = (flags & 2) != 0;
                    if (framesPerPacket == 0 && bytesPerPacket == 0) framesPerPacket = 1;
                }
                else if (id == "data")
                {
                    dataOffset = body + 4;          // skip the 4-byte edit count
                    dataLength = (int)size - 4;
                }
                pos = body + (int)size;
                if (dataOffset >= 0 && channels > 0) break;
            }

            if (channels <= 0 || rate <= 0 || dataOffset < 0)
                throw new InvalidDataException("not a readable CAF file");
            if (formatId != "lpcm")
                throw new InvalidDataException("CAF payload is '" + formatId + "'; only uncompressed lpcm is read directly");

            int bytesPerSample = isFloat ? (bits / 8) : (bits / 8);
            if (bytesPerSample <= 0) throw new InvalidDataException("unsupported CAF bit depth");
            int frameBytes = bytesPerSample * channels;
            int frames = Math.Min(dataLength, b.Length - dataOffset) / frameBytes;
            if (frames <= 0) throw new InvalidDataException("CAF contains no audio frames");

            float[][] ch = new float[channels][];
            for (int c = 0; c < channels; c++) ch[c] = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                int o = dataOffset + i * frameBytes;
                for (int c = 0; c < channels; c++)
                {
                    int p = o + c * bytesPerSample;
                    float v;
                    if (isFloat)
                    {
                        int raw = bigEndian ? BE32(b, p) : LE32(b, p);
                        v = BitConverter.ToSingle(BitConverter.GetBytes(raw), 0);
                    }
                    else if (bits == 8) v = (sbyte)b[p] / 128f;
                    else if (bits == 16)
                    {
                        short s = bigEndian ? (short)BE16(b, p) : (short)LE16(b, p);
                        v = s / 32768f;
                    }
                    else if (bits == 24)
                    {
                        int v24 = bigEndian
                            ? ((b[p] << 16) | (b[p + 1] << 8) | b[p + 2])
                            : (b[p] | (b[p + 1] << 8) | (b[p + 2] << 16));
                        if ((v24 & 0x800000) != 0) v24 |= unchecked((int)0xFF000000);
                        v = v24 / 8388608f;
                    }
                    else if (bits == 32)
                    {
                        int s = bigEndian ? BE32(b, p) : LE32(b, p);
                        v = s / 2147483648f;
                    }
                    else throw new InvalidDataException("unsupported CAF bit depth " + bits);
                    ch[c][i] = v;
                }
            }

            WavData d = new WavData();
            d.SampleRate = (int)Math.Round(rate);
            d.Channels = channels;
            d.Samples = ch;
            d.Frames = frames;
            d.FormatDescription = "CAF LPCM " + bits + "-bit";
            return d;
        }

        // ------------------------------- AU -------------------------------

        private static WavData ReadAu(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            int dataOffset = BE32(b, 4);
            int dataSize = BE32(b, 8);
            int encoding = BE32(b, 12);
            int rate = BE32(b, 16);
            int channels = BE32(b, 20);
            if (channels <= 0 || rate <= 0 || dataOffset < 0) throw new InvalidDataException("not a readable AU file");

            int available = (dataSize > 0 ? Math.Min(dataSize, b.Length - dataOffset) : b.Length - dataOffset);
            int bytesPerSample;
            switch (encoding)
            {
                case 1: case 27: bytesPerSample = 1; break;   // Âµ-law / A-law
                case 2: bytesPerSample = 1; break;
                case 3: bytesPerSample = 2; break;
                case 4: bytesPerSample = 3; break;
                case 5: case 6: bytesPerSample = 4; break;
                case 7: bytesPerSample = 8; break;
                default: throw new InvalidDataException("unsupported AU encoding " + encoding);
            }
            int frameBytes = bytesPerSample * channels;
            int frames = available / frameBytes;
            if (frames <= 0) throw new InvalidDataException("AU contains no audio frames");

            float[][] ch = new float[channels][];
            for (int c = 0; c < channels; c++) ch[c] = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                int o = dataOffset + i * frameBytes;
                for (int c = 0; c < channels; c++)
                {
                    int p = o + c * bytesPerSample;
                    float v;
                    switch (encoding)
                    {
                        case 1: v = MuLaw(b[p]); break;
                        case 27: v = ALaw(b[p]); break;
                        case 2: v = (sbyte)b[p] / 128f; break;
                        case 3: v = (short)BE16(b, p) / 32768f; break;
                        case 4:
                            int v24 = (b[p] << 16) | (b[p + 1] << 8) | b[p + 2];
                            if ((v24 & 0x800000) != 0) v24 |= unchecked((int)0xFF000000);
                            v = v24 / 8388608f;
                            break;
                        case 5: v = BE32(b, p) / 2147483648f; break;
                        case 6: v = BitConverter.ToSingle(BigEndianBytes(b, p, 4), 0); break;
                        default: v = (float)BitConverter.ToDouble(BigEndianBytes(b, p, 8), 0); break;
                    }
                    ch[c][i] = v;
                }
            }

            WavData d = new WavData();
            d.SampleRate = rate;
            d.Channels = channels;
            d.Samples = ch;
            d.Frames = frames;
            d.FormatDescription = "AU/SND encoding " + encoding;
            return d;
        }

        // ------------------------------- helpers -------------------------------

        internal static float MuLaw(byte u)
        {
            u = (byte)~u;
            int sign = u & 0x80;
            int exponent = (u >> 4) & 0x07;
            int mantissa = u & 0x0F;
            int sample = ((mantissa << 3) + 0x84) << exponent;
            sample -= 0x84;
            return (sign != 0 ? -sample : sample) / 32768f;
        }

        internal static float ALaw(byte a)
        {
            a ^= 0x55;
            int sign = a & 0x80;
            int exponent = (a >> 4) & 0x07;
            int mantissa = a & 0x0F;
            int sample = exponent == 0 ? (mantissa << 4) + 8 : ((mantissa << 4) + 0x108) << (exponent - 1);
            return (sign != 0 ? sample : -sample) / 32768f;
        }

        private static int BE16(byte[] b, int i) { return (b[i] << 8) | b[i + 1]; }
        private static int LE16(byte[] b, int i) { return b[i] | (b[i + 1] << 8); }
        private static int BE32(byte[] b, int i) { return (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]; }
        private static int LE32(byte[] b, int i) { return b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24); }

        private static long BE64(byte[] b, int i)
        {
            long v = 0;
            for (int k = 0; k < 8; k++) v = (v << 8) | b[i + k];
            return v;
        }

        private static byte[] BigEndianBytes(byte[] b, int i, int n)
        {
            byte[] r = new byte[n];
            for (int k = 0; k < n; k++) r[n - 1 - k] = b[i + k];
            return r;
        }

        /// <summary>IEEE 754 80-bit extended, as used by AIFF for the sample rate.</summary>
        private static double Extended80(byte[] b, int i)
        {
            int exponent = ((b[i] & 0x7F) << 8) | b[i + 1];
            ulong mantissa = 0;
            for (int k = 0; k < 8; k++) mantissa = (mantissa << 8) | b[i + 2 + k];
            if (exponent == 0 && mantissa == 0) return 0;
            double value = (double)mantissa * Math.Pow(2.0, exponent - 16383 - 63);
            return (b[i] & 0x80) != 0 ? -value : value;
        }
    }
}


