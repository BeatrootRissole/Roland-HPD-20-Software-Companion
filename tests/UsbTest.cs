using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hpd20Ctrl
{
    /// <summary>Checks the USB export layout against the rules in the HPD-20 manual.</summary>
    public static class UsbSelfTest
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

            string root = Path.Combine(Path.GetTempPath(), "hpd20-usbtest", "STICK");
            if (Directory.Exists(Path.GetDirectoryName(root))) Directory.Delete(Path.GetDirectoryName(root), true);
            Directory.CreateDirectory(root);

            // three compliant sample files, one with a deliberately awkward name
            string[] names = { "Kick", "Snare (tight)", "Größe Ätzend" };
            List<MainForm.ExportItem> items = new List<MainForm.ExportItem>();
            string srcDir = Path.Combine(Path.GetTempPath(), "hpd20-usbtest", "src");
            Directory.CreateDirectory(srcDir);
            for (int i = 0; i < names.Length; i++)
            {
                float[][] ch = new float[1][];
                ch[0] = new float[4410];
                for (int k = 0; k < 4410; k++) ch[0][k] = (float)(0.3 * Math.Sin(2 * Math.PI * (200 + i * 100) * k / 44100.0));
                string p = Path.Combine(srcDir, MainForm.SanitizeFileName(names[i]) + ".wav");
                WavFile.Write16(p, ch, 44100);
                MainForm.ExportItem it = new MainForm.ExportItem();
                it.OutputPath = p;
                it.Source = names[i] + ".wav";
                it.Seconds = 0.1;
                items.Add(it);
            }

            string error;
            int n = MainForm.WriteUsbExport(root, "Test Pack", items, out error);
            check("export returned no error", error == null);
            check("export wrote 3 files", n == 3);

            string[] rootWavs = Directory.GetFiles(root, "*.WAV", SearchOption.TopDirectoryOnly);
            check("3 WAVs in the root", rootWavs.Length == 3);
            check("root files are not in a subfolder", rootWavs.Length > 0 && Path.GetDirectoryName(rootWavs[0]).TrimEnd('\\') == root.TrimEnd('\\'));

            bool naming = true, ascii = true;
            foreach (string f in rootWavs)
            {
                string bn = Path.GetFileName(f);
                if (!System.Text.RegularExpressions.Regex.IsMatch(bn, @"^\d{3}_[A-Za-z0-9_\- ()]{1,20}\.WAV$")) naming = false;
                foreach (char c in bn) if (c > 127) ascii = false;
            }
            check("root names are NNN_name.WAV", naming);
            check("root names are pure ASCII", ascii);
            string joined = string.Join(" ", Array.ConvertAll(rootWavs, Path.GetFileName));
            log.AppendLine("      root: " + joined);

            string archive = Path.Combine(root, "HPD20_Samples", "Test Pack");
            check("organised folder created", Directory.Exists(archive));
            check("archive holds 3 wavs", Directory.Exists(archive) && Directory.GetFiles(archive, "*.wav").Length == 3);
            check("index.csv written", File.Exists(Path.Combine(archive, "index.csv")));
            check("READ-ME written", File.Exists(Path.Combine(archive, "READ-ME.txt")));
            if (File.Exists(Path.Combine(archive, "index.csv")))
            {
                string[] lines = File.ReadAllLines(Path.Combine(archive, "index.csv"));
                check("index has header + 3 rows", lines.Length == 4);
            }

            // every root WAV must be a valid 44.1 kHz 16-bit file the unit will accept
            bool allValid = true;
            foreach (string f in rootWavs)
            {
                try
                {
                    WavData d = WavFile.Read(f);
                    if (d.SampleRate != 44100 || !d.FormatDescription.Contains("16")) allValid = false;
                }
                catch { allValid = false; }
            }
            check("every exported WAV is 44.1 kHz 16-bit", allValid);

            // a second pack must not overwrite the first, and must add to the root numbering
            MainForm.ExportItem extra = new MainForm.ExportItem();
            extra.OutputPath = items[0].OutputPath;
            extra.Source = "Kick again.wav";
            extra.Seconds = 0.1;
            List<MainForm.ExportItem> second = new List<MainForm.ExportItem>();
            second.Add(extra);
            int n2 = MainForm.WriteUsbExport(root, "Second", second, out error);
            check("second export works", error == null && n2 == 1);
            check("both pack folders exist", Directory.Exists(archive) && Directory.Exists(Path.Combine(root, "HPD20_Samples", "Second")));
            check("root now has 4 wavs", Directory.GetFiles(root, "*.WAV").Length == 4);

            log.AppendLine();
            log.AppendLine(failures == 0 ? "USB CHECKS PASSED" : failures + " USB CHECK(S) FAILED");
            Console.Write(log.ToString());
            if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, log.ToString(), Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }
    }
}
