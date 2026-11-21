using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Hpd20Ctrl
{
    /// <summary>
    /// Reader/writer for an HPD-20 USB backup file (Roland/HPD-20/Backup).
    ///
    /// Layout is taken from the "hpd20" reference implementation (PyPI, v0.0.9), which reverse
    /// engineered the memory dump:
    ///   [0 .. len-16)  memory image
    ///   [len-16 .. len) MD5 of the memory image
    ///   kits : offset 6922,  224 bytes each, 200 kits
    ///   pads : offset 51596,  68 bytes each, 17 pads per kit (17 x 200)
    ///   chains: offset 1180, 128 bytes each, 15 chains
    /// Integers are big-endian; strings are raw ASCII bytes.
    ///
    /// This has not been verified against a real instrument: always keep the original file.
    /// </summary>
    public sealed class HpdBackup
    {
        public const int KitIndex = 6922;
        public const int KitSize = 224;
        public const int KitCount = 200;

        public const int PadIndex = 51596;
        public const int PadSize = 68;
        public const int PadsPerKit = 17;
        public const int PadCount = KitCount * PadsPerKit;

        public const int ChainIndex = 1180;
        public const int ChainSize = 128;
        public const int ChainCount = 15;

        public const int MinimumSize = PadIndex + PadCount * PadSize + 16;

        private byte[] _mem;          // memory image, without the MD5 trailer
        private byte[] _storedMd5;

        public string SourcePath;
        public bool Md5Valid;
        public bool Modified;

        public static readonly string[] PadNames = new string[]
        {
            "M1", "M2", "M3", "M4", "M5",
            "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8",
            "D-Beam", "Ext Head", "Ext Rim", "HH Ctrl"
        };

        public int MemorySize { get { return _mem.Length; } }
        public byte[] StoredMd5 { get { return _storedMd5; } }
        public byte[] Memory { get { return _mem; } }
        public byte[] CurrentMd5 { get { return ComputeMd5(_mem); } }

        // ------------------------------- load / save -------------------------------

        public static HpdBackup Load(string path, out string error)
        {
            error = null;
            try
            {
                byte[] all = File.ReadAllBytes(path);
                if (all.Length < MinimumSize)
                {
                    error = "File is " + all.Length + " bytes; an HPD-20 backup is at least " + MinimumSize + " bytes.";
                    return null;
                }
                HpdBackup b = new HpdBackup();
                b.SourcePath = path;
                b._mem = new byte[all.Length - 16];
                Array.Copy(all, 0, b._mem, 0, b._mem.Length);
                b._storedMd5 = new byte[16];
                Array.Copy(all, all.Length - 16, b._storedMd5, 0, 16);

                byte[] computed = ComputeMd5(b._mem);
                b.Md5Valid = SameBytes(computed, b._storedMd5);
                if (!b.Md5Valid)
                    error = "MD5 mismatch - the file may be damaged. It will open read-write, but do not load it into the instrument.";
                return b;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>Creates a blank, correctly sized image so a backup can be built from scratch.</summary>
        public static HpdBackup Create(int size)
        {
            if (size < MinimumSize) size = MinimumSize;
            HpdBackup b = new HpdBackup();
            b._mem = new byte[size];
            b._storedMd5 = new byte[16];
            b.Md5Valid = true;
            return b;
        }

        public void Save(string path)
        {
            byte[] md5 = ComputeMd5(_mem);
            using (FileStream fs = File.Create(path))
            {
                fs.Write(_mem, 0, _mem.Length);
                fs.Write(md5, 0, 16);
            }
            _storedMd5 = md5;
            Md5Valid = true;
            Modified = false;
            SourcePath = path;
        }

        private static byte[] ComputeMd5(byte[] data)
        {
            using (MD5 md5 = MD5.Create()) return md5.ComputeHash(data);
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ------------------------------- header / chains -------------------------------

        /// <summary>Backup name as the instrument shows it (offset 8, 10 chars).</summary>
        public string HeaderName { get { return GetString(8, 10); } }

        /// <summary>Firmware version string recorded in the header (offset 0x16, 8 chars).</summary>
        public string FirmwareVersion { get { return GetString(0x16, 8); } }

        public string GetChainName(int chain)
        {
            if (chain < 0 || chain >= ChainCount) throw new ArgumentOutOfRangeException("chain");
            return GetString(ChainIndex + chain * ChainSize, 10);
        }

        public void SetChainName(int chain, string value)
        {
            if (chain < 0 || chain >= ChainCount) throw new ArgumentOutOfRangeException("chain");
            SetString(ChainIndex + chain * ChainSize, 10, value);
            Modified = true;
        }

        // ------------------------------- kits -------------------------------

        private int KitOffset(int kit)
        {
            if (kit < 0 || kit >= KitCount) throw new ArgumentOutOfRangeException("kit");
            return KitIndex + kit * KitSize;
        }

        public string GetKitName(int kit)
        {
            return GetString(KitOffset(kit) + 2, 12);
        }

        public void SetKitName(int kit, string value)
        {
            SetString(KitOffset(kit) + 2, 12, value);
            Modified = true;
        }

        public string GetKitSubName(int kit)
        {
            return GetString(KitOffset(kit) + 14, 16);
        }

        public void SetKitSubName(int kit, string value)
        {
            SetString(KitOffset(kit) + 14, 16, value);
            Modified = true;
        }

        public int GetKitVolume(int kit) { return GetByte(KitOffset(kit) + 30); }
        public void SetKitVolume(int kit, int v) { SetByte(KitOffset(kit) + 30, v); Modified = true; }

        public int GetKitHhVolume(int kit) { return GetByte(KitOffset(kit) + 31); }
        public void SetKitHhVolume(int kit, int v) { SetByte(KitOffset(kit) + 31, v); Modified = true; }

        public int GetKitPadSensitivity(int kit) { return GetSByte(KitOffset(kit) + 55); }
        public void SetKitPadSensitivity(int kit, int v) { SetByte(KitOffset(kit) + 55, v & 0xFF); Modified = true; }

        public int GetKitBalance(int kit) { return GetSByte(KitOffset(kit) + 67); }
        public void SetKitBalance(int kit, int v) { SetByte(KitOffset(kit) + 67, v & 0xFF); Modified = true; }

        // ------------------------------- pads -------------------------------

        private int PadOffset(int kit, int pad)
        {
            if (kit < 0 || kit >= KitCount) throw new ArgumentOutOfRangeException("kit");
            if (pad < 0 || pad >= PadsPerKit) throw new ArgumentOutOfRangeException("pad");
            return PadIndex + (kit * PadsPerKit + pad) * PadSize;
        }

        // per-layer offsets (layer 0 = A, 1 = B)
        public int GetPadVolume(int kit, int pad, int layer) { return GetByte(PadOffset(kit, pad) + 0 + layer); }
        public void SetPadVolume(int kit, int pad, int layer, int v) { SetByte(PadOffset(kit, pad) + 0 + layer, v); Modified = true; }

        public int GetPadAmbient(int kit, int pad, int layer) { return GetByte(PadOffset(kit, pad) + 2 + layer); }
        public void SetPadAmbient(int kit, int pad, int layer, int v) { SetByte(PadOffset(kit, pad) + 2 + layer, v); Modified = true; }

        /// <summary>Instrument number as displayed by the unit (1-based); 0 means no instrument.</summary>
        public int GetPadInstrument(int kit, int pad, int layer)
        {
            int raw = GetUInt16(PadOffset(kit, pad) + 4 + layer * 2);
            return raw + 1;
        }

        public void SetPadInstrument(int kit, int pad, int layer, int number)
        {
            int raw = Math.Max(0, number - 1);
            SetUInt16(PadOffset(kit, pad) + 4 + layer * 2, raw);
            Modified = true;
        }

        public int GetPadInternalInstrument(int kit, int pad, int layer)
        {
            return GetUInt16(PadOffset(kit, pad) + 8 + layer * 2);
        }

        public void SetPadInternalInstrument(int kit, int pad, int layer, int v)
        {
            SetUInt16(PadOffset(kit, pad) + 8 + layer * 2, v);
            Modified = true;
        }

        public int GetPadPitch(int kit, int pad, int layer)
        {
            return GetInt16(PadOffset(kit, pad) + 12 + layer * 2);
        }

        public void SetPadPitch(int kit, int pad, int layer, int cents)
        {
            SetInt16(PadOffset(kit, pad) + 12 + layer * 2, cents);
            Modified = true;
        }

        public int GetPadMuffling(int kit, int pad, int layer) { return GetByte(PadOffset(kit, pad) + 16 + layer); }
        public void SetPadMuffling(int kit, int pad, int layer, int v) { SetByte(PadOffset(kit, pad) + 16 + layer, v); Modified = true; }

        public int GetPadPan(int kit, int pad, int layer) { return GetSByte(PadOffset(kit, pad) + 18 + layer); }
        public void SetPadPan(int kit, int pad, int layer, int v) { SetByte(PadOffset(kit, pad) + 18 + layer, v & 0xFF); Modified = true; }

        public int GetPadColor(int kit, int pad, int layer) { return GetByte(PadOffset(kit, pad) + 23 + layer); }
        public void SetPadColor(int kit, int pad, int layer, int v) { SetByte(PadOffset(kit, pad) + 23 + layer, v); Modified = true; }

        public int GetPadMfxAssign(int kit, int pad, int layer) { return GetByte(PadOffset(kit, pad) + 25 + layer); }
        public void SetPadMfxAssign(int kit, int pad, int layer, int v) { SetByte(PadOffset(kit, pad) + 25 + layer, v); Modified = true; }

        public int GetPadSweep(int kit, int pad, int layer) { return GetSByte(PadOffset(kit, pad) + 27 + layer); }
        public void SetPadSweep(int kit, int pad, int layer, int v) { SetByte(PadOffset(kit, pad) + 27 + layer, v & 0xFF); Modified = true; }

        public int GetPadMidiNote(int kit, int pad) { return GetByte(PadOffset(kit, pad) + 33); }
        public void SetPadMidiNote(int kit, int pad, int v) { SetByte(PadOffset(kit, pad) + 33, v); Modified = true; }

        public int GetPadMidiGate(int kit, int pad) { return GetByte(PadOffset(kit, pad) + 34); }
        public void SetPadMidiGate(int kit, int pad, int v) { SetByte(PadOffset(kit, pad) + 34, v); Modified = true; }

        public int GetPadLayerMode(int kit, int pad) { return GetByte(PadOffset(kit, pad) + 43); }
        public void SetPadLayerMode(int kit, int pad, int v) { SetByte(PadOffset(kit, pad) + 43, v); Modified = true; }

        public int GetPadVelocityFade(int kit, int pad) { return GetByte(PadOffset(kit, pad) + 44); }
        public void SetPadVelocityFade(int kit, int pad, int v) { SetByte(PadOffset(kit, pad) + 44, v); Modified = true; }

        public int GetPadTriggerMode(int kit, int pad) { return GetByte(PadOffset(kit, pad) + 45); }
        public void SetPadTriggerMode(int kit, int pad, int v) { SetByte(PadOffset(kit, pad) + 45, v); Modified = true; }

        public int GetPadFixedVelocity(int kit, int pad) { return GetByte(PadOffset(kit, pad) + 47); }
        public void SetPadFixedVelocity(int kit, int pad, int v) { SetByte(PadOffset(kit, pad) + 47, v); Modified = true; }

        public int GetPadMuteGroup(int kit, int pad) { return GetByte(PadOffset(kit, pad) + 48); }
        public void SetPadMuteGroup(int kit, int pad, int v) { SetByte(PadOffset(kit, pad) + 48, v); Modified = true; }

        public int GetPadMonoPoly(int kit, int pad) { return GetByte(PadOffset(kit, pad) + 49); }
        public void SetPadMonoPoly(int kit, int pad, int v) { SetByte(PadOffset(kit, pad) + 49, v); Modified = true; }

        // ------------------------------- raw access -------------------------------

        public int GetByte(int index) { return _mem[index]; }
        public void SetByte(int index, int value) { _mem[index] = (byte)value; }
        public int GetSByte(int index) { int v = _mem[index]; return v > 127 ? v - 256 : v; }
        public int GetUInt16(int index) { return (_mem[index] << 8) | _mem[index + 1]; }
        public int GetInt16(int index) { int v = GetUInt16(index); return v > 32767 ? v - 65536 : v; }

        public void SetUInt16(int index, int value)
        {
            _mem[index] = (byte)((value >> 8) & 0xFF);
            _mem[index + 1] = (byte)(value & 0xFF);
        }

        public void SetInt16(int index, int value) { SetUInt16(index, value & 0xFFFF); }

        public string GetString(int index, int length)
        {
            StringBuilder sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                byte b = _mem[index + i];
                if (b == 0) break;
                sb.Append(b < 32 || b > 126 ? ' ' : (char)b);
            }
            return sb.ToString().TrimEnd();
        }

        public void SetString(int index, int length, string value)
        {
            // keep whatever padding byte the field already uses (space or zero)
            byte pad = 0x20;
            for (int i = 0; i < length; i++)
            {
                if (_mem[index + i] == 0) { pad = 0; break; }
                if (_mem[index + i] == 0x20) { pad = 0x20; break; }
            }
            if (value == null) value = "";
            for (int i = 0; i < length; i++)
            {
                if (i < value.Length) _mem[index + i] = (byte)(value[i] < 32 || value[i] > 126 ? ' ' : value[i]);
                else _mem[index + i] = pad;
            }
            Modified = true;
        }

        /// <summary>Human readable note for a pad, combining the instrument's base pitch and the pad pitch.</summary>
        public string DescribePadPitch(int kit, int pad, int layer)
        {
            int cents = GetPadPitch(kit, pad, layer);
            int basePitch = InstrumentTable.BasePitchOf(GetPadInstrument(kit, pad, layer));
            int total = cents + basePitch;
            double semitones = total / 100.0;
            return (total > 0 ? "+" : "") + semitones.ToString("0.00") + " st";
        }
    }
}

