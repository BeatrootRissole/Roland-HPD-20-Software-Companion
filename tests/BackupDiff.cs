using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hpd20Ctrl
{
    /// <summary>Compares two backup images and reports what changed, in terms of the known layout.</summary>
    public static class BackupDiff
    {
        public static int Run(string pathA, string pathB, string reportPath)
        {
            StringBuilder log = new StringBuilder();
            if (!File.Exists(pathA) || !File.Exists(pathB))
            {
                log.AppendLine("SKIP  one of the two files is missing:");
                log.AppendLine("      A: " + pathA + (File.Exists(pathA) ? "" : "  (not found)"));
                log.AppendLine("      B: " + pathB + (File.Exists(pathB) ? "" : "  (not found)"));
                Write(log, reportPath);
                return 0;
            }
            string errA, errB;
            HpdBackup a = HpdBackup.Load(pathA, out errA);
            HpdBackup b = HpdBackup.Load(pathB, out errB);
            if (a == null || b == null)
            {
                log.AppendLine("cannot load: " + (a == null ? "A: " + errA : "") + (b == null ? " B: " + errB : ""));
                Write(log, reportPath);
                return 1;
            }

            log.AppendLine("A: " + Path.GetFileName(pathA) + "   " + a.MemorySize + " bytes   md5 " + (a.Md5Valid ? "ok" : "BAD"));
            log.AppendLine("B: " + Path.GetFileName(pathB) + "   " + b.MemorySize + " bytes   md5 " + (b.Md5Valid ? "ok" : "BAD"));
            log.AppendLine();

            // ---- header
            DiffText(log, "backup name", a.HeaderName, b.HeaderName);
            DiffText(log, "firmware", a.FirmwareVersion, b.FirmwareVersion);

            // ---- kits and pads, field by field
            int kitChanges = 0, padChanges = 0;
            for (int k = 0; k < HpdBackup.KitCount; k++)
            {
                if (a.GetKitName(k) != b.GetKitName(k))
                { log.AppendLine("kit " + (k + 1).ToString("000") + " name: '" + a.GetKitName(k) + "' -> '" + b.GetKitName(k) + "'"); kitChanges++; }
                if (a.GetKitSubName(k) != b.GetKitSubName(k))
                { log.AppendLine("kit " + (k + 1).ToString("000") + " sub name: '" + a.GetKitSubName(k) + "' -> '" + b.GetKitSubName(k) + "'"); kitChanges++; }
                if (a.GetKitVolume(k) != b.GetKitVolume(k))
                { log.AppendLine("kit " + (k + 1).ToString("000") + " volume: " + a.GetKitVolume(k) + " -> " + b.GetKitVolume(k)); kitChanges++; }
                if (a.GetKitHhVolume(k) != b.GetKitHhVolume(k))
                { log.AppendLine("kit " + (k + 1).ToString("000") + " hh volume: " + a.GetKitHhVolume(k) + " -> " + b.GetKitHhVolume(k)); kitChanges++; }
                if (a.GetKitPadSensitivity(k) != b.GetKitPadSensitivity(k))
                { log.AppendLine("kit " + (k + 1).ToString("000") + " pad sensitivity: " + a.GetKitPadSensitivity(k) + " -> " + b.GetKitPadSensitivity(k)); kitChanges++; }
                if (a.GetKitBalance(k) != b.GetKitBalance(k))
                { log.AppendLine("kit " + (k + 1).ToString("000") + " balance: " + a.GetKitBalance(k) + " -> " + b.GetKitBalance(k)); kitChanges++; }

                for (int p = 0; p < HpdBackup.PadsPerKit; p++)
                {
                    string pad = HpdBackup.PadNames[p];
                    for (int layer = 0; layer < 2; layer++)
                    {
                        string L = layer == 0 ? " A" : " B";
                        if (a.GetPadInstrument(k, p, layer) != b.GetPadInstrument(k, p, layer))
                        { log.AppendLine("kit " + (k + 1).ToString("000") + " " + pad + L + " instrument: " +
                                         InstrumentTable.NameOf(a.GetPadInstrument(k, p, layer)) + " -> " +
                                         InstrumentTable.NameOf(b.GetPadInstrument(k, p, layer))); padChanges++; }
                        if (a.GetPadVolume(k, p, layer) != b.GetPadVolume(k, p, layer))
                        { log.AppendLine("kit " + (k + 1).ToString("000") + " " + pad + L + " volume: " + a.GetPadVolume(k, p, layer) + " -> " + b.GetPadVolume(k, p, layer)); padChanges++; }
                        if (a.GetPadPan(k, p, layer) != b.GetPadPan(k, p, layer))
                        { log.AppendLine("kit " + (k + 1).ToString("000") + " " + pad + L + " pan: " + a.GetPadPan(k, p, layer) + " -> " + b.GetPadPan(k, p, layer)); padChanges++; }
                        if (a.GetPadPitch(k, p, layer) != b.GetPadPitch(k, p, layer))
                        { log.AppendLine("kit " + (k + 1).ToString("000") + " " + pad + L + " pitch: " + a.GetPadPitch(k, p, layer) + " -> " + b.GetPadPitch(k, p, layer)); padChanges++; }
                    }
                    if (a.GetPadMidiNote(k, p) != b.GetPadMidiNote(k, p))
                    { log.AppendLine("kit " + (k + 1).ToString("000") + " " + pad + " midi note: " + a.GetPadMidiNote(k, p) + " -> " + b.GetPadMidiNote(k, p)); padChanges++; }
                    if (a.GetPadTriggerMode(k, p) != b.GetPadTriggerMode(k, p))
                    { log.AppendLine("kit " + (k + 1).ToString("000") + " " + pad + " trigger mode: " + a.GetPadTriggerMode(k, p) + " -> " + b.GetPadTriggerMode(k, p)); padChanges++; }
                }
            }
            for (int c = 0; c < HpdBackup.ChainCount; c++)
                DiffText(log, "chain " + (c + 1), a.GetChainName(c), b.GetChainName(c));

            log.AppendLine();
            log.AppendLine("known-field changes: " + kitChanges + " kit field(s), " + padChanges + " pad field(s)");

            // ---- raw byte differences, mapped to regions
            int n = Math.Min(a.MemorySize, b.MemorySize);
            List<int> diff = new List<int>();
            for (int i = 0; i < n; i++) if (a.GetByte(i) != b.GetByte(i)) diff.Add(i);
            log.AppendLine("differing bytes in the image: " + diff.Count + " of " + n);

            List<string> ranges = new List<string>();
            if (diff.Count > 0)
            {
                int start = diff[0], prev = diff[0];
                for (int i = 1; i < diff.Count; i++)
                {
                    if (diff[i] == prev + 1) { prev = diff[i]; continue; }
                    ranges.Add(Describe(a, b, start, prev));
                    start = prev = diff[i];
                }
                ranges.Add(Describe(a, b, start, prev));
            }
            foreach (string r in ranges) log.AppendLine("  " + r);

            Write(log, reportPath);
            return 0;
        }

        private static void DiffText(StringBuilder log, string label, string a, string b)
        {
            if (a != b) log.AppendLine(label + ": '" + a + "' -> '" + b + "'");
        }

        private static string Describe(HpdBackup a, HpdBackup b, int lo, int hi)
        {
            string region;
            if (hi < 0x28) region = "header (name / firmware)";
            else if (hi < HpdBackup.ChainIndex) region = "system settings (undecoded)";
            else if (lo >= HpdBackup.KitIndex && hi < HpdBackup.KitIndex + HpdBackup.KitCount * HpdBackup.KitSize)
            {
                int k = (lo - HpdBackup.KitIndex) / HpdBackup.KitSize;
                region = "kit " + (k + 1) + " block";
            }
            else if (lo >= HpdBackup.PadIndex && hi < HpdBackup.PadIndex + HpdBackup.PadCount * HpdBackup.PadSize)
            {
                int rel = lo - HpdBackup.PadIndex;
                int padIdx = rel / HpdBackup.PadSize;
                region = "kit " + (padIdx / HpdBackup.PadsPerKit + 1) + " pad " +
                         HpdBackup.PadNames[padIdx % HpdBackup.PadsPerKit];
            }
            else if (lo >= HpdBackup.ChainIndex && hi < HpdBackup.ChainIndex + HpdBackup.ChainCount * HpdBackup.ChainSize)
                region = "chain block";
            else if (lo >= 0xC1C && hi < HpdBackup.KitIndex)
                region = "system settings, second block (undecoded)";
            else
                region = "unmapped area";

            StringBuilder sb = new StringBuilder();
            sb.Append(string.Format("0x{0:X6}..0x{1:X6}  ({2} byte{3})  {4}", lo, hi, hi - lo + 1, hi == lo ? "" : "s", region));
            sb.Append("   ");
            int shown = 0;
            for (int i = lo; i <= hi && shown < 8; i++, shown++)
            {
                int x = a.GetByte(i), y = b.GetByte(i);
                if (x >= 32 && x < 127 && y >= 32 && y < 127)
                    sb.Append(string.Format("'{0}'->'{1}' ", (char)x, (char)y));
                else
                    sb.Append(string.Format("{0:X2}->{1:X2} ", x, y));
            }
            if (hi - lo + 1 > shown) sb.Append("...");
            return sb.ToString();
        }

        private static void Write(StringBuilder log, string reportPath)
        {
            Console.Write(log.ToString());
            if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, log.ToString(), Encoding.UTF8);
        }
    }
}


