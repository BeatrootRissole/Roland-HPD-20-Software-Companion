using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Hpd20Ctrl
{
    public sealed class PadState
    {
        public string Id;            // M1..M5, S1..S8, BEAM, EXT_H, EXT_R, HH, FSW1, FSW2
        public string Display;
        public int Note = -1;        // -1 = unknown
        public string SampleLabel = "";
        public int UserInst = -1;    // 1..500, -1 unknown
        public bool IsBeam;
        public bool IsSwitch;
        public string Info = "";     // instrument name, CC number, or foot-switch function
        public int Cc = -1;          // controller number when the element sends one
    }

    public sealed class PadTriggerEventArgs : EventArgs
    {
        public string PadId;
        public PadTriggerEventArgs(string id) { PadId = id; }
    }

    public sealed class PadSpec
    {
        public string Id;
        public string Display;
        public bool IsBeam;
        public bool IsSwitch;
        public int DefaultNote = -1;

        // Factory default note map (Roland HPD-20): M1=60 ... M5=64, S1=65 ... S8=72,
        // D-BEAM=73, Ext Head=74, Ext Rim=75, HH Pedal=76. Taken from the factory backup.
        public static readonly PadSpec[] All = new PadSpec[]
        {
            new PadSpec { Id = "M1", Display = "M1", DefaultNote = 60 },
            new PadSpec { Id = "M2", Display = "M2", DefaultNote = 61 },
            new PadSpec { Id = "M3", Display = "M3", DefaultNote = 62 },
            new PadSpec { Id = "M4", Display = "M4", DefaultNote = 63 },
            new PadSpec { Id = "M5", Display = "M5", DefaultNote = 64 },
            new PadSpec { Id = "S1", Display = "S1", DefaultNote = 65 },
            new PadSpec { Id = "S2", Display = "S2", DefaultNote = 66 },
            new PadSpec { Id = "S3", Display = "S3", DefaultNote = 67 },
            new PadSpec { Id = "S4", Display = "S4", DefaultNote = 68 },
            new PadSpec { Id = "S5", Display = "S5", DefaultNote = 69 },
            new PadSpec { Id = "S6", Display = "S6", DefaultNote = 70 },
            new PadSpec { Id = "S7", Display = "S7", DefaultNote = 71 },
            new PadSpec { Id = "S8", Display = "S8", DefaultNote = 72 },
            new PadSpec { Id = "BEAM", Display = "D-BEAM", IsBeam = true, DefaultNote = 73 },
            new PadSpec { Id = "EXT_H", Display = "Head", DefaultNote = 74 },
            new PadSpec { Id = "EXT_R", Display = "Rim", DefaultNote = 75 },
            new PadSpec { Id = "HH", Display = "HH Ctrl", DefaultNote = 76 },
            new PadSpec { Id = "FSW1", Display = "Foot SW 1", IsSwitch = true },
            new PadSpec { Id = "FSW2", Display = "Foot SW 2", IsSwitch = true }
        };
    }

    /// <summary>
    /// Top view of the HPD-20: D-BEAM across the top edge, the playing surface below it
    /// (M5 centre, M1/M2 nearest the player, M3/M4 above, S1..S8 across the top half of the rim),
    /// and the three jacks along the bottom: TRIG IN (head + rim), HH CTRL and FOOT SW.
    /// Geometry follows the manual's pad diagram (p. 14); the jacks mirror the rear panel.
    /// </summary>
    public sealed class PadSurface : Control
    {
        private const float CenterPadR = 0.27f;
        private const float InnerOuterR = 0.78f;
        private const float RingInnerR = 0.82f;
        private const float RingOuterR = 1.00f;
        private const float GapDeg = 1.6f;

        private readonly Dictionary<string, PadState> _pads = new Dictionary<string, PadState>();
        private readonly Dictionary<string, float> _flash = new Dictionary<string, float>();
        private readonly Timer _anim;

        private RectangleF _surface;
        private PointF _center;
        private float _radius;
        private RectangleF _beamRect;
        private RectangleF _trigBox, _hhBox, _fswBox;
        private RectangleF _trigHead, _trigRim, _fsw1, _fsw2;
        private RectangleF _hhPedal;

        public Dictionary<string, PadState> Pads { get { return _pads; } }
        public string Caption = "";
        public string SelectedId;
        public Image UserPhoto;
        public bool ShowLabels = true;

        public event EventHandler<PadTriggerEventArgs> PadPressed;
        public event EventHandler<PadTriggerEventArgs> PadReleased;

        public PadSurface()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(26, 27, 30);

            foreach (PadSpec s in PadSpec.All)
            {
                PadState p = new PadState();
                p.Id = s.Id;
                p.Display = s.Display;
                p.IsBeam = s.IsBeam;
                p.IsSwitch = s.IsSwitch;
                p.Note = s.DefaultNote;
                _pads[s.Id] = p;
            }

            _anim = new Timer();
            _anim.Interval = 40;
            _anim.Tick += delegate
            {
                bool any = false;
                foreach (string k in new List<string>(_flash.Keys))
                {
                    _flash[k] -= 0.08f;
                    if (_flash[k] <= 0) _flash.Remove(k); else any = true;
                }
                if (!any) _anim.Stop();
                Invalidate();
            };
        }

        public void Flash(string id)
        {
            if (!_pads.ContainsKey(id)) return;
            _flash[id] = 1f;
            if (!_anim.Enabled) _anim.Start();
            Invalidate();
        }

        // ------------------------------ geometry ------------------------------

        private void ComputeLayout()
        {
            float w = Width, h = Height;
            float jackH = 74f;
            float beamH = 26f;

            // D-BEAM runs across the top edge, like the strip above the playing surface
            _beamRect = new RectangleF(170, 10, Math.Max(80, w - 260), beamH);

            float top = _beamRect.Bottom + 12;
            float bottom = h - jackH - 10;
            if (bottom - top < 80) return;

            _radius = Math.Min(w - 40, bottom - top) * 0.47f;
            _center = new PointF(w / 2f, top + (bottom - top) / 2f);
            _surface = new RectangleF(_center.X - _radius, _center.Y - _radius, _radius * 2, _radius * 2);

            // three jack groups along the bottom
            float boxW = (w - 28 - 16) / 3f;
            float y = h - jackH;
            _trigBox = new RectangleF(14, y, boxW, jackH - 8);
            _hhBox = new RectangleF(14 + boxW + 8, y, boxW, jackH - 8);
            _fswBox = new RectangleF(14 + (boxW + 8) * 2, y, boxW, jackH - 8);

            float d = Math.Min(boxW * 0.24f, 20f);
            _trigHead = new RectangleF(_trigBox.X + boxW * 0.18f - d, _trigBox.Y + 20, d * 2, d * 2);
            _trigRim = new RectangleF(_trigBox.X + boxW * 0.58f - d, _trigBox.Y + 20, d * 2, d * 2);

            _hhPedal = new RectangleF(_hhBox.X + boxW * 0.28f, _hhBox.Y + 20, boxW * 0.44f, 30);

            _fsw1 = new RectangleF(_fswBox.X + boxW * 0.16f, _fswBox.Y + 22, boxW * 0.30f, 26);
            _fsw2 = new RectangleF(_fswBox.X + boxW * 0.54f, _fswBox.Y + 22, boxW * 0.30f, 26);
        }

        private static double Deg(double d) { return d * Math.PI / 180.0; }

        /// <summary>One-line description of what sits under a point, for the hover readout.</summary>
        public string DescribeAt(Point p)
        {
            string id = HitTest(p);
            if (id == null) return "Hover a pad, the D-BEAM strip or one of the jacks.";
            PadState st;
            if (!_pads.TryGetValue(id, out st)) return "";
            StringBuilder sb = new StringBuilder();
            sb.Append(st.Display);
            if (st.IsSwitch)
            {
                sb.Append("  -  foot switch, runs a function (no note)");
                if (!string.IsNullOrEmpty(st.Info)) sb.Append("  -  ").Append(st.Info);
                return sb.ToString();
            }
            if (st.Cc >= 0) sb.Append("  -  CC").Append(st.Cc);
            if (st.Note >= 0) sb.Append("  -  note ").Append(st.Note).Append(" (").Append(MidiText.NoteName(st.Note)).Append(")");
            else sb.Append("  -  no note assigned");
            if (!string.IsNullOrEmpty(st.Info)) sb.Append("  -  ").Append(st.Info);
            sb.Append(st.IsBeam ? "  -  click sends the controller value" : "  -  click to play, hold to sustain");
            return sb.ToString();
        }

        public string HitTest(Point p)
        {
            ComputeLayout();

            PointF f = new PointF(p.X, p.Y);
            if (_beamRect.Contains(f)) return "BEAM";
            if (_trigHead.Contains(f)) return "EXT_H";
            if (_trigRim.Contains(f)) return "EXT_R";
            if (_hhPedal.Contains(f)) return "HH";
            if (_fsw1.Contains(f)) return "FSW1";
            if (_fsw2.Contains(f)) return "FSW2";

            float dx = p.X - _center.X;
            float dy = _center.Y - p.Y;
            double r = Math.Sqrt(dx * dx + dy * dy);
            double ang = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            if (ang < 0) ang += 360;

            if (r <= _radius * RingOuterR + 4)
            {
                if (r <= _radius * CenterPadR) return "M5";
                if (r <= _radius * InnerOuterR) return QuadrantPad(ang);
                if (r >= _radius * RingInnerR) return RingPad(ang);
                return null;
            }
            return null;
        }

        private static string QuadrantPad(double ang)
        {
            if (ang < 90) return "M4";
            if (ang < 180) return "M3";
            if (ang < 270) return "M1";
            return "M2";
        }

        private static string RingPad(double ang)
        {
            if (ang > 180) return null;    // the bottom rim is smooth: no sub-pads there
            int idx = (int)((180 - ang) / 22.5);
            if (idx < 0) idx = 0;
            if (idx > 7) idx = 7;
            return "S" + (idx + 1);
        }

        // ------------------------------ input ------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            string id = HitTest(e.Location);
            if (id == null) return;
            SelectedId = id;
            Invalidate();
            if (PadPressed != null) PadPressed(this, new PadTriggerEventArgs(id));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            string id = HitTest(e.Location);
            if (id == null) id = SelectedId;
            if (id != null && PadReleased != null) PadReleased(this, new PadTriggerEventArgs(id));
        }

        // ------------------------------ painting ------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);
            if (Width < 240 || Height < 260) return;

            ComputeLayout();

            if (UserPhoto != null) DrawPhoto(g);
            else DrawBody(g);

            DrawSurfaceDisc(g);
            DrawRing(g);
            DrawInnerPads(g);
            DrawCenterPad(g);
            DrawBeam(g);
            DrawJacks(g);

            using (Font f = new Font("Segoe UI Semibold", 9f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(185, 187, 194)))
                g.DrawString("Roland HandSonic HPD-20", f, b, 14, 8);

            if (!string.IsNullOrEmpty(Caption))
            {
                using (Font f = new Font("Segoe UI", 7.5f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(124, 200, 255)))
                {
                    SizeF sz = g.MeasureString(Caption, f);
                    g.DrawString(Caption, f, b, _beamRect.Right - sz.Width, _beamRect.Bottom + 4);
                }
            }
        }

        private void DrawBody(Graphics g)
        {
            RectangleF body = new RectangleF(6, 6, Width - 12, Height - 12);
            using (GraphicsPath path = RoundRect(body, 18))
            using (LinearGradientBrush lg = new LinearGradientBrush(body, Color.FromArgb(52, 53, 58), Color.FromArgb(20, 21, 24), 90f))
                g.FillPath(lg, path);
            using (GraphicsPath path = RoundRect(body, 18))
            using (Pen p = new Pen(Color.FromArgb(78, 80, 86), 1.3f))
                g.DrawPath(p, path);
        }

        private void DrawPhoto(Graphics g)
        {
            RectangleF body = new RectangleF(6, 6, Width - 12, Height - 12);
            g.DrawImage(UserPhoto, body);
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
                g.FillRectangle(dim, body);
        }

        private void DrawSurfaceDisc(Graphics g)
        {
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(_surface);
                using (PathGradientBrush pg = new PathGradientBrush(gp))
                {
                    pg.CenterColor = Color.FromArgb(62, 63, 68);
                    pg.SurroundColors = new Color[] { Color.FromArgb(34, 35, 39) };
                    g.FillPath(pg, gp);
                }
                using (Pen pen = new Pen(Color.FromArgb(96, 98, 104), 1.6f)) g.DrawPath(pen, gp);
            }
        }

        private void DrawBeam(Graphics g)
        {
            float fl;
            _flash.TryGetValue("BEAM", out fl);
            Color c = Color.FromArgb(66, 68, 74);
            if (fl > 0) c = PadSurface.Blend(c, Color.FromArgb(120, 200, 255), fl);
            if (SelectedId == "BEAM") c = Color.FromArgb(84, 104, 124);

            using (GraphicsPath gp = RoundRect(_beamRect, 7))
            using (LinearGradientBrush lg = new LinearGradientBrush(_beamRect, Blend(c, Color.White, 0.14f), Blend(c, Color.Black, 0.18f), 0f))
                g.FillPath(lg, gp);
            using (GraphicsPath gp = RoundRect(_beamRect, 7))
            using (Pen p = new Pen(Color.FromArgb(108, 110, 118), 1.1f))
                g.DrawPath(p, gp);

            // emitter marks along the strip
            using (Pen p = new Pen(Color.FromArgb(120, 125, 132), 1f))
            {
                for (int i = 1; i < 8; i++)
                {
                    float x = _beamRect.X + _beamRect.Width * i / 8f;
                    g.DrawLine(p, x, _beamRect.Y + 6, x, _beamRect.Bottom - 6);
                }
            }

            using (Font f = new Font("Segoe UI Semibold", 7.5f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(210, 214, 220)))
                g.DrawString("D-BEAM", f, b, _beamRect.X + 8, _beamRect.Y + 6);

            PadState st;
            if (_pads.TryGetValue("BEAM", out st))
            {
                using (Font f = new Font("Segoe UI", 6.8f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(170, 174, 182)))
                {
                    StringBuilder sb = new StringBuilder();
                    if (st.Cc >= 0) sb.Append("CC").Append(st.Cc).Append("   ");
                    if (st.Note >= 0) sb.Append(MidiText.NoteName(st.Note));
                    string t = sb.ToString();
                    if (t.Length > 0)
                    {
                        SizeF sz = g.MeasureString(t, f);
                        g.DrawString(t, f, b, _beamRect.Right - 8 - sz.Width, _beamRect.Y + 7);
                    }
                }
            }
        }

        private void DrawJacks(Graphics g)
        {
            DrawJackBox(g, _trigBox, "TRIG IN");
            SinglePad(g, "EXT_H", _trigHead);
            SinglePad(g, "EXT_R", _trigRim);

            DrawJackBox(g, _hhBox, "HH CTRL");
            DrawPedal(g);

            DrawJackBox(g, _fswBox, "FOOT SW");
            DrawSwitch(g, "FSW1", _fsw1, "1");
            DrawSwitch(g, "FSW2", _fsw2, "2");
        }

        private void DrawJackBox(Graphics g, RectangleF box, string title)
        {
            using (GraphicsPath gp = RoundRect(box, 6))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(38, 39, 44)))
                g.FillPath(b, gp);
            using (GraphicsPath gp = RoundRect(box, 6))
            using (Pen p = new Pen(Color.FromArgb(72, 74, 80), 1f))
                g.DrawPath(p, gp);

            using (Font f = new Font("Segoe UI", 7f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 153, 160)))
                g.DrawString(title, f, b, box.X + 7, box.Y + 4);
        }

        private void SinglePad(Graphics g, string id, RectangleF r)
        {
            PadState st = _pads[id];
            float fl;
            _flash.TryGetValue(id, out fl);
            Color c = Color.FromArgb(58, 60, 66);
            if (fl > 0) c = Blend(c, Color.FromArgb(255, 150, 40), fl);
            if (SelectedId == id) c = Color.FromArgb(84, 88, 100);

            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(r);
                using (PathGradientBrush pg = new PathGradientBrush(gp))
                {
                    pg.CenterColor = Blend(c, Color.White, 0.16f);
                    pg.SurroundColors = new Color[] { c };
                    g.FillPath(pg, gp);
                }
                using (Pen pen = new Pen(SelectedId == id ? Color.FromArgb(255, 172, 64) : Color.FromArgb(104, 106, 112), 1.1f))
                    g.DrawPath(pen, gp);
            }

            // name and note inside the circle so nothing is clipped by the box edge
            using (Font f = new Font("Segoe UI Semibold", 6.6f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(222, 224, 230)))
            using (Font f2 = new Font("Segoe UI", 6.2f))
            using (SolidBrush b2 = new SolidBrush(Color.FromArgb(168, 170, 178)))
            {
                string note = st.Note >= 0 ? MidiText.NoteName(st.Note) : "?";
                SizeF s1 = g.MeasureString(st.Display, f);
                SizeF s2 = g.MeasureString(note, f2);
                float ty = r.Y + r.Height / 2 - (s1.Height + s2.Height) / 2;
                g.DrawString(st.Display, f, b, r.X + r.Width / 2 - s1.Width / 2, ty);
                g.DrawString(note, f2, b2, r.X + r.Width / 2 - s2.Width / 2, ty + s1.Height);
            }
        }

        private void DrawPedal(Graphics g)
        {
            float fl;
            _flash.TryGetValue("HH", out fl);
            Color c = Color.FromArgb(58, 60, 66);
            if (fl > 0) c = Blend(c, Color.FromArgb(255, 150, 40), fl);
            if (SelectedId == "HH") c = Color.FromArgb(84, 88, 100);

            RectangleF r = _hhPedal;
            using (GraphicsPath gp = RoundRect(r, 4))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, gp);
            using (GraphicsPath gp = RoundRect(r, 4))
            using (Pen p = new Pen(SelectedId == "HH" ? Color.FromArgb(255, 172, 64) : Color.FromArgb(104, 106, 112), 1.1f))
                g.DrawPath(p, gp);

            // hinge line + treadle suggestion
            using (Pen p = new Pen(Color.FromArgb(150, 152, 158), 1f))
            {
                g.DrawLine(p, r.X + 4, r.Y + r.Height - 6, r.Right - 4, r.Y + 8);
            }

            PadState st = _pads["HH"];
            using (Font f = new Font("Segoe UI", 6.4f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(214, 216, 222)))
            {
                StringBuilder sb = new StringBuilder();
                if (st.Cc >= 0) sb.Append("CC").Append(st.Cc).Append("  ");
                sb.Append(st.Note >= 0 ? "note " + MidiText.NoteName(st.Note) : "note ?");
                g.DrawString(sb.ToString(), f, b, r.X, r.Bottom + 3);
            }
        }

        private void DrawSwitch(Graphics g, string id, RectangleF r, string label)
        {
            PadState st = _pads[id];
            float fl;
            _flash.TryGetValue(id, out fl);
            Color c = Color.FromArgb(58, 60, 66);
            if (fl > 0) c = Blend(c, Color.FromArgb(255, 150, 40), fl);
            if (SelectedId == id) c = Color.FromArgb(84, 88, 100);

            using (GraphicsPath gp = RoundRect(r, 5))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, gp);
            using (GraphicsPath gp = RoundRect(r, 5))
            using (Pen p = new Pen(SelectedId == id ? Color.FromArgb(255, 172, 64) : Color.FromArgb(104, 106, 112), 1.1f))
                g.DrawPath(p, gp);

            using (Font f = new Font("Segoe UI Semibold", 8f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(216, 218, 224)))
            {
                SizeF sz = g.MeasureString(label, f);
                g.DrawString(label, f, b, r.X + r.Width / 2 - sz.Width / 2, r.Y + r.Height / 2 - sz.Height / 2);
            }
            if (!string.IsNullOrEmpty(st.Info))
            {
                using (Font f = new Font("Segoe UI", 6f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 200, 240)))
                {
                    string t = st.Info;
                    if (t.Length > 14) t = t.Substring(0, 13) + "\u2026";
                    SizeF sz = g.MeasureString(t, f);
                    g.DrawString(t, f, b, r.X + r.Width / 2 - sz.Width / 2, r.Bottom + 1);
                }
            }
        }

        private void DrawRing(Graphics g)
        {
            DrawBand(g, _radius * RingInnerR, _radius * RingOuterR, 180, 360, Color.FromArgb(46, 47, 52));

            for (int i = 0; i < 8; i++)
            {
                string id = "S" + (i + 1);
                double a0 = 180 - (i + 1) * 22.5 + GapDeg;
                double a1 = 180 - i * 22.5 - GapDeg;
                PadState st = _pads[id];
                DrawSector(g, _radius * RingInnerR, _radius * RingOuterR, a0, a1, st, false);
            }
        }

        private void DrawInnerPads(Graphics g)
        {
            double[][] quadrants = new double[][]
            {
                new double[] {  0 + GapDeg,  90 - GapDeg },
                new double[] { 90 + GapDeg, 180 - GapDeg },
                new double[] {180 + GapDeg, 270 - GapDeg },
                new double[] {270 + GapDeg, 360 - GapDeg }
            };
            string[] ids = { "M4", "M3", "M1", "M2" };
            for (int i = 0; i < 4; i++)
                DrawSector(g, _radius * CenterPadR, _radius * InnerOuterR, quadrants[i][0], quadrants[i][1], _pads[ids[i]], true);
        }

        private void DrawCenterPad(Graphics g)
        {
            PadState st = _pads["M5"];
            RectangleF r = new RectangleF(_center.X - _radius * CenterPadR, _center.Y - _radius * CenterPadR,
                                          _radius * CenterPadR * 2, _radius * CenterPadR * 2);
            Color baseCol = PadBase(st.Id, true);
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(r);
                using (PathGradientBrush pg = new PathGradientBrush(gp))
                {
                    pg.CenterColor = Blend(baseCol, Color.White, 0.18f);
                    pg.SurroundColors = new Color[] { baseCol };
                    g.FillPath(pg, gp);
                }
                using (Pen pen = new Pen(SelectedId == "M5" ? Color.FromArgb(255, 172, 64) : Color.FromArgb(112, 114, 120), SelectedId == "M5" ? 2.2f : 1.2f))
                    g.DrawPath(pen, gp);
            }
            DrawPadText(g, st, _center.X, _center.Y, _radius * CenterPadR, true);
        }

        private void DrawBand(Graphics g, float rIn, float rOut, double a0, double a1, Color colour)
        {
            RectangleF outer = new RectangleF(_center.X - rOut, _center.Y - rOut, rOut * 2, rOut * 2);
            RectangleF inner = new RectangleF(_center.X - rIn, _center.Y - rIn, rIn * 2, rIn * 2);
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddArc(outer, (float)(-a1), (float)(a1 - a0));
                gp.AddArc(inner, (float)(-a0), (float)(a0 - a1));
                gp.CloseFigure();
                using (SolidBrush b = new SolidBrush(colour)) g.FillPath(b, gp);
                using (Pen p = new Pen(Color.FromArgb(78, 80, 86), 1f)) g.DrawPath(p, gp);
            }
        }

        private void DrawSector(Graphics g, float rIn, float rOut, double a0, double a1, PadState st, bool big)
        {
            RectangleF outer = new RectangleF(_center.X - rOut, _center.Y - rOut, rOut * 2, rOut * 2);
            RectangleF inner = new RectangleF(_center.X - rIn, _center.Y - rIn, rIn * 2, rIn * 2);
            using (GraphicsPath gp = new GraphicsPath())
            {
                // GDI+ arcs are clockwise with y down; our angles are counter-clockwise with y up
                gp.AddArc(outer, (float)(-a1), (float)(a1 - a0));
                gp.AddArc(inner, (float)(-a0), (float)(a0 - a1));
                gp.CloseFigure();

                Color baseCol = PadBase(st.Id, big);
                RectangleF bounds = gp.GetBounds();
                using (LinearGradientBrush lg = new LinearGradientBrush(bounds, Blend(baseCol, Color.White, 0.10f),
                                                                       Blend(baseCol, Color.Black, 0.14f), 60f))
                    g.FillPath(lg, gp);

                bool sel = SelectedId == st.Id;
                using (Pen pen = new Pen(sel ? Color.FromArgb(255, 172, 64) : Color.FromArgb(105, 107, 113), sel ? 2.0f : 1.0f))
                    g.DrawPath(pen, gp);
            }

            double mid = (a0 + a1) / 2.0;
            float lr = (rIn + rOut) / 2f;
            float lx = _center.X + (float)(Math.Cos(Deg(mid)) * lr);
            float ly = _center.Y - (float)(Math.Sin(Deg(mid)) * lr);
            DrawPadText(g, st, lx, ly, (rOut - rIn) / 2f, big);
        }

        private Color PadBase(string id, bool big)
        {
            float fl;
            _flash.TryGetValue(id, out fl);
            Color c = big ? Color.FromArgb(70, 72, 78) : Color.FromArgb(58, 60, 66);
            if (fl > 0) c = Blend(c, Color.FromArgb(255, 150, 40), fl);
            return c;
        }

        private void DrawPadText(Graphics g, PadState st, float x, float y, float halfSize, bool big)
        {
            if (!ShowLabels) return;
            string note = st.Note >= 0 ? MidiText.NoteName(st.Note) : "?";
            using (Font fName = new Font("Segoe UI Semibold", big ? 9.5f : 7f))
            using (Font fSub = new Font("Segoe UI", big ? 7f : 6f))
            using (SolidBrush bName = new SolidBrush(Color.FromArgb(235, 236, 240)))
            using (SolidBrush bSub = new SolidBrush(Color.FromArgb(165, 167, 175)))
            {
                SizeF s1 = g.MeasureString(st.Display, fName);
                float ty = y - s1.Height * 0.6f;
                string info = st.Cc >= 0 ? ("CC" + st.Cc + (st.Info.Length > 0 ? "  " + st.Info : "")) : st.Info;
                if (big)
                {
                    g.DrawString(st.Display, fName, bName, x - s1.Width / 2, ty - 6);
                    g.DrawString(note, fSub, bSub, x - g.MeasureString(note, fSub).Width / 2, ty + 7);
                    string lab = info.Length > 0 ? info : st.SampleLabel;
                    if (lab.Length > 0)
                    {
                        if (g.MeasureString(lab, fSub).Width > halfSize * 1.7f)
                            lab = lab.Length > 12 ? lab.Substring(0, 11) + "\u2026" : lab;
                        using (SolidBrush bLab = new SolidBrush(Color.FromArgb(120, 200, 255)))
                            g.DrawString(lab, fSub, bLab, x - g.MeasureString(lab, fSub).Width / 2, ty + 18);
                    }
                }
                else
                {
                    g.DrawString(st.Display, fName, bName, x - s1.Width / 2, ty - 3);
                    g.DrawString(note, fSub, bSub, x - g.MeasureString(note, fSub).Width / 2, ty + 7);
                    if (st.Cc >= 0)
                        using (SolidBrush bInfo = new SolidBrush(Color.FromArgb(120, 200, 255)))
                            g.DrawString("CC" + st.Cc, fSub, bInfo, x - g.MeasureString("CC" + st.Cc, fSub).Width / 2, ty + 15);
                }
            }
        }

        public static Color Blend(Color a, Color b, float t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}






