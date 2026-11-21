using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Hpd20Ctrl
{
    /// <summary>Exercises a connected HPD-20: identity, a note, a kit change, and a clean teardown.</summary>
    public static class HardwareTest
    {
        public static int Run(string reportPath, string match)
        {
            if (string.IsNullOrEmpty(match)) match = "HPD";

            StringBuilder log = new StringBuilder();
            int failures = 0;
            Action<string, bool> check = delegate(string name, bool ok)
            {
                if (!ok) failures++;
                log.AppendLine((ok ? "PASS  " : "FAIL  ") + name);
            };
            Action<string> note = delegate(string s) { log.AppendLine("      " + s); };

            // ---- find the ports
            int outIdx = -1, inIdx = -1;
            string outName = null, inName = null;
            int outs = WinMM.midiOutGetNumDevs();
            for (int i = 0; i < outs && outIdx < 0; i++)
            {
                WinMM.MIDIOUTCAPS caps;
                if (WinMM.midiOutGetDevCaps((IntPtr)i, out caps, Marshal.SizeOf(typeof(WinMM.MIDIOUTCAPS))) == 0
                    && caps.szPname != null && caps.szPname.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                { outIdx = i; outName = caps.szPname; }
            }
            int ins = WinMM.midiInGetNumDevs();
            for (int i = 0; i < ins && inIdx < 0; i++)
            {
                WinMM.MIDIINCAPS caps;
                if (WinMM.midiInGetDevCaps((IntPtr)i, out caps, Marshal.SizeOf(typeof(WinMM.MIDIINCAPS))) == 0
                    && caps.szPname != null && caps.szPname.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                { inIdx = i; inName = caps.szPname; }
            }
            note("looking for a port matching '" + match + "'");
            note("output: " + (outName != null ? "[" + outIdx + "] " + outName : "not found"));
            note("input : " + (inName != null ? "[" + inIdx + "] " + inName : "not found"));
            if (outIdx < 0 || inIdx < 0)
            {
                log.AppendLine();
                log.AppendLine("SKIP  no HPD-20 MIDI port found - connect the instrument and run this again");
                return Write(log, reportPath, 0);
            }
            check("HPD-20 output port found", true);
            check("HPD-20 input port found", true);

            // ---- open
            MidiSession session = new MidiSession();
            bool opened = session.Open(outIdx, inIdx, true, true);
            check("ports open", opened);
            if (!opened) { note("error: " + session.LastError); session.Dispose(); return Write(log, reportPath, failures); }

            List<byte[]> rx = new List<byte[]>();
            object gate = new object();
            session.MessageReceived += delegate(object sender, MidiMessageEventArgs e)
            {
                lock (gate) { rx.Add(e.Data); }
            };

            // ---- identity request
            byte[] identity = { 0xF0, 0x7E, 0x7F, 0x06, 0x01, 0xF7 };
            bool sentId = session.SendSysEx(identity);
            check("identity request sent", sentId);

            byte[] reply = null;
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2500 && reply == null)
            {
                System.Threading.Thread.Sleep(25);
                lock (gate)
                {
                    foreach (byte[] m in rx)
                        if (m.Length >= 15 && m[0] == 0xF0 && m[1] == 0x7E && m[3] == 0x06 && m[4] == 0x02) { reply = m; break; }
                }
            }
            check("identity reply received", reply != null);
            if (reply != null)
            {
                note("reply: " + MidiText.Hex(reply));
                bool roland = reply[5] == 0x41;
                int family = (reply[6] << 8) | reply[7];
                int member = (reply[8] << 8) | reply[9];
                int rev = (reply[10] << 24) | (reply[11] << 16) | (reply[12] << 8) | reply[13];
                note("manufacturer 0x" + reply[5].ToString("X2") + (roland ? " (Roland)" : " (NOT Roland)"));
                note("device id    0x" + reply[2].ToString("X2"));
                note("family       0x" + family.ToString("X4") + "   member 0x" + member.ToString("X4"));
                note("firmware rev bytes " + ((rev >> 24) & 0xFF).ToString("X2") + " " + ((rev >> 16) & 0xFF).ToString("X2") + " " + ((rev >> 8) & 0xFF).ToString("X2") + " " + (rev & 0xFF).ToString("X2"));
                check("reply is from Roland", roland);
                check("family code is 7802 (HPD-20)", family == 0x7802);
            }

            // ---- a note on the centre pad, then silence
            System.Threading.Thread.Sleep(150);
            bool noteOn = session.SendShort(0x90, 60, 90);
            System.Threading.Thread.Sleep(120);
            bool noteOff = session.SendShort(0x80, 60, 0x40);
            check("note on sent to M1 (60)", noteOn);
            check("note off sent", noteOff);
            System.Threading.Thread.Sleep(200);
            session.SendShort(0xB0, 120, 0);              // All Sound Off
            note("sent note 60 on ch1 and All Sound Off afterwards");

            // ---- kit change, and whether the unit echoes it (needs Program Change Tx = ON)
            lock (gate) { rx.Clear(); }
            session.SendShort(0xB0, 0, 0);                // bank MSB 0
            session.SendShort(0xB0, 32, 0);               // bank LSB 0
            bool pc = session.SendShort1(0xC0, 0);        // program 1 -> kit 001
            check("program change sent (kit 001)", pc);
            int echo = -1;
            sw.Restart();
            while (sw.ElapsedMilliseconds < 1500)
            {
                System.Threading.Thread.Sleep(25);
                lock (gate) { foreach (byte[] m in rx) if (m.Length >= 2 && (m[0] & 0xF0) == 0xC0) { echo = m[1]; break; } }
                if (echo >= 0) break;
            }
            if (echo >= 0) note("unit echoed program change " + (echo + 1) + " (Program Change Tx is ON)");
            else note("no program-change echo seen (Program Change Tx may be OFF) - the kit change itself cannot be read back");

            // ---- did anything else arrive (active sensing, pad notes)?
            lock (gate)
            {
                int active = 0, notes = 0, cc = 0;
                foreach (byte[] m in rx)
                {
                    if (m.Length == 1 && m[0] == 0xFE) active++;
                    else if (m.Length >= 2 && (m[0] & 0xF0) == 0x90) notes++;
                    else if (m.Length >= 2 && (m[0] & 0xF0) == 0xB0) cc++;
                }
                note("messages received during the run: " + rx.Count + " (notes " + notes + ", CC " + cc + ", active sensing " + active + ")");
            }

            // ---- teardown must not hang
            Stopwatch close = Stopwatch.StartNew();
            session.Dispose();
            close.Stop();
            check("disconnect completed in under 2 s", close.ElapsedMilliseconds < 2000);
            note("teardown took " + close.ElapsedMilliseconds + " ms");

            return Write(log, reportPath, failures);
        }

        private static int Write(StringBuilder log, string reportPath, int failures)
        {
            log.AppendLine();
            log.AppendLine(failures == 0 ? "HARDWARE CHECKS PASSED" : failures + " HARDWARE CHECK(S) FAILED");
            Console.Write(log.ToString());
            if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, log.ToString(), Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }
    }
}




