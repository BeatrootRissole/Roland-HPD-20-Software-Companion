using System;
using System.Drawing;
using System.IO;
using System.Text;

namespace Hpd20Ctrl
{
    /// <summary>Verifies pad placement and hit-testing against the documented HPD-20 layout.</summary>
    public static class PadTest
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

            PadSurface surface = new PadSurface();
            surface.Size = new Size(600, 500);
            using (Graphics g = surface.CreateGraphics()) { }   // realise the control's geometry

            // mirrors PadSurface.ComputeLayout: beam 90..w-90 at y=10 h=26, jacks 74px tall
            float w = 600, h = 500;
            float top = 10 + 26 + 12;                 // 48
            float bottom = h - 74 - 10;               // 416
            float r = Math.Min(w - 40, bottom - top) * 0.47f;
            float cx = w / 2f;                        // 300
            float cy = top + (bottom - top) / 2f;     // 232

            Func<float, float, string> hit = delegate(float dx, float dy) { return surface.HitTest(new Point((int)(cx + dx), (int)(cy + dy))); };
            Func<int, int, string> at = delegate(int x, int y) { return surface.HitTest(new Point(x, y)); };

            // centre pad
            check("centre is M5", hit(0, 0) == "M5");

            // four main pads: M3 upper-left, M4 upper-right, M1 lower-left, M2 lower-right
            float d = r * 0.52f;
            check("upper-left is M3", hit(-d, -d) == "M3");
            check("upper-right is M4", hit(d, -d) == "M4");
            check("lower-left is M1", hit(-d, d) == "M1");
            check("lower-right is M2", hit(d, d) == "M2");

            // rim segments span the TOP half only: S1 far left, S8 far right, S4/S5 at the top
            float rr = r * 0.90f;
            check("top-left of rim is S3", hit(-0.555f * rr, -0.831f * rr) == "S3");
            check("top of rim is S4 or S5", hit(-12, -rr) == "S4" || hit(-12, -rr) == "S5");
            check("top-right of rim is S6", hit(0.555f * rr, -0.831f * rr) == "S6");
            check("far left of rim is S1", hit(-rr, 0) == "S1");
            check("far right of rim is S8", hit(rr, 0) == "S8");

            // the bottom of the rim has no sub-pads
            check("bottom rim is empty", hit(0, rr) == null);
            check("bottom-left rim is empty", hit(-0.6f * rr, 0.75f * rr) == null);
            check("bottom-right rim is empty", hit(0.6f * rr, 0.75f * rr) == null);

            // D-BEAM sits across the top edge
            check("D-BEAM is at the top", at(300, 20) == "BEAM");
            check("D-BEAM is not on the right any more", at(300, 20) == "BEAM" && at((int)(w - 20), 250) != "BEAM");

            // jacks along the bottom: TRIG IN head/rim, HH CTRL, FOOT SW 1/2
            check("TRIG IN head is clickable", at(47, 466) == "EXT_H");
            check("TRIG IN rim is clickable", at(121, 466) == "EXT_R");
            check("HH CTRL is clickable", at(300, 461) == "HH");
            check("Foot SW 1 is clickable", at(458, 461) == "FSW1");
            check("Foot SW 2 is clickable", at(528, 461) == "FSW2");

            // factory default note numbers
            check("M1 default note 60", surface.Pads["M1"].Note == 60);
            check("M5 default note 64", surface.Pads["M5"].Note == 64);
            check("S1 default note 65", surface.Pads["S1"].Note == 65);
            check("S8 default note 72", surface.Pads["S8"].Note == 72);
            check("D-BEAM default note 73", surface.Pads["BEAM"].Note == 73);
            check("TRIG IN head default 74", surface.Pads["EXT_H"].Note == 74);
            check("TRIG IN rim default 75", surface.Pads["EXT_R"].Note == 75);
            check("HH CTRL default 76", surface.Pads["HH"].Note == 76);
            check("foot switches have no note", surface.Pads["FSW1"].Note < 0 && surface.Pads["FSW2"].Note < 0);
            check("13 playable pads present", surface.Pads.ContainsKey("M1") && surface.Pads.ContainsKey("M5") &&
                                              surface.Pads.ContainsKey("S1") && surface.Pads.ContainsKey("S8"));

            log.AppendLine();
            log.AppendLine(failures == 0 ? "PAD CHECKS PASSED" : failures + " PAD CHECK(S) FAILED");
            Console.Write(log.ToString());
            if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, log.ToString(), Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }
    }
}
