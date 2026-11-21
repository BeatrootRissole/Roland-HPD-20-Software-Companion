using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Hpd20Ctrl
{
    /// <summary>Structural checks for the backup reader/writer.</summary>
    public static class BackupTest
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

            string dir = Path.Combine(Path.GetTempPath(), "hpd20-backuptest");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "test.bin");

            // documented offsets
            check("kit block at 6922", HpdBackup.KitIndex == 6922);
            check("pad block at 51596", HpdBackup.PadIndex == 51596);
            check("224-byte kits / 200 kits", HpdBackup.KitSize == 224 && HpdBackup.KitCount == 200);
            check("68-byte pads / 17 per kit", HpdBackup.PadSize == 68 && HpdBackup.PadsPerKit == 17);
            check("minimum size matches layout", HpdBackup.MinimumSize == 51596 + 200 * 17 * 68 + 16);

            // build a file with a little extra tail area, like a real image
            HpdBackup b = HpdBackup.Create(HpdBackup.MinimumSize + 4096);
            check("created image size", b.MemorySize == HpdBackup.MinimumSize + 4096);

            b.SetKitName(0, "TEST KIT");
            b.SetKitSubName(0, "Sub Name");
            b.SetKitVolume(0, 100);
            b.SetKitHhVolume(0, 90);
            b.SetKitPadSensitivity(0, -10);
            b.SetKitBalance(0, 5);

            b.SetPadInstrument(0, 0, 0, 5);
            b.SetPadPitch(0, 0, 0, -1200);
            b.SetPadVolume(0, 0, 0, 80);
            b.SetPadPan(0, 0, 0, -20);
            b.SetPadAmbient(0, 0, 0, 30);
            b.SetPadMuffling(0, 0, 0, 40);
            b.SetPadSweep(0, 0, 0, -7);
            b.SetPadMidiNote(0, 0, 60);
            b.SetPadMidiGate(0, 0, 25);
            b.SetPadTriggerMode(0, 0, 1);
            b.SetPadMonoPoly(0, 0, 1);
            b.SetPadMuteGroup(0, 0, 3);
            b.SetPadFixedVelocity(0, 0, 99);
            b.SetPadVelocityFade(0, 0, 12);
            b.SetPadLayerMode(0, 0, 2);

            // layer B holds a different instrument
            b.SetPadInstrument(0, 0, 1, 42);
            b.SetPadVolume(0, 0, 1, 64);

            b.SetKitName(199, "LAST KIT");
            b.Save(file);

            byte[] raw = File.ReadAllBytes(file);
            check("file = image + 16-byte trailer", raw.Length == b.MemorySize + 16);
            byte[] computed = MD5.Create().ComputeHash(Sub(raw, 0, raw.Length - 16));
            check("trailer is MD5 of the image", Same(computed, Sub(raw, raw.Length - 16, 16)));

            // reload and verify every field survived
            string error;
            HpdBackup r = HpdBackup.Load(file, out error);
            check("reload succeeds", r != null);
            if (r != null)
            {
                check("md5 validated on load", r.Md5Valid);
                check("kit name", r.GetKitName(0) == "TEST KIT");
                check("kit sub name", r.GetKitSubName(0) == "Sub Name");
                check("kit volume", r.GetKitVolume(0) == 100);
                check("hh volume", r.GetKitHhVolume(0) == 90);
                check("pad sensitivity (-10)", r.GetKitPadSensitivity(0) == -10);
                check("balance (5)", r.GetKitBalance(0) == 5);
                check("pad instrument (5)", r.GetPadInstrument(0, 0, 0) == 5);
                check("pad instrument layer B (42)", r.GetPadInstrument(0, 0, 1) == 42);
                check("pad pitch (-1200)", r.GetPadPitch(0, 0, 0) == -1200);
                check("pad volume (80)", r.GetPadVolume(0, 0, 0) == 80);
                check("pad pan (-20)", r.GetPadPan(0, 0, 0) == -20);
                check("pad ambient (30)", r.GetPadAmbient(0, 0, 0) == 30);
                check("pad muffling (40)", r.GetPadMuffling(0, 0, 0) == 40);
                check("pad sweep (-7)", r.GetPadSweep(0, 0, 0) == -7);
                check("midi note (60)", r.GetPadMidiNote(0, 0) == 60);
                check("midi gate (25)", r.GetPadMidiGate(0, 0) == 25);
                check("trigger mode (1)", r.GetPadTriggerMode(0, 0) == 1);
                check("mono/poly (1)", r.GetPadMonoPoly(0, 0) == 1);
                check("mute group (3)", r.GetPadMuteGroup(0, 0) == 3);
                check("fixed velocity (99)", r.GetPadFixedVelocity(0, 0) == 99);
                check("velocity fade (12)", r.GetPadVelocityFade(0, 0) == 12);
                check("layer mode (2)", r.GetPadLayerMode(0, 0) == 2);
                check("last kit name", r.GetKitName(199) == "LAST KIT");
                check("untouched kit is empty", r.GetKitName(5) == "");
            }

            // long name handling: 12 chars exactly, then truncation on read
            HpdBackup b2 = HpdBackup.Create(HpdBackup.MinimumSize);
            b2.SetKitName(0, "ABCDEFGHIJKL");
            b2.SetKitSubName(0, "1234567890123456");
            string f2 = Path.Combine(dir, "names.bin");
            b2.Save(f2);
            HpdBackup r2 = HpdBackup.Load(f2, out error);
            check("12-char name round trip", r2 != null && r2.GetKitName(0) == "ABCDEFGHIJKL");
            check("16-char sub name round trip", r2 != null && r2.GetKitSubName(0) == "1234567890123456");

            // corrupted payload must be reported
            byte[] bad = File.ReadAllBytes(file);
            bad[HpdBackup.KitIndex + 2] = (byte)'X';
            string f3 = Path.Combine(dir, "corrupt.bin");
            File.WriteAllBytes(f3, bad);
            HpdBackup r3 = HpdBackup.Load(f3, out error);
            check("corrupt file is flagged", r3 != null && !r3.Md5Valid);

            // truncated file must be refused
            byte[] cut = Sub(raw, 0, 1000);
            string f4 = Path.Combine(dir, "short.bin");
            File.WriteAllBytes(f4, cut);
            HpdBackup r4 = HpdBackup.Load(f4, out error);
            check("short file refused", r4 == null && !string.IsNullOrEmpty(error));

            // an edited file must get a new valid MD5
            if (r != null)
            {
                r.SetKitName(1, "CHANGED");
                string f5 = Path.Combine(dir, "resaved.bin");
                r.Save(f5);
                HpdBackup r5 = HpdBackup.Load(f5, out error);
                check("resaved md5 is valid", r5 != null && r5.Md5Valid);
                check("resaved value persisted", r5 != null && r5.GetKitName(1) == "CHANGED");
            }

            log.AppendLine();
            log.AppendLine(failures == 0 ? "BACKUP CHECKS PASSED" : failures + " BACKUP CHECK(S) FAILED");
            Console.Write(log.ToString());
            if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, log.ToString(), Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }

        private static byte[] Sub(byte[] a, int start, int len)
        {
            byte[] r = new byte[len];
            Array.Copy(a, start, r, 0, len);
            return r;
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    /// <summary>Validates a real backup file (e.g. Roland's factory image) against this implementation.</summary>
    public static class BackupCheck
    {
        public static int Run(string path, string reportPath)
        {
            StringBuilder log = new StringBuilder();
            int failures = 0;
            Action<string, bool> check = delegate(string name, bool ok)
            {
                if (!ok) failures++;
                log.AppendLine((ok ? "PASS  " : "FAIL  ") + name);
            };

            string error;
            if (!File.Exists(path))
            {
                log.AppendLine("SKIP  " + path + " not found - plug the stick in or pass a local copy");
                log.AppendLine();
                log.AppendLine("REAL BACKUP CHECKS SKIPPED");
                Console.Write(log.ToString());
                if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, log.ToString(), Encoding.UTF8);
                return 0;
            }
            HpdBackup b = HpdBackup.Load(path, out error);
            check("file loads", b != null);
            if (b == null)
            {
                log.AppendLine("  " + error);
                Write(log, reportPath, failures);
                return 1;
            }

            log.AppendLine("  file      " + Path.GetFileName(path));
            log.AppendLine("  size      " + b.MemorySize + " bytes + 16");
            log.AppendLine("  header    name='" + b.HeaderName + "'  firmware='" + b.FirmwareVersion + "'");
            check("MD5 trailer validates", b.Md5Valid);
            check("header name present", b.HeaderName.Length > 0);
            check("firmware string present", b.FirmwareVersion.Length > 0);

            // kits
            int named = 0;
            for (int i = 0; i < HpdBackup.KitCount; i++) if (b.GetKitName(i).Length > 0) named++;
            log.AppendLine("  kits      " + named + " of " + HpdBackup.KitCount + " have names");
            check("kit block decodes (>=1 named kit)", named >= 1);
            check("first kit name is printable", IsPrintable(b.GetKitName(0)));
            check("first kit sub name is printable", IsPrintable(b.GetKitSubName(0)));

            // kit 1 pad note numbers: factory map M1..M5 = 60..64
            bool notes = true;
            for (int p = 0; p < 5; p++) if (b.GetPadMidiNote(0, p) != 60 + p) notes = false;
            check("kit 1 M1..M5 notes are 60..64", notes);

            // pad instruments should be inside the instrument table
            bool instruments = true;
            int maxInst = 0;
            for (int k = 0; k < 5; k++)
                for (int p = 0; p < HpdBackup.PadsPerKit; p++)
                {
                    int inst = b.GetPadInstrument(k, p, 0);
                    if (inst > maxInst) maxInst = inst;
                    if (inst > 2000) instruments = false;
                }
            log.AppendLine("  highest instrument number in first 5 kits: " + maxInst);
            check("pad instruments are sane", instruments);

            // chains
            int chainsNamed = 0;
            for (int i = 0; i < HpdBackup.ChainCount; i++)
            {
                string n = b.GetChainName(i);
                if (n.Length > 0 && IsPrintable(n)) chainsNamed++;
            }
            log.AppendLine("  chains    " + chainsNamed + " of " + HpdBackup.ChainCount + " have names, first='" + b.GetChainName(0) + "'");
            check("chain names decode", chainsNamed >= 1);

            // byte-identical round trip: our writer must not disturb a single byte
            string copy = Path.Combine(Path.GetTempPath(), "hpd20-roundtrip.HS0");
            b.Save(copy);
            byte[] a = File.ReadAllBytes(path);
            byte[] c = File.ReadAllBytes(copy);
            bool identical = a.Length == c.Length;
            if (identical) for (int i = 0; i < a.Length; i++) if (a[i] != c[i]) { identical = false; break; }
            check("save reproduces the file byte for byte", identical);

            // edit â†’ save â†’ reload
            HpdBackup m = HpdBackup.Load(path, out error);
            string originalName = m.GetKitName(0);
            m.SetKitName(0, "RNDTRIP");
            string edited = Path.Combine(Path.GetTempPath(), "hpd20-edited.HS0");
            m.Save(edited);
            HpdBackup e = HpdBackup.Load(edited, out error);
            check("edited file reloads with a valid MD5", e != null && e.Md5Valid);
            check("edited kit name persists", e != null && e.GetKitName(0) == "RNDTRIP");
            check("edit does not disturb other bytes", e != null && SameRegion(b, e, 0, HpdBackup.KitIndex));
            check("original kit name recoverable", originalName.Length > 0);

            Write(log, reportPath, failures);
            try { File.Delete(copy); File.Delete(edited); } catch { }
            return failures == 0 ? 0 : 1;
        }

        private static void Write(StringBuilder log, string reportPath, int failures)
        {
            log.AppendLine();
            log.AppendLine(failures == 0 ? "REAL BACKUP CHECKS PASSED" : failures + " REAL BACKUP CHECK(S) FAILED");
            Console.Write(log.ToString());
            if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, log.ToString(), Encoding.UTF8);
        }

        private static bool IsPrintable(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (c < 32 || c > 126) return false;
            return true;
        }

        private static bool SameRegion(HpdBackup a, HpdBackup b, int start, int end)
        {
            for (int i = start; i < end && i < a.MemorySize && i < b.MemorySize; i++)
                if (a.GetByte(i) != b.GetByte(i)) return false;
            return true;
        }
    }
}

