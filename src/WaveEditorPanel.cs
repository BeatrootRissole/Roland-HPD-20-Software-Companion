using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace Hpd20Ctrl
{
    /// <summary>Waveform display with a draggable selection, zoom and time ruler.</summary>
    public sealed class WaveView : Control
    {
        private Segment _segment;
        private int _selStart, _selEnd;
        private double _viewStart;      // samples
        private double _viewSpan;       // samples
        private int _cursor;

        public Segment Segment
        {
            get { return _segment; }
            set { _segment = value; ZoomAll(); }
        }

        public int SelectionStart { get { return Math.Min(_selStart, _selEnd); } }
        public int SelectionEnd { get { return Math.Max(_selStart, _selEnd); } }
        public bool HasSelection { get { return Math.Abs(_selEnd - _selStart) > 1; } }
        public int Playhead { get { return _cursor; } }

        public event EventHandler SelectionChanged;

        private int _playPos = -1;

        /// <summary>Moves the edit cursor without touching the selection.</summary>
        public void SetCursor(int sample)
        {
            _cursor = Math.Max(0, sample);
            Invalidate();
        }

        /// <summary>Sample position of the playback playhead, or -1 when not playing.</summary>
        public void SetPlayPosition(int sample)
        {
            _playPos = sample;
            Invalidate();
        }

        public WaveView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(22, 23, 26);
            Cursor = Cursors.IBeam;
        }

        public void ZoomAll()
        {
            _viewStart = 0;
            _viewSpan = _segment != null ? Math.Max(1, _segment.Frames) : 1;
            _selStart = 0;
            _selEnd = 0;
            _cursor = 0;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        public void SelectAll()
        {
            if (_segment == null) return;
            _selStart = 0;
            _selEnd = _segment.Frames;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        public void SetSelection(int start, int end)
        {
            if (_segment == null) { _selStart = _selEnd = 0; return; }
            _selStart = Math.Max(0, Math.Min(_segment.Frames, start));
            _selEnd = Math.Max(0, Math.Min(_segment.Frames, end));
            _cursor = _selEnd;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        public void ClearSelection()
        {
            _selStart = _selEnd = _cursor;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        private int XToSample(int x)
        {
            double frac = Math.Max(0, Math.Min(1, (double)(x - 0) / Math.Max(1, Width)));
            return (int)Math.Round(_viewStart + frac * _viewSpan);
        }

        private float SampleToX(int sample)
        {
            return (float)((sample - _viewStart) / Math.Max(1, _viewSpan) * Width);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_segment == null) return;
            Focus();
            int s = Math.Min(_segment.Frames, Math.Max(0, XToSample(e.X)));
            if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Shift) != 0))
            {
                _panning = true;
                _panX = e.X;
                _panStart = _viewStart;
                return;
            }
            _dragging = true;
            _cursor = s;
            _selStart = s;
            _selEnd = s;
            Invalidate();
        }
        private bool _dragging, _panning;
        private int _panX;
        private double _panStart;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_segment == null) return;
            if (_panning)
            {
                double perPx = _viewSpan / Math.Max(1, Width);
                _viewStart = Math.Max(0, Math.Min(_segment.Frames - _viewSpan, _panStart - (e.X - _panX) * perPx));
                Invalidate();
                return;
            }
            if (!_dragging) return;
            _selEnd = Math.Min(_segment.Frames, Math.Max(0, XToSample(e.X)));
            _cursor = _selEnd;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            _panning = false;
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            SelectAll();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_segment == null) return;
            int anchor = XToSample(e.X);
            double factor = e.Delta > 0 ? 0.5 : 2.0;      // wheel up zooms in
            double span = Math.Max(32, Math.Min(_segment.Frames, _viewSpan * factor));
            double perPx = span / Math.Max(1, Width);
            _viewStart = Math.Max(0, Math.Min(_segment.Frames - span, anchor - e.X * perPx));
            _viewSpan = span;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (Width < 20 || Height < 20) return;

            int rulerH = 18;
            Rectangle wave = new Rectangle(0, rulerH, Width, Height - rulerH);

            using (SolidBrush b = new SolidBrush(Color.FromArgb(28, 29, 33))) g.FillRectangle(b, wave);

            if (_segment == null || _segment.Frames == 0)
            {
                using (Font f = new Font("Segoe UI", 9f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 152, 158)))
                    g.DrawString("Load a clip with \"Open clip...\" to start editing.", f, b, 12, rulerH + 12);
                return;
            }

            // selection
            if (HasSelection)
            {
                float x1 = SampleToX(SelectionStart), x2 = SampleToX(SelectionEnd);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 90, 160, 255)))
                    g.FillRectangle(b, x1, wave.Top, Math.Max(1, x2 - x1), wave.Height);
            }

            // waveform envelope
            float mid = wave.Top + wave.Height / 2f;
            float half = wave.Height / 2f - 4;
            using (Pen p = new Pen(Color.FromArgb(110, 200, 255), 1f))
            {
                for (int x = 0; x < wave.Width; x++)
                {
                    int a = (int)Math.Round(_viewStart + (double)x / wave.Width * _viewSpan);
                    int b = (int)Math.Round(_viewStart + (double)(x + 1) / wave.Width * _viewSpan);
                    if (a >= _segment.Frames) break;
                    if (b > _segment.Frames) b = _segment.Frames;
                    if (b <= a) b = a + 1;
                    float min = 1, max = -1;
                    for (int i = a; i < b; i++)
                        for (int ch = 0; ch < _segment.ChannelCount; ch++)
                        {
                            float v = _segment.Channels[ch][i];
                            if (v < min) min = v;
                            if (v > max) max = v;
                        }
                    if (min > max) continue;
                    g.DrawLine(p, x, mid - max * half, x, mid - min * half);
                }
            }

            using (Pen p = new Pen(Color.FromArgb(70, 72, 78), 1f)) g.DrawLine(p, 0, mid, wave.Width, mid);

            // cursor
            float cx = SampleToX(_cursor);
            using (Pen p = new Pen(Color.FromArgb(255, 180, 70), 1f)) g.DrawLine(p, cx, wave.Top, cx, wave.Bottom);

            // playback playhead
            if (_playPos >= 0)
            {
                float px = SampleToX(_playPos);
                using (Pen p = new Pen(Color.FromArgb(120, 230, 120), 2f)) g.DrawLine(p, px, wave.Top, px, wave.Bottom);
            }

            // ruler
            using (SolidBrush b = new SolidBrush(Color.FromArgb(34, 35, 40))) g.FillRectangle(b, 0, 0, Width, rulerH);
            using (Font f = new Font("Segoe UI", 6.8f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(170, 172, 180)))
            using (Pen p = new Pen(Color.FromArgb(80, 82, 88), 1f))
            {
                double secondsSpan = _viewSpan / _segment.Rate;
                double step = NiceStep(secondsSpan);
                double startSec = _viewStart / _segment.Rate;
                double first = Math.Ceiling(startSec / step) * step;
                for (double t = first; t <= startSec + secondsSpan; t += step)
                {
                    float x = SampleToX((int)Math.Round(t * _segment.Rate));
                    g.DrawLine(p, x, rulerH - 5, x, rulerH);
                    g.DrawString(t.ToString(step < 0.1 ? "0.000" : (step < 1 ? "0.0" : "0.0")) + "s", f, b, x + 2, 2);
                }
            }
        }

        private static double NiceStep(double span)
        {
            double[] steps = { 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 30 };
            foreach (double s in steps) if (span / s <= 8) return s;
            return 60;
        }
    }

    /// <summary>Chop, recombine, audition and render samples before converting them for the HPD-20.</summary>
    public sealed class WaveEditorPanel : UserControl
    {
        private readonly List<Segment> _segments = new List<Segment>();
        private readonly UndoHistory<EditorState> _history = new UndoHistory<EditorState>();
        private Button _btnUndo, _btnRedo;

        private sealed class EditorState
        {
            public List<Segment> Segments;
            public int Selected;
            public int SelStart, SelEnd;
            public bool HasSelection;
        }
        private Segment _clipboard;
        private ListBox _list;
        private WaveView _view;
        private Label _status;
        private ToolTip _tips;
        private NumericUpDown _numParts, _numThreshold, _numGap;

        /// <summary>WAV files produced by the last "Send to samples" press.</summary>
        public List<string> OutputFiles = new List<string>();

        /// <summary>Raised with the rendered WAV files when "Send to samples" is pressed.</summary>
        public event Action<List<string>> ClipsRendered;

        private readonly WavePlayer _player = new WavePlayer();
        private Timer _playTimer;
        private TrackBar _jog;
        private Label _lblTime;
        private Button _btnPlay, _btnStop;
        private int _playFrom;

        public WaveEditorPanel()
        {
            Font = new Font("Segoe UI", 9f);
            BuildUi();
            BuildTransport();
            AllowDrop = true;
            _view.AllowDrop = true;
            _list.AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            _view.DragEnter += OnDragEnter;
            _view.DragDrop += OnDragDrop;
            _list.DragEnter += OnDragEnter;
            _list.DragDrop += OnDragDrop;

            _playTimer = new Timer();
            _playTimer.Interval = 50;
            _playTimer.Tick += delegate { OnPlayTick(); };
        }

        private void BuildUi()
        {
            _tips = new ToolTip();
            _tips.AutoPopDelay = 30000;
            _tips.InitialDelay = 350;

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 98;

            int x1 = 6, x2 = 6, x3 = 6;
            Button b;

            b = AddButton(top, "Open clip...", ref x1, 4, 104,
                "Load an audio file (WAV, AIFF, CAF, AU, MP3, FLAC...) as a new clip in the list.",
                delegate { OpenClip(); });
            b = AddButton(top, "Append clip...", ref x1, 4, 104,
                "Load another file and add it to the end of the clip list, for recombining pieces.",
                delegate { AppendClip(); });
            b = AddButton(top, "Trim", ref x1, 4, 76,
                "Keep only the selection and discard the rest of this clip.",
                delegate { Apply(delegate(Segment s) { return HasSelection() ? WaveOps.Trim(s, SelStart(), SelEnd()) : s; }); });
            b = AddButton(top, "Cut", ref x1, 4, 66,
                "Remove the selection and put it on the clipboard.",
                delegate { Cut(); });
            b = AddButton(top, "Copy", ref x1, 4, 66,
                "Copy the selection to the clipboard without removing it.",
                delegate { Copy(); });
            b = AddButton(top, "Paste", ref x1, 4, 66,
                "Insert the clipboard at the cursor, or replace the selection with it.",
                delegate { Paste(); });
            b = AddButton(top, "Delete", ref x1, 4, 76,
                "Remove the selection and close the gap.",
                delegate { Del(); });
            b = AddButton(top, "Silence", ref x1, 4, 80,
                "Replace the selection with silence, keeping the timing.",
                delegate { Apply(delegate(Segment s) { return HasSelection() ? WaveOps.Silence(s, SelStart(), SelEnd()) : s; }); });

            b = AddButton(top, "Fade in", ref x2, 34, 80,
                "Fade the selection in from silence.",
                delegate { Apply(delegate(Segment s) { return HasSelection() ? WaveOps.FadeIn(s, SelStart(), SelEnd()) : s; }); });
            b = AddButton(top, "Fade out", ref x2, 34, 80,
                "Fade the selection out to silence.",
                delegate { Apply(delegate(Segment s) { return HasSelection() ? WaveOps.FadeOut(s, SelStart(), SelEnd()) : s; }); });
            b = AddButton(top, "Reverse", ref x2, 34, 80,
                "Reverse the whole clip.",
                delegate { Apply(delegate(Segment s) { return WaveOps.Reverse(s); }); });
            b = AddButton(top, "Normalize", ref x2, 34, 88,
                "Raise the clip's peak to just below full scale.",
                delegate { Apply(delegate(Segment s) { return WaveOps.Normalize(s, 0.98f); }); });
            b = AddButton(top, "Move up", ref x2, 34, 72,
                "Move the selected clip earlier in the render order.",
                delegate { MoveSegment(-1); });
            b = AddButton(top, "Move down", ref x2, 34, 82,
                "Move the selected clip later in the render order.",
                delegate { MoveSegment(1); });
            b = AddButton(top, "Remove", ref x2, 34, 72,
                "Drop the selected clip from the list.",
                delegate { RemoveSegment(); });
            b = AddButton(top, "Send to samples", ref x2, 34, 116,
                "Render every clip in order to WAV and hand them to the Samples tab, where they are converted to 44.1 kHz / 16-bit for the HPD-20.",
                delegate { Render(); });


            _btnUndo = AddButton(top, "Undo", ref x1, 4, 76,
                "Step back through edits (Ctrl+Z). The history keeps the last 30 changes.",
                delegate { Undo(); });
            _btnUndo.Enabled = false;
            _btnRedo = AddButton(top, "Redo", ref x1, 4, 76,
                "Step forward again after undoing (Ctrl+Y or Ctrl+Shift+Z).",
                delegate { Redo(); });
            _btnRedo.Enabled = false;

            Label lParts = new Label();
            lParts.Text = "Slice into";
            lParts.SetBounds(x3, 68, 56, 20);
            top.Controls.Add(lParts);
            x3 += 58;
            _numParts = new NumericUpDown();
            _numParts.Minimum = 1;
            _numParts.Maximum = 128;
            _numParts.Value = 8;
            _numParts.SetBounds(x3, 66, 52, 22);
            top.Controls.Add(_numParts);
            _tips.SetToolTip(_numParts, "How many equal pieces to cut the current clip into.");
            x3 += 56;
            b = AddButton(top, "Slice", ref x3, 65, 60,
                "Cut the current clip into that many equal pieces. Each piece becomes its own clip, so you can keep, reorder or delete them.",
                delegate { SliceInto(); });

            Label lThreshold = new Label();
            lThreshold.Text = "Silence dB";
            lThreshold.SetBounds(x3 + 6, 68, 68, 20);
            top.Controls.Add(lThreshold);
            x3 += 74;
            _numThreshold = new NumericUpDown();
            _numThreshold.Minimum = -80;
            _numThreshold.Maximum = -10;
            _numThreshold.Value = -40;
            _numThreshold.SetBounds(x3, 66, 52, 22);
            top.Controls.Add(_numThreshold);
            _tips.SetToolTip(_numThreshold, "Level below which audio counts as silence when auto-slicing. -40 dB suits most one-shots.");
            x3 += 58;
            Label lGap = new Label();
            lGap.Text = "gap ms";
            lGap.SetBounds(x3 + 6, 68, 52, 20);
            top.Controls.Add(lGap);
            x3 += 64;
            _numGap = new NumericUpDown();
            _numGap.Minimum = 5;
            _numGap.Maximum = 2000;
            _numGap.Value = 60;
            _numGap.Increment = 5;
            _numGap.SetBounds(x3, 66, 56, 22);
            top.Controls.Add(_numGap);
            _tips.SetToolTip(_numGap, "Shortest silence that becomes a cut when auto-slicing. Raise it to avoid cutting inside a sound.");
            x3 += 62;
            b = AddButton(top, "Auto-slice", ref x3, 65, 84,
                "Cut the current clip at every silence longer than the gap. Useful for a drum loop or a pack recorded as one file.",
                delegate { AutoSlice(); });

            b = AddButton(top, "Trim silence", ref x3, 65, 92,
                "Remove silence from the start and end of every clip selected in the list on the left, using the Silence dB level above. Undo (Ctrl+Z) puts it back.",
                delegate { TrimSilenceSelected(); });

            _list = new ListBox();
            _list.Dock = DockStyle.Left;
            _list.Width = 210;
            _list.IntegralHeight = false;
            _list.SelectionMode = SelectionMode.MultiExtended;
            _list.SelectedIndexChanged += delegate { ShowSelected(); };
            _tips.SetToolTip(_list, "The clips in render order. The selected one is shown in the waveform; use Move up/down to recombine them in a different order.");

            _view = new WaveView();
            _view.Dock = DockStyle.Fill;
            _view.SelectionChanged += delegate { UpdateStatus(); };
            _tips.SetToolTip(_view, "Drag to select, wheel to zoom, shift-drag or middle-drag to pan, double-click to select all. Operations apply to the selection, or to the whole clip where they say so.");

            _status = new Label();
            _status.Dock = DockStyle.Bottom;
            _status.Height = 22;
            _status.Padding = new Padding(8, 0, 0, 0);
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.ForeColor = Color.FromArgb(150, 152, 160);
            _status.Text = "No clip loaded.";

            Splitter listSplitter = new Splitter();
            listSplitter.Dock = DockStyle.Left;
            listSplitter.Width = 7;
            listSplitter.BackColor = Color.FromArgb(180, 182, 188);
            Controls.Add(_view);
            Controls.Add(listSplitter);
            Controls.Add(_list);
            Controls.Add(_status);
            Controls.Add(top);
        }

        /// <summary>Play / Stop / jog row, docked above the status line.</summary>
        private void BuildTransport()
        {
            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = 30;

            _btnPlay = new Button();
            _btnPlay.Text = "Play";
            _btnPlay.SetBounds(6, 3, 64, 24);
            _btnPlay.Click += delegate { Play(); };
            _tips.SetToolTip(_btnPlay, "Audition the current clip, or the selection if there is one, starting at the cursor. Space also starts and stops.");

            _btnStop = new Button();
            _btnStop.Text = "Stop";
            _btnStop.SetBounds(74, 3, 64, 24);
            _btnStop.Click += delegate { Stop(); };
            _tips.SetToolTip(_btnStop, "Stop playback and leave the playhead where it is.");

            _jog = new TrackBar();
            _jog.SetBounds(146, 0, 560, 28);
            _jog.TickStyle = TickStyle.None;
            _jog.Minimum = 0;
            _jog.Maximum = 0;
            _jog.Scroll += delegate { JogTo(_jog.Value); };
            _jog.MouseUp += delegate { if (!_player.IsPlaying) _view.SetPlayPosition(-1); };
            _tips.SetToolTip(_jog, "Jog: drag to scrub through the clip. While playing, the audio restarts from the position you drag to.");

            _lblTime = new Label();
            _lblTime.SetBounds(714, 5, 220, 20);
            _lblTime.Text = "0.000 s";
            _lblTime.ForeColor = Color.FromArgb(150, 152, 160);
            _tips.SetToolTip(_lblTime, "Playhead position and length of the current clip.");

            bar.Controls.AddRange(new Control[] { _btnPlay, _btnStop, _jog, _lblTime });

            Controls.Add(bar);
            // docking order: the status line must stay the outermost bottom element
            Controls.SetChildIndex(bar, 2);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_jog == null || _lblTime == null) return;
            int right = Math.Max(220, Width - 240);
            int jogWidth = Math.Max(160, right - 146);
            _jog.Width = jogWidth;
            if (_lblTime.Parent != null && _lblTime.Parent.Width > 0)
                _lblTime.Left = Math.Max(_jog.Right + 12, _lblTime.Parent.Width - 230);
        }

        private void Play()
        {
            Segment s = Current;
            if (s == null || s.Frames == 0) return;
            int start = _view.HasSelection ? _view.SelectionStart : _view.Playhead;
            int end = _view.HasSelection ? _view.SelectionEnd : s.Frames;
            if (end <= start) { start = 0; end = s.Frames; }
            _playFrom = start;
            _jog.Minimum = 0;
            _jog.Maximum = Math.Max(1, s.Frames);
            if (start <= _jog.Maximum) _jog.Value = start;
            short[] pcm = PlaybackBuffer.ToPcm16(s, start, end);
            PlaybackBuffer.FadeEdges(pcm, Math.Max(1, s.ChannelCount), s.Rate, 3.0);
            _player.Play(pcm, s.Rate, Math.Max(1, s.ChannelCount));
            _playTimer.Start();
        }

        private void Stop()
        {
            _player.Stop();
            _playTimer.Stop();
            _view.SetPlayPosition(-1);
            UpdateTime();
        }

        private void JogTo(int frame)
        {
            Segment s = Current;
            if (s == null) return;
            _view.SetPlayPosition(frame);
            _view.SetCursor(frame);
            if (_player.IsPlaying)
            {
                int end = _view.HasSelection ? _view.SelectionEnd : s.Frames;
                if (end <= frame) end = s.Frames;
                _playFrom = frame;
                short[] pcm = PlaybackBuffer.ToPcm16(s, frame, end);
                PlaybackBuffer.FadeEdges(pcm, Math.Max(1, s.ChannelCount), s.Rate, 3.0);
                _player.Play(pcm, s.Rate, Math.Max(1, s.ChannelCount));
            }
            UpdateTime();
        }

        private void OnPlayTick()
        {
            Segment s = Current;
            if (s == null || !_player.IsPlaying)
            {
                _playTimer.Stop();
                _view.SetPlayPosition(-1);
                UpdateTime();
                return;
            }
            int at = _playFrom + (int)_player.PlayedSamples;
            if (at > s.Frames) at = s.Frames;
            _view.SetPlayPosition(at);
            if (at > 0 && at < _jog.Maximum) _jog.Value = at;
            UpdateTime();
        }

        private void UpdateTime()
        {
            Segment s = Current;
            if (s == null) { _lblTime.Text = "0.000 s"; return; }
            double at = _jog.Value / (double)Math.Max(1, s.Rate);
            _lblTime.Text = at.ToString("0.000") + " s / " + s.Seconds.ToString("0.000") + " s";
        }

        private Button AddButton(Control parent, string text, ref int x, int y, int width, string tip, EventHandler handler)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, width, 26);
            b.Click += handler;
            parent.Controls.Add(b);
            _tips.SetToolTip(b, tip);
            x += width + 6;
            return b;
        }
        private void _tipsSet(Control c, string text)
        {
            if (_tips == null) _tips = new ToolTip();
            _tips.SetToolTip(c, text);
        }

        // ------------------------------ helpers ------------------------------

        private Segment Current
        {
            get
            {
                int i = _list.SelectedIndex;
                return (i >= 0 && i < _segments.Count) ? _segments[i] : null;
            }
        }

        private bool HasSelection() { return _view.HasSelection; }
        private int SelStart() { return _view.SelectionStart; }
        private int SelEnd() { return _view.SelectionEnd; }

        /// <summary>Preloads a file when the editor is opened from the Samples tab.</summary>
        public void LoadFile(string path) { LoadClip(path, false); }

        /// <summary>Appends a file to the clip list, for drag-and-drop.</summary>
        public void AppendFile(string path) { LoadClip(path, true); }

        private static string[] DroppedFiles(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            return (string[])e.Data.GetData(DataFormats.FileDrop);
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            string[] files = DroppedFiles(e);
            bool ok = false;
            if (files != null)
                foreach (string f in files) if (AudioFormats.IsSupportedExtension(f)) { ok = true; break; }
            e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            string[] files = DroppedFiles(e);
            if (files == null) return;
            List<string> audio = new List<string>();
            foreach (string f in files) if (AudioFormats.IsSupportedExtension(f) && File.Exists(f)) audio.Add(f);
            if (audio.Count == 0) return;

            // drop onto the clip list appends; anywhere else replaces the current clip
            bool append = (sender == _list) || (sender == this && _segments.Count > 0);
            LoadClip(audio[0], append);
            for (int i = 1; i < audio.Count; i++) LoadClip(audio[i], true);
            _status.Text = "Loaded " + audio.Count + " dropped file(s).";
        }

        private void OpenClip()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = AudioFormats.Filter;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                LoadClip(d.FileName, false);
            }
        }

        private void AppendClip()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = AudioFormats.Filter;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                LoadClip(d.FileName, true);
            }
        }

        private void LoadClip(string path, bool append)
        {
            try
            {
                WavData d = AudioFormats.Read(path);
                Segment s = Segment.From(d, Path.GetFileNameWithoutExtension(path));
                PushUndo();
                if (!append) { _segments.Clear(); _clipboard = null; }
                _segments.Add(s);
                RefreshList();
                _list.SelectedIndex = _segments.Count - 1;
                if (append) _status.Text = "Appended " + s.Name + " (" + s.Seconds.ToString("0.000") + " s)";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Cannot open this file:\r\n\r\n" + ex.Message, "Waveform editor",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RefreshList()
        {
            int keep = _list.SelectedIndex;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (Segment s in _segments)
                _list.Items.Add(s.Name + "   " + s.Seconds.ToString("0.000") + " s");
            _list.EndUpdate();
            if (keep >= 0 && keep < _list.Items.Count) _list.SelectedIndex = keep;
        }

        private EditorState Snapshot()
        {
            EditorState s = new EditorState();
            s.Segments = new List<Segment>(_segments);
            s.Selected = _list.SelectedIndex;
            s.HasSelection = _view.HasSelection;
            s.SelStart = _view.SelectionStart;
            s.SelEnd = _view.SelectionEnd;
            return s;
        }

        private static long MeasureState(EditorState s)
        {
            long bytes = 0;
            foreach (Segment seg in s.Segments)
                bytes += (long)seg.Frames * Math.Max(1, seg.ChannelCount) * 4;
            return bytes;
        }

        /// <summary>Records the current state so the next change can be undone.</summary>
        private void PushUndo()
        {
            if (_history.Measure == null) _history.Measure = MeasureState;
            _history.Push(Snapshot());
            UpdateUndoButtons();
        }

        private void UpdateUndoButtons()
        {
            if (_btnUndo != null)
            {
                _btnUndo.Enabled = _history.CanUndo;
                _btnUndo.Text = _history.CanUndo ? "Undo (" + _history.UndoDepth + ")" : "Undo";
            }
            if (_btnRedo != null)
            {
                _btnRedo.Enabled = _history.CanRedo;
                _btnRedo.Text = _history.CanRedo ? "Redo (" + _history.RedoDepth + ")" : "Redo";
            }
        }

        private void Undo()
        {
            if (!_history.CanUndo) return;
            EditorState s = _history.Undo(Snapshot());
            Restore(s);
            _status.Text = "Undone.";
        }

        private void Redo()
        {
            if (!_history.CanRedo) return;
            EditorState s = _history.Redo(Snapshot());
            Restore(s);
            _status.Text = "Redone.";
        }

        private void Restore(EditorState s)
        {
            _segments.Clear();
            _segments.AddRange(s.Segments);
            RefreshList();
            if (_segments.Count == 0) { _view.Segment = null; UpdateUndoButtons(); UpdateStatus(); return; }
            int index = Math.Max(0, Math.Min(_segments.Count - 1, s.Selected));
            _list.SelectedIndex = index;
            _view.Segment = Current;
            if (s.HasSelection) _view.SetSelection(s.SelStart, s.SelEnd);
            else _view.ClearSelection();
            UpdateUndoButtons();
            UpdateStatus();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Space) { if (_player.IsPlaying) Stop(); else Play(); return true; }
            if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
            if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { Redo(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ShowSelected()
        {
            _view.Segment = Current;
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            Segment s = Current;
            if (s == null) { _status.Text = "No clip loaded."; return; }
            string sel = "";
            if (_view.HasSelection)
                sel = "   selection " + (SelStart() / (double)s.Rate).ToString("0.000") + "s - " +
                      (SelEnd() / (double)s.Rate).ToString("0.000") + "s (" +
                      ((SelEnd() - SelStart()) / (double)s.Rate).ToString("0.000") + "s)";
            _status.Text = s.Name + "   " + s.Rate + " Hz, " + s.ChannelCount + " ch, " +
                           s.Frames + " frames, " + s.Seconds.ToString("0.000") + " s" + sel;
        }

        private void Apply(Func<Segment, Segment> op)
        {
            Segment s = Current;
            if (s == null) return;
            PushUndo();
            if (s == null) return;
            Segment r = op(s);
            int i = _list.SelectedIndex;
            _segments[i] = r;
            RefreshList();
            _list.SelectedIndex = i;
            _view.Segment = r;
            UpdateStatus();
        }

        private void Copy()
        {
            Segment s = Current;
            if (s == null || !HasSelection()) return;
            _clipboard = WaveOps.Trim(s, SelStart(), SelEnd());
            _status.Text = "Copied " + _clipboard.Seconds.ToString("0.000") + " s to the clipboard.";
        }

        private void Cut()
        {
            Segment s = Current;
            if (s == null || !HasSelection()) return;
            _clipboard = WaveOps.Trim(s, SelStart(), SelEnd());
            Apply(delegate(Segment x) { return WaveOps.Delete(x, SelStart(), SelEnd()); });
        }

        private void Del()
        {
            Apply(delegate(Segment x) { return HasSelection() ? WaveOps.Delete(x, SelStart(), SelEnd()) : x; });
        }

        private void Paste()
        {
            Segment s = Current;
            if (s == null || _clipboard == null) return;
            int at = HasSelection() ? SelStart() : _view.Playhead;
            Apply(delegate(Segment x)
            {
                return HasSelection()
                    ? WaveOps.Replace(x, SelStart(), SelEnd(), _clipboard)
                    : WaveOps.Insert(x, _clipboard, at);
            });
        }

        private void SliceInto()
        {
            Segment s = Current;
            if (s == null) return;
            PushUndo();
            List<Segment> parts = WaveOps.SliceInto(s, (int)_numParts.Value);
            int i = _list.SelectedIndex;
            _segments.RemoveAt(i);
            _segments.InsertRange(i, parts);
            RefreshList();
            _list.SelectedIndex = i;
            _status.Text = "Sliced into " + parts.Count + " piece(s).";
        }

        private void AutoSlice()
        {
            Segment s = Current;
            if (s == null) return;
            PushUndo();
            List<Segment> parts = WaveOps.SliceBySilence(s, (float)_numThreshold.Value, (int)_numGap.Value);
            int i = _list.SelectedIndex;
            _segments.RemoveAt(i);
            _segments.InsertRange(i, parts);
            RefreshList();
            _list.SelectedIndex = i;
            _status.Text = "Auto-sliced into " + parts.Count + " piece(s).";
        }

        /// <summary>Trims leading and trailing silence from every selected clip in one undoable step.</summary>
        private void TrimSilenceSelected()
        {
            if (_segments.Count == 0) { _status.Text = "No clips to trim."; return; }

            List<int> targets = new List<int>();
            foreach (int i in _list.SelectedIndices) targets.Add(i);
            if (targets.Count == 0 && _list.SelectedIndex >= 0) targets.Add(_list.SelectedIndex);
            if (targets.Count == 0) { _status.Text = "Select one or more clips first."; return; }

            PushUndo();
            float threshold = (float)_numThreshold.Value;
            double before = 0, after = 0;
            int changed = 0;

            foreach (int i in targets)
            {
                if (i < 0 || i >= _segments.Count) continue;
                Segment original = _segments[i];
                Segment trimmed = WaveOps.TrimSilence(original, threshold, 5);
                if (trimmed == null) continue;
                before += original.Seconds;
                after += trimmed.Seconds;
                if (trimmed.Frames != original.Frames) changed++;
                _segments[i] = trimmed;
            }

            RefreshList();
            foreach (int i in targets) if (i >= 0 && i < _list.Items.Count) _list.SetSelected(i, true);
            if (_list.SelectedIndex < 0 && _list.Items.Count > 0) _list.SelectedIndex = 0;

            double removed = before - after;
            _status.Text = "Trimmed silence from " + targets.Count + " clip(s): " +
                           removed.ToString("0.000") + " s removed" +
                           (changed == 0 ? " (nothing was below the threshold)" : "") +
                           ". Ctrl+Z restores it.";
        }

        private void MoveSegment(int delta)
        {
            int i = _list.SelectedIndex;
            int j = i + delta;
            if (i < 0 || j < 0 || j >= _segments.Count) return;
            PushUndo();
            Segment s = _segments[i];
            _segments.RemoveAt(i);
            _segments.Insert(j, s);
            RefreshList();
            _list.SelectedIndex = j;
        }

        private void RemoveSegment()
        {
            int i = _list.SelectedIndex;
            if (i < 0) return;
            PushUndo();
            _segments.RemoveAt(i);
            RefreshList();
            if (_segments.Count > 0) _list.SelectedIndex = Math.Min(i, _segments.Count - 1);
            else { _view.Segment = null; UpdateStatus(); }
        }

        private void Render()
        {
            if (_segments.Count == 0) { MessageBox.Show(this, "Nothing to render.", "Waveform editor"); return; }
            string dir = Path.Combine(Path.GetTempPath(), "hpd20-wave");
            Directory.CreateDirectory(dir);
            OutputFiles = new List<string>();
            try
            {
                for (int i = 0; i < _segments.Count; i++)
                {
                    Segment s = _segments[i];
                    if (s.Frames <= 0) continue;
                    string safe = Sanitize(s.Name);
                    string p = Path.Combine(dir, (i + 1).ToString("00") + "_" + safe + ".wav");
                    WavFile.Write16(p, s.Channels, s.Rate);
                    OutputFiles.Add(p);
                }
                if (ClipsRendered != null) ClipsRendered(OutputFiles);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Render failed: " + ex.Message, "Waveform editor");
            }
        }

        private static string Sanitize(string name)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (char c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            string s = sb.ToString();
            return s.Length > 40 ? s.Substring(0, 40) : (s.Length == 0 ? "clip" : s);
        }
    }
}













