using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hpd20Ctrl
{
    /// <summary>
    /// Cross-platform command line front end. Built with the .NET SDK on Windows, Linux or macOS; the
    /// GUI in this repository is Windows-only because it is WinForms and talks to winmm.
    /// </summary>
    public static class Cli
    {
        /// <summary>Kept in step with CHANGELOG.md.</summary>
        public const string Version = "hpd20cli 1.6.2";

        public static int Main(string[] args)
        {
            if (args == null || args.Length == 0) { Usage(); return 2; }

            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "convert": return Convert(args);
                    case "info": return Info(args);
                    case "backup-info": return BackupInfo(args);
                    case "backup-diff": return BackupDiffCmd(args);
                    case "formats": return Formats();
                    case "version": Console.WriteLine(Cli.Version); return 0;
                    case "-h":
                    case "--help":
                    case "help": Usage(); return 0;
                    default:
                        Console.Error.WriteLine("unknown command: " + args[0]);
                        Usage();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        private static void Usage()
        {
            Console.WriteLine("hpd20cli - HPD-20 sample and backup helper");
            Console.WriteLine();
            Console.WriteLine("  convert <file...> --out <dir> [options]   resample to 44.1 kHz / 16-bit WAV");
            Console.WriteLine("      --mono          downmix to mono");
            Console.WriteLine("      --normalise     raise the peak to -0.2 dBFS");
            Console.WriteLine("      --trim          trim leading/trailing silence");
            Console.WriteLine("      --fade          add 5 ms fades at both ends");
            Console.WriteLine("      --max-min N     truncate at N minutes (default 6)");
            Console.WriteLine("  info <file>                     show format, rate, channels and duration");
            Console.WriteLine("  backup-info <file.HS0>          show header, firmware and kit names");
            Console.WriteLine("  backup-diff <a.HS0> <b.HS0>     report what differs between two images");
            Console.WriteLine("  formats                         list the formats this build can read");
            Console.WriteLine("  version                         print the version");
            Console.WriteLine();
            Console.WriteLine("Uncompressed WAV, AIFF/AIFF-C, CAF and AU are read everywhere.");
            Console.WriteLine("MP3, FLAC, AAC and WMA are read on Windows (Media Foundation);");
            Console.WriteLine("on other systems convert them first or use the Windows app.");
        }

        private static int Formats()
        {
            Console.WriteLine("readable here:");
            foreach (string ext in AudioFormats.Extensions)
            {
                bool compressed = ext == "mp3" || ext == "flac" || ext == "ogg" || ext == "oga" || ext == "opus"
                               || ext == "m4a" || ext == "aac" || ext == "wma" || ext == "alac" || ext == "mp4";
                string how = compressed ? " Windows Media Foundation only" : " built in";
                Console.WriteLine("  ." + ext.PadRight(6) + how);
            }
            return 0;
        }

        private static int Info(string[] args)
        {
            if (args.Length < 2) { Console.Error.WriteLine("usage: info <file>"); return 2; }
            WavData d = AudioFormats.Read(args[1]);
            Console.WriteLine("file      " + Path.GetFileName(args[1]));
            Console.WriteLine("format    " + d.FormatDescription);
            Console.WriteLine("rate      " + d.SampleRate + " Hz");
            Console.WriteLine("channels  " + d.Channels);
            Console.WriteLine("frames    " + d.Frames);
            Console.WriteLine("length    " + d.DurationSeconds.ToString("0.000") + " s");

            double peak = 0;
            for (int c = 0; c < d.Channels; c++)
                for (int i = 0; i < d.Frames; i++)
                {
                    double a = Math.Abs(d.Samples[c][i]);
                    if (a > peak) peak = a;
                }
            double db = peak > 0 ? 20 * Math.Log10(peak) : -120;
            Console.WriteLine("peak      " + db.ToString("0.0") + " dBFS");
            return 0;
        }

        private static int Convert(string[] args)
        {
            List<string> inputs = new List<string>();
            string outDir = null;
            ResampleOptions opts = new ResampleOptions();
            double maxMin = 6.0;

            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--out": if (i + 1 < args.Length) outDir = args[++i]; break;
                    case "--mono": opts.Mono = true; break;
                    case "--normalise":
                    case "--normalize": opts.Normalize = true; break;
                    case "--trim": opts.TrimSilence = true; break;
                    case "--fade": opts.FadeEdges = true; break;
                    case "--max-min": if (i + 1 < args.Length) double.TryParse(args[++i], out maxMin); break;
                    default: inputs.Add(a); break;
                }
            }
            if (inputs.Count == 0 || string.IsNullOrEmpty(outDir))
            {
                Console.Error.WriteLine("usage: convert <file...> --out <dir> [--mono] [--normalise] [--trim] [--fade] [--max-min N]");
                return 2;
            }

            opts.MaxSeconds = maxMin * 60.0;
            Directory.CreateDirectory(outDir);
            int failures = 0;
            foreach (string input in inputs)
            {
                string name = Path.GetFileNameWithoutExtension(input);
                string outPath = Path.Combine(outDir, name + ".wav");
                SampleResult r = SampleProcessor.Process(input, outPath, opts);
                if (r.Ok)
                    Console.WriteLine("ok    " + Path.GetFileName(input) + " -> " + Path.GetFileName(outPath) + "  " + r.Message);
                else
                {
                    Console.WriteLine("FAIL  " + Path.GetFileName(input) + "  " + r.Message);
                    failures++;
                }
            }
            return failures == 0 ? 0 : 1;
        }

        private static int BackupInfo(string[] args)
        {
            if (args.Length < 2) { Console.Error.WriteLine("usage: backup-info <file.HS0>"); return 2; }
            string error;
            HpdBackup b = HpdBackup.Load(args[1], out error);
            if (b == null) { Console.Error.WriteLine("cannot read: " + error); return 1; }

            Console.WriteLine("file      " + Path.GetFileName(args[1]));
            Console.WriteLine("size      " + b.MemorySize + " bytes");
            Console.WriteLine("md5       " + (b.Md5Valid ? "ok" : "MISMATCH"));
            Console.WriteLine("name      " + b.HeaderName);
            Console.WriteLine("firmware  " + b.FirmwareVersion);
            int named = 0;
            for (int i = 0; i < HpdBackup.KitCount; i++) if (b.GetKitName(i).Trim().Length > 0) named++;
            Console.WriteLine("kits      " + named + " named of " + HpdBackup.KitCount);
            Console.WriteLine();
            Console.WriteLine("first 10 kits:");
            for (int i = 0; i < 10; i++)
                Console.WriteLine("  " + (i + 1).ToString("000") + "  " + b.GetKitName(i) + (b.GetKitSubName(i).Length > 0 ? "  (" + b.GetKitSubName(i) + ")" : ""));
            return 0;
        }

        private static int BackupDiffCmd(string[] args)
        {
            if (args.Length < 3) { Console.Error.WriteLine("usage: backup-diff <a.HS0> <b.HS0>"); return 2; }
            return BackupDiff.Run(args[1], args[2], null);
        }
    }
}
