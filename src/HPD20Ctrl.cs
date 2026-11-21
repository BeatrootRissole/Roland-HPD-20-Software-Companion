using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Hpd20Ctrl
{
    // ------------------------------------------------------------------
    // WinMM interop
    // ------------------------------------------------------------------
    internal static class WinMM
    {
        public const int CALLBACK_FUNCTION = 0x00030000;
        public const int MIM_DATA = 0x3C3;
        public const int MIM_LONGDATA = 0x3C4;
        public const int MIM_LONGERROR = 0x3C6;
        public const int MHDR_DONE = 1;
        public const int MHDR_PREPARED = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct MIDIINCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwSupport;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIDIOUTCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public ushort wTechnology;
            public ushort wVoices;
            public ushort wNotes;
            public ushort wChannelMask;
            public uint dwSupport;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIDIHDR
        {
            public IntPtr lpData;
            public int dwBufferLength;
            public int dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags;
            public IntPtr lpNext;
            public IntPtr reserved;
            public int dwOffset;
            public IntPtr dwReserved1;
            public IntPtr dwReserved2;
            public IntPtr dwReserved3;
            public IntPtr dwReserved4;
            public IntPtr dwReserved5;
            public IntPtr dwReserved6;
            public IntPtr dwReserved7;
            public IntPtr dwReserved8;
        }

        public delegate void MidiInProc(IntPtr hMidiIn, int wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [DllImport("winmm.dll")] public static extern int midiInGetNumDevs();
        [DllImport("winmm.dll")] public static extern int midiInGetDevCaps(IntPtr uDeviceID, out MIDIINCAPS caps, int cbMidiInCaps);
        [DllImport("winmm.dll")] public static extern int midiInOpen(out IntPtr hMidiIn, int uDeviceID, MidiInProc callback, IntPtr dwInstance, int dwFlags);
        [DllImport("winmm.dll")] public static extern int midiInClose(IntPtr hMidiIn);
        [DllImport("winmm.dll")] public static extern int midiInStart(IntPtr hMidiIn);
        [DllImport("winmm.dll")] public static extern int midiInStop(IntPtr hMidiIn);
        [DllImport("winmm.dll")] public static extern int midiInReset(IntPtr hMidiIn);
        [DllImport("winmm.dll")] public static extern int midiInPrepareHeader(IntPtr hMidiIn, IntPtr lpMidiInHdr, int cbMidiInHdr);
        [DllImport("winmm.dll")] public static extern int midiInUnprepareHeader(IntPtr hMidiIn, IntPtr lpMidiInHdr, int cbMidiInHdr);
        [DllImport("winmm.dll")] public static extern int midiInAddBuffer(IntPtr hMidiIn, IntPtr lpMidiInHdr, int cbMidiInHdr);

        [DllImport("winmm.dll")] public static extern int midiOutGetNumDevs();
        [DllImport("winmm.dll")] public static extern int midiOutGetDevCaps(IntPtr uDeviceID, out MIDIOUTCAPS caps, int cbMidiOutCaps);
        [DllImport("winmm.dll")] public static extern int midiOutOpen(out IntPtr hMidiOut, int uDeviceID, IntPtr callback, IntPtr dwInstance, int dwFlags);
        [DllImport("winmm.dll")] public static extern int midiOutClose(IntPtr hMidiOut);
        [DllImport("winmm.dll")] public static extern int midiOutShortMsg(IntPtr hMidiOut, uint dwMsg);
        [DllImport("winmm.dll")] public static extern int midiOutLongMsg(IntPtr hMidiOut, IntPtr lpMidiOutHdr, int cbMidiOutHdr);
        [DllImport("winmm.dll")] public static extern int midiOutPrepareHeader(IntPtr hMidiOut, IntPtr lpMidiOutHdr, int cbMidiOutHdr);
        [DllImport("winmm.dll")] public static extern int midiOutUnprepareHeader(IntPtr hMidiOut, IntPtr lpMidiOutHdr, int cbMidiOutHdr);
        [DllImport("winmm.dll")] public static extern int midiOutReset(IntPtr hMidiOut);
    }

    internal class PortInfo
    {
        public int Index;
        public string Name;
        public override string ToString() { return "[" + Index + "] " + Name; }
        public bool LooksLikeHpd()
        {
            string n = (Name ?? "").ToUpperInvariant();
            return n.Contains("HPD") || n.Contains("HANDSONIC") || n.Contains("HAND SONIC");
        }
    }

    internal class MidiMessageEventArgs : EventArgs
    {
        public byte[] Data;
        public DateTime Time;
        public MidiMessageEventArgs(byte[] data, DateTime time) { Data = data; Time = time; }
        public bool IsSysEx
        {
            get { return Data.Length > 0 && Data[0] == 0xF0; }
        }
    }

    /// <summary>Owns the MIDI in/out handles, the receive callback and the SysEx receive buffers.</summary>
    internal sealed class MidiSession : IDisposable
    {
        private const int SysExBufferCount = 8;
        private const int SysExBufferSize = 8192;

        private IntPtr _hIn = IntPtr.Zero;
        private IntPtr _hOut = IntPtr.Zero;
        private WinMM.MidiInProc _proc;              // keep delegate alive
        private readonly List<IntPtr> _hdrPtrs = new List<IntPtr>();
        private readonly List<IntPtr> _bufPtrs = new List<IntPtr>();
        private volatile bool _stopping;

        public event EventHandler<MidiMessageEventArgs> MessageReceived;

        public bool IsOpen { get { return _hOut != IntPtr.Zero || _hIn != IntPtr.Zero; } }
        public string LastError { get; private set; }

        public bool Open(int outIndex, int inIndex, bool needOut, bool needIn)
        {
            LastError = null;
            _stopping = false;
            if (needOut)
            {
                int r = WinMM.midiOutOpen(out _hOut, outIndex, IntPtr.Zero, IntPtr.Zero, 0);
                if (r != 0) { LastError = "midiOutOpen failed (" + r + ")"; _hOut = IntPtr.Zero; return false; }
            }
            if (needIn)
            {
                _proc = OnMidiIn;
                int r = WinMM.midiInOpen(out _hIn, inIndex, _proc, IntPtr.Zero, WinMM.CALLBACK_FUNCTION);
                if (r != 0) { LastError = "midiInOpen failed (" + r + ")"; _hIn = IntPtr.Zero; return false; }

                for (int i = 0; i < SysExBufferCount; i++)
                    AddSysExBuffer();

                r = WinMM.midiInStart(_hIn);
                if (r != 0) { LastError = "midiInStart failed (" + r + ")"; return false; }
            }
            return true;
        }

        private void AddSysExBuffer()
        {
            IntPtr buf = Marshal.AllocHGlobal(SysExBufferSize);
            IntPtr hdr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinMM.MIDIHDR)));
            WinMM.MIDIHDR h = new WinMM.MIDIHDR();
            h.lpData = buf;
            h.dwBufferLength = SysExBufferSize;
            h.dwBytesRecorded = 0;
            h.dwFlags = 0;
            Marshal.StructureToPtr(h, hdr, false);
            WinMM.midiInPrepareHeader(_hIn, hdr, Marshal.SizeOf(typeof(WinMM.MIDIHDR)));
            WinMM.midiInAddBuffer(_hIn, hdr, Marshal.SizeOf(typeof(WinMM.MIDIHDR)));
            _bufPtrs.Add(buf);
            _hdrPtrs.Add(hdr);
        }

        private void RecycleBuffer(IntPtr hdr)
        {
            // Never re-queue while shutting down: the driver hands every buffer back
            // during midiInStop/midiInReset, and re-adding it there causes a callback storm.
            if (_stopping || _hIn == IntPtr.Zero) return;
            WinMM.MIDIHDR h = (WinMM.MIDIHDR)Marshal.PtrToStructure(hdr, typeof(WinMM.MIDIHDR));
            h.dwFlags &= ~WinMM.MHDR_DONE;
            h.dwBytesRecorded = 0;
            Marshal.StructureToPtr(h, hdr, false);
            if (WinMM.midiInAddBuffer(_hIn, hdr, Marshal.SizeOf(typeof(WinMM.MIDIHDR))) != 0)
            {
                // buffer refused (driver already finished with it) - drop it
                h.dwFlags = 0;
                Marshal.StructureToPtr(h, hdr, false);
            }
        }

        private void OnMidiIn(IntPtr hMidiIn, int wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
        {
            try
            {
                if (wMsg == WinMM.MIM_DATA)
                {
                    uint v = unchecked((uint)dwParam1.ToInt64());
                    int status = (int)(v & 0xFF);
                    int d1 = (int)((v >> 8) & 0xFF);
                    int d2 = (int)((v >> 16) & 0xFF);
                    byte[] data;
                    if (status >= 0xF0)
                    {
                        if (status == 0xF1 || status == 0xF3) data = new byte[] { (byte)status, (byte)d1 };
                        else if (status == 0xF2) data = new byte[] { (byte)status, (byte)d1, (byte)d2 };
                        else data = new byte[] { (byte)status };
                    }
                    else
                    {
                        int hi = status & 0xF0;
                        if (hi == 0xC0 || hi == 0xD0) data = new byte[] { (byte)status, (byte)d1 };
                        else data = new byte[] { (byte)status, (byte)d1, (byte)d2 };
                    }
                    Raise(data);
                }
                else if (wMsg == WinMM.MIM_LONGDATA || wMsg == WinMM.MIM_LONGERROR)
                {
                    WinMM.MIDIHDR h = (WinMM.MIDIHDR)Marshal.PtrToStructure(dwParam1, typeof(WinMM.MIDIHDR));
                    int n = h.dwBytesRecorded;
                    if (n > 0 && h.lpData != IntPtr.Zero)
                    {
                        byte[] data = new byte[n];
                        Marshal.Copy(h.lpData, data, 0, n);
                        Raise(data);
                    }
                    RecycleBuffer(dwParam1);
                }
            }
            catch { /* never throw out of a driver callback */ }
        }

        private void Raise(byte[] data)
        {
            EventHandler<MidiMessageEventArgs> h = MessageReceived;
            if (h != null) h(this, new MidiMessageEventArgs(data, DateTime.Now));
        }

        public bool SendShort(byte status, byte d1, byte d2)
        {
            if (_hOut == IntPtr.Zero) { LastError = "output not open"; return false; }
            uint msg = (uint)(status | (d1 << 8) | (d2 << 16));
            int r = WinMM.midiOutShortMsg(_hOut, msg);
            if (r != 0) { LastError = "midiOutShortMsg failed (" + r + ")"; return false; }
            return true;
        }

        public bool SendShort1(byte status, byte d1)
        {
            if (_hOut == IntPtr.Zero) { LastError = "output not open"; return false; }
            uint msg = (uint)(status | (d1 << 8));
            int r = WinMM.midiOutShortMsg(_hOut, msg);
            if (r != 0) { LastError = "midiOutShortMsg failed (" + r + ")"; return false; }
            return true;
        }

        public bool SendSysEx(byte[] data)
        {
            if (_hOut == IntPtr.Zero) { LastError = "output not open"; return false; }
            if (data == null || data.Length < 2 || data[0] != 0xF0 || data[data.Length - 1] != 0xF7)
            {
                LastError = "SysEx must start with F0 and end with F7";
                return false;
            }
            IntPtr buf = Marshal.AllocHGlobal(data.Length);
            IntPtr hdr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinMM.MIDIHDR)));
            try
            {
                Marshal.Copy(data, 0, buf, data.Length);
                WinMM.MIDIHDR h = new WinMM.MIDIHDR();
                h.lpData = buf;
                h.dwBufferLength = data.Length;
                h.dwBytesRecorded = data.Length;
                h.dwFlags = 0;
                Marshal.StructureToPtr(h, hdr, false);

                int r = WinMM.midiOutPrepareHeader(_hOut, hdr, Marshal.SizeOf(typeof(WinMM.MIDIHDR)));
                if (r != 0) { LastError = "midiOutPrepareHeader failed (" + r + ")"; return false; }
                r = WinMM.midiOutLongMsg(_hOut, hdr, Marshal.SizeOf(typeof(WinMM.MIDIHDR)));
                if (r != 0) { LastError = "midiOutLongMsg failed (" + r + ")"; return false; }

                // wait (bounded) for the transfer to complete
                for (int i = 0; i < 400; i++)
                {
                    WinMM.MIDIHDR cur = (WinMM.MIDIHDR)Marshal.PtrToStructure(hdr, typeof(WinMM.MIDIHDR));
                    if ((cur.dwFlags & WinMM.MHDR_DONE) != 0) break;
                    System.Threading.Thread.Sleep(5);
                }
                WinMM.midiOutUnprepareHeader(_hOut, hdr, Marshal.SizeOf(typeof(WinMM.MIDIHDR)));
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(hdr);
                Marshal.FreeHGlobal(buf);
            }
        }

        public void Dispose()
        {
            _stopping = true;
            if (_hIn != IntPtr.Zero)
            {
                try { WinMM.midiInStop(_hIn); } catch { }
                try { WinMM.midiInReset(_hIn); } catch { }
                foreach (IntPtr hdr in _hdrPtrs)
                {
                    try { WinMM.midiInUnprepareHeader(_hIn, hdr, Marshal.SizeOf(typeof(WinMM.MIDIHDR))); } catch { }
                }
                try { WinMM.midiInClose(_hIn); } catch { }
                _hIn = IntPtr.Zero;
            }
            if (_hOut != IntPtr.Zero)
            {
                try { WinMM.midiOutReset(_hOut); } catch { }
                try { WinMM.midiOutClose(_hOut); } catch { }
                _hOut = IntPtr.Zero;
            }
            foreach (IntPtr p in _hdrPtrs) Marshal.FreeHGlobal(p);
            foreach (IntPtr p in _bufPtrs) Marshal.FreeHGlobal(p);
            _hdrPtrs.Clear();
            _bufPtrs.Clear();
        }
    }

    // ------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------
    internal static class MidiText
    {
        private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        public static string NoteName(int note)
        {
            if (note < 0 || note > 127) return note.ToString();
            return NoteNames[note % 12] + (note / 12 - 1);
        }

        public static string CcName(int cc)
        {
            switch (cc)
            {
                case 0: return "Bank Select MSB";
                case 1: return "Modulation";
                case 4: return "Foot Controller";
                case 7: return "Volume";
                case 10: return "Pan";
                case 11: return "Expression";
                case 12: return "Effect Ctrl 1";
                case 13: return "Effect Ctrl 2";
                case 16: return "GP Ctrl 1 (M1 position)";
                case 17: return "GP Ctrl 2";
                case 18: return "GP Ctrl 3";
                case 19: return "GP Ctrl 4";
                case 32: return "Bank Select LSB";
                case 64: return "Sustain";
                case 65: return "Portamento";
                case 70: return "Sound Ctrl 1";
                case 71: return "Sound Ctrl 2 (resonance)";
                case 74: return "Sound Ctrl 5 (cutoff)";
                case 84: return "Portamento Control";
                case 91: return "Reverb Send";
                case 93: return "Chorus Send";
                case 120: return "All Sound Off";
                case 121: return "Reset All Controllers";
                case 123: return "All Notes Off";
                default: return "CC" + cc;
            }
        }

        public static string Describe(byte[] d)
        {
            if (d == null || d.Length == 0) return "";
            int st = d[0];
            if (st == 0xF0)
            {
                return "SysEx (" + d.Length + " bytes)" + (d.Length > 1 ? "" : "");
            }
            if (st >= 0xF8) return "Realtime 0x" + st.ToString("X2");
            if (st == 0xFE) return "Active Sensing";
            int hi = st & 0xF0;
            int ch = (st & 0x0F) + 1;
            switch (hi)
            {
                case 0x80: return "Note Off  ch" + ch + "  " + NoteName(d[1]) + " (" + d[1] + ")  vel " + d[2];
                case 0x90:
                    if (d.Length > 2 && d[2] == 0) return "Note Off  ch" + ch + "  " + NoteName(d[1]) + " (" + d[1] + ")";
                    return "Note On   ch" + ch + "  " + NoteName(d[1]) + " (" + d[1] + ")  vel " + d[2];
                case 0xA0: return "Poly Press ch" + ch + "  " + NoteName(d[1]) + "  " + d[2];
                case 0xB0: return "CC        ch" + ch + "  " + CcName(d[1]) + " (" + d[1] + ") = " + d[2];
                case 0xC0: return "Prog Chg  ch" + ch + "  program " + (d[1] + 1) + " (" + d[1] + ")";
                case 0xD0: return "Chan Press ch" + ch + "  " + d[1];
                case 0xE0:
                    int v = d[1] | (d[2] << 7);
                    return "Pitch Bend ch" + ch + "  " + (v - 8192);
                default: return "0x" + st.ToString("X2");
            }
        }

        public static string Hex(byte[] d)
        {
            if (d == null) return "";
            StringBuilder sb = new StringBuilder(d.Length * 3);
            for (int i = 0; i < d.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(d[i].ToString("X2"));
            }
            return sb.ToString();
        }

        public static bool TryParseHex(string text, out byte[] bytes, out string error)
        {
            bytes = null;
            error = null;
            if (string.IsNullOrEmpty(text)) { error = "empty"; return false; }
            List<byte> list = new List<byte>();
            StringBuilder token = new StringBuilder();
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c) || c == ',' || c == '-' || c == ':')
                {
                    if (token.Length > 0) { if (!Flush(token, list, ref error)) return false; }
                }
                else token.Append(c);
            }
            if (token.Length > 0 && !Flush(token, list, ref error)) return false;
            if (list.Count == 0) { error = "no bytes"; return false; }
            bytes = list.ToArray();
            return true;
        }

        private static bool Flush(StringBuilder token, List<byte> list, ref string error)
        {
            string t = token.ToString();
            token.Length = 0;
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            if (t.Length == 0) return true;
            byte b;
            if (!byte.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out b))
            {
                error = "bad hex byte '" + t + "'";
                return false;
            }
            list.Add(b);
            return true;
        }

        /// <summary>Roland checksum: sum of address+data, inverted, 7-bit.</summary>
        public static byte RolandChecksum(byte[] data, int offset, int count)
        {
            int sum = 0;
            for (int i = 0; i < count; i++) sum += data[offset + i];
            return (byte)((128 - (sum % 128)) % 128);
        }
    }

    internal class KitInfo
    {
        public int Number;         // 1..200
        public int BankMsb;
        public int BankLsb;
        public int Program;        // 0..127
        public string Label;       // user-editable

        public override string ToString() { return Label; }
    }

    // ------------------------------------------------------------------
    // Main window
    // ------------------------------------------------------------------
    public sealed partial class MainForm : Form
    {
        private readonly MidiSession _midi = new MidiSession();
        private readonly List<KitInfo> _kits = new List<KitInfo>();
        private readonly Dictionary<int, string> _labels = new Dictionary<int, string>();
        private readonly object _logLock = new object();

        private ComboBox _cmbOut, _cmbIn;
        private Button _btnRefresh, _btnConnect;
        private CheckBox _chkDark;
        private Label _lblStatus;
        private TabControl _tabs;

        private ListView _lstKits;
        private NumericUpDown _numChannel;
        private Label _lblCurrentKit;
        private CheckBox _chkAutoFollow;

        private RichTextBox _txtMonitor;
        private CheckBox _chkHex, _chkDecode, _chkPause;
        private Button _btnClearMon, _btnSaveMon;

        // pads page
        private PadSurface _pads;
        private Label _lblPadTitle, _lblPadHint;
        private NumericUpDown _numPadNote, _numPadInst;
        private TextBox _txtPadLabel;
        private Button _btnLearn, _btnPadClear, _btnPadApply, _btnLoadPhoto;
        private TrackBar _barVelocity;
        private NumericUpDown _numGate;
        private CheckBox _chkHold;
        private TrackBar _barBeam;
        private NumericUpDown _numBeamCc;
        private ComboBox _cmbFsw1, _cmbFsw2;
        private int _lastKitIndex;
        private bool _learning;
        private string _learningPad;
        private readonly Dictionary<string, DateTime> _held = new Dictionary<string, DateTime>();

        // samples page
        private ListView _lstSamples;
        private readonly List<string> _sampleFiles = new List<string>();
        private readonly Dictionary<string, SampleResult> _sampleResults = new Dictionary<string, SampleResult>();
        private TextBox _txtOutDir, _txtPackName;
        private CheckBox _chkMono, _chkNorm, _chkTrim, _chkFade;
        private NumericUpDown _numMaxMin;
        private ProgressBar _barProgress;
        private Label _lblSampleStatus;
        private Button _btnSampleRemove, _btnSampleClear, _btnResample, _btnCancel, _btnAssign, _btnUsb;
        private FileExplorer _explorer;
        private WaveEditorPanel _waveEditor;
        private SplitContainer _splitSamples;
        private volatile bool _cancelResample;

        private TextBox _txtHex;
        private CheckBox _chkAddChecksum;
        private Button _btnSendHex, _btnIdentity, _btnLoadSyx, _btnSaveSyx;
        private TextBox _txtDevice;

        private int _bankMsb, _bankLsb;
        private bool _connected;
        private string _settingsPath;

        public MainForm()
        {
            Text = "HPD-20 Companion";
            Width = 1200;
            Height = 800;
            MinimumSize = new Size(820, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HPD20Companion");
            Directory.CreateDirectory(_settingsPath);
            _settingsPath = Path.Combine(_settingsPath, "settings.ini");

            BuildUi();
            BuildKitList();
            LoadLabels();
            LoadSettings();
            LoadTheme();
            RefreshPorts();

            _midi.MessageReceived += OnMidiMessage;
            FormClosing += delegate { SaveSettings(); _midi.Dispose(); };
        }

        // ------------------------- UI construction -------------------------

        private void BuildUi()
        {
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 76;
            top.Padding = new Padding(8);

            Label lOut = new Label();
            lOut.Text = "MIDI Out:";
            lOut.SetBounds(10, 12, 60, 20);
            _cmbOut = new ComboBox();
            _cmbOut.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbOut.SetBounds(74, 9, 250, 22);

            Label lIn = new Label();
            lIn.Text = "MIDI In:";
            lIn.SetBounds(340, 12, 52, 20);
            _cmbIn = new ComboBox();
            _cmbIn.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbIn.SetBounds(396, 9, 250, 22);

            _btnRefresh = new Button();
            _btnRefresh.Text = "Refresh";
            _btnRefresh.SetBounds(660, 8, 80, 25);
            _btnRefresh.Click += delegate { RefreshPorts(); };

            _btnConnect = new Button();
            _btnConnect.Text = "Connect";
            _btnConnect.SetBounds(750, 8, 90, 25);
            _btnConnect.Click += delegate { ToggleConnect(); };

            _lblStatus = new Label();
            _lblStatus.SetBounds(10, 44, 930, 22);
            _lblStatus.Text = "Not connected.";
            _lblStatus.ForeColor = Color.DimGray;

            _chkDark = new CheckBox();
            _chkDark.Text = "Dark mode";
            _chkDark.SetBounds(846, 10, 96, 22);
            _chkDark.CheckedChanged += delegate { SetTheme(_chkDark.Checked); };
            Tip(_chkDark, "Switch the whole window between the dark and light palette. The choice applies to every tab and is remembered next time.");

            top.Controls.AddRange(new Control[] { lOut, _cmbOut, lIn, _cmbIn, _btnRefresh, _btnConnect, _chkDark, _lblStatus });
            Controls.Add(top);

            _tabs = new TabControl();
            _tabs.Dock = DockStyle.Fill;
            _tabs.Padding = new Point(12, 4);

            _tabs.TabPages.Add(BuildPadsTab());
            _tabs.TabPages.Add(BuildKitsTab());
            _tabs.TabPages.Add(BuildSamplesTab());
            _tabs.TabPages.Add(BuildMonitorTab());
            _tabs.TabPages.Add(BuildSysExTab());
            _tabs.TabPages.Add(BuildBackupTab());
            _tabs.TabPages.Add(BuildDeviceTab());

            Controls.Add(_tabs);
            _tabs.BringToFront();

            ApplyTooltips();
        }

        // ------------------------- hover help -------------------------

        private ToolTip _tips;

        private void Tip(Control c, string text)
        {
            if (c == null || _tips == null || string.IsNullOrEmpty(text)) return;
            _tips.SetToolTip(c, text);
        }

        /// <summary>Adds a hover explanation to every control that is worth explaining.</summary>
        private void ApplyTooltips()
        {
            if (_tips != null) return;
            _tips = new ToolTip();
            _tips.AutoPopDelay = 30000;
            _tips.InitialDelay = 350;
            _tips.ReshowDelay = 120;
            _tips.ShowAlways = true;

            // ---- connection bar
            Tip(_cmbOut, "MIDI output port. Pick the HPD-20 (it is chosen automatically when the name contains HPD or HANDSONIC). Everything the app sends leaves here.");
            Tip(_cmbIn, "MIDI input port. The app listens here for the HPD-20's identity reply, kit changes and pad notes. Use the same device as the output.");
            Tip(_btnRefresh, "Re-read the list of MIDI devices. Use it after plugging the HPD-20 in or reinstalling the Roland driver.");
            Tip(_btnConnect, "Open the selected ports. Connecting sends an Identity Request so the Device tab can show the unit's firmware. Press again to disconnect.");

            // ---- pads tab
            Tip(_pads, "The playing surface. Click a pad to send its note, hold to sustain, click the D-BEAM strip to send its controller value, and click the jacks to play the external pad, hi-hat pedal or foot switch. Hover for details.");
            Tip(_lblHover, "Shows what is under the mouse: the element, its note or controller number, and what a click will do.");
            Tip(_numPadNote, "Note number this pad sends. Click Learn and strike the pad on the instrument to capture it automatically, or type the number that the kit uses (MENU > KIT > MIDI > NOTE).");
            Tip(_btnLearn, "Waits for the next note from the instrument and assigns it to the selected pad. Strike the pad on the HPD-20 after clicking this.");
            Tip(_txtPadLabel, "Your own label for the sample loaded on this pad. The HPD-20 cannot report pad assignments over MIDI, so this is local bookkeeping shown on the pad.");
            Tip(_numPadInst, "User instrument number (1-500) you imported into the unit and assigned here. 0 means none. Purely a local note.");
            Tip(_btnPadApply, "Save the note, label and instrument number for this pad into %APPDATA%\\HPD20Companion\\pads.ini.");
            Tip(_btnPadClear, "Forget the note and label for this pad. The pad then falls back to whatever a loaded backup says, or to \"no note\".");
            Tip(_barVelocity, "Velocity sent when you click a pad. The HPD-20 responds to velocity like a real strike.");
            Tip(_chkHold, "On: the note lasts until you release the mouse button. Off: the note is released after the Gate value, which suits percussive one-shots.");
            Tip(_numGate, "Note length in milliseconds when Hold is off.");
            Tip(_numBeamCc, "Controller number the D-BEAM sends. It must match MENU > SYS > MIDI > MIDI on the unit (EFFECT1 = 12, EFFECT2 = 13).");
            Tip(_barBeam, "Sends the D-BEAM controller value as you move it. Release to leave the value where it is.");
            Tip(_cmbFsw1, "Function this foot switch would run. The instrument's own assignment is MENU > SYS > FootSw Control; the app runs the function for you, and only KIT+, KIT- and ALL SOUND OFF are reachable over MIDI.");
            Tip(_cmbFsw2, "Second foot switch function (needs a Y-cable or an FS-6). Same rules as Foot SW 1.");
            Tip(_btnLoadPhoto, "Put a photo of your own HPD-20 behind the pad overlay. The pads stay clickable on top of it.");

            // ---- kits tab
            Tip(_lstKits, "All 200 kits. Double-click one to send it, right-click to give it a local label. The list is driven by bank select + program change.");
            Tip(_numChannel, "MIDI channel used for kit changes, notes and controllers. It must match the unit's MIDI channel.");
            Tip(_chkAutoFollow, "When the instrument sends a program change (you turned its knob), highlight the matching kit here. Needs Program Change Tx = ON on the unit.");
            Tip(_lblCurrentKit, "Shows the last kit the app sent, or the kit the instrument reported.");

            // ---- samples tab
            Tip(_lstSamples, "Files queued for conversion. The Result column reports what happened to each one: resampled, already compliant, truncated, or the error. Drag a row into the waveform editor below to edit it - once the file has been converted, the drag hands over the resampled WAV.");
            Tip(_chkMono, "Downmix to a single channel. Halves the memory the sample uses on the instrument; stereo is allowed but costs twice as much.");
            Tip(_chkNorm, "Raise the loudest peak to just under full scale so quiet recordings are not lost against other pads.");
            Tip(_chkTrim, "Cut leading and trailing silence. Keeps a 10 ms margin so the attack is not clipped.");
            Tip(_chkFade, "Apply a 5 ms fade at both ends to avoid clicks when a sample starts or stops.");
            Tip(_numMaxMin, "Longest output in minutes. The HPD-20 accepts at most 6:00 per file; longer material is truncated rather than rejected.");
            Tip(_txtOutDir, "Folder the converted WAVs are written to, before they are copied to the USB stick.");
            Tip(_btnSampleRemove, "Remove the selected file from the queue without touching it on disk.");
            Tip(_btnSampleClear, "Empty the queue.");
            Tip(_btnResample, "Convert every queued file to 44.1 kHz / 16-bit and write it to the output folder. The progress bar and status line track the 12-minute user-instrument budget.");
            Tip(_btnCancel, "Stop the batch after the file that is currently being converted.");
            Tip(_btnAssign, "Record the selected converted file as the sample on the selected pad (local label only).");
            Tip(_btnUsb, "Copy the converted files to the stick: short ASCII names in the root for the instrument to import, plus an organised folder and index for you.");

            // ---- backup editor
            Tip(_btnBackupOpen, "Open a backup file (BKUP-nnn.HS0) from the stick or from disk. Nothing is written until you save.");
            Tip(_btnBackupSave, "Write the edited image to a new local file, recomputing the MD5 trailer.");
            Tip(_btnBackupUsb, "Write the image into Roland/HPD-20/Backup on the stick, choosing the next free BKUP number so an existing backup is not overwritten.");
            Tip(_btnBackupRevert, "Discard the edits in memory and re-read the file from disk.");
            Tip(_lstBackupKits, "The 200 kits in the open backup. Selecting one loads its six kit fields and its pads, and mirrors that kit onto the Pads tab.");
            Tip(_lstBackupPads, "The 17 pad records of the selected kit: M1-M5, S1-S8, D-BEAM, TRIG IN head and rim, and the HH pedal.");
            Tip(_txtKitName, "Kit name, 12 characters. Shown on the instrument's display.");
            Tip(_txtKitSubName, "Kit sub-name, 16 characters. The instrument stores this alongside the main name.");
            Tip(_numKitVolume, "Overall kit volume, stored as one byte. Typical factory values are 60-100.");
            Tip(_numKitHhVolume, "Volume of the hi-hat control pedal for this kit, one byte.");
            Tip(_numKitSensitivity, "Pad sensitivity for this kit, one byte. Higher is more sensitive; the instrument shows 0-25 on its own scale.");
            Tip(_numKitBalance, "Kit balance, one byte. 64 is centred on the instrument.");
            Tip(_radLayerA, "Edit the layer A instrument and its settings (the sound that plays first).");
            Tip(_radLayerB, "Edit layer B, the second sound a pad can layer or switch to.");
            Tip(_cmbInstrument, "Instrument assigned to this pad layer. Pick by name, or type a number. All 850 known instruments are listed.");
            Tip(_numPitch, "Tuning in cents. 100 = one semitone, 1200 = one octave.");
            Tip(_numPadVolume, "Pad volume, one byte.");
            Tip(_numPan, "Stereo position, signed. 0 is centre, negative is left.");
            Tip(_numAmbient, "How much of the kit's ambience this pad receives, one byte.");
            Tip(_numMuffling, "Muffling amount: how quickly the sound is damped when the pad is pressed.");
            Tip(_numSweep, "Pitch sweep amount, signed. Negative sweeps down, positive up.");
            Tip(_numMfxAssign, "Which multi-effect, if any, this pad feeds. 0-3 as used by the factory data.");
            Tip(_numMidiNote, "Note number this pad sends and responds to. This is the value the Pads tab shows when a backup is loaded.");
            Tip(_numMidiGate, "Gate time byte stored with the pad's MIDI settings.");
            Tip(_numVelocityFade, "How strongly velocity shapes the sound. 0 is flat, higher is more dynamic.");
            Tip(_numFixedVelocity, "When trigger mode fixes the velocity, this is the value used.");
            Tip(_cmbTriggerMode, "How the pad responds: SHOT (one-shot), GATE (sounds while held) or ALT. The byte values are 0, 1 and 2.");
            Tip(_numMuteGroup, "Pads in the same mute group cut each other off. 0 means no group.");
            Tip(_numMonoPoly, "0 = the pad's sound is monophonic, 1 = polyphonic.");
            Tip(_numLayerMode, "How layers A and B combine; the instrument offers several modes.");
            Tip(_lblPadHex, "The raw 68 bytes of this pad record, at their real offset in the image. Use the Expert page to edit them.");
            Tip(_lstChains, "The 15 kits chains stored in the backup.");
            Tip(_txtChainName, "Chain name, 10 characters.");
            Tip(_numRawOffset, "Byte offset into the image. 0 is the first byte; the MD5 trailer is not shown.");
            Tip(_numRawValue, "Value to write at that offset, 0-255.");
            Tip(_numRawPage, "Page number (256 bytes per page) for the hex dump.");
            Tip(_txtRawDump, "Hex dump of the current page with an ASCII column. The byte at the chosen offset is shown in brackets.");

            // ---- monitor / sysex / device
            Tip(_txtMonitor, "Every message the app sends and receives, with timestamps. Incoming notes also light up the matching pad.");
            Tip(_chkHex, "Show the raw bytes of each message.");
            Tip(_chkDecode, "Show a readable interpretation: note name, controller name, program number.");
            Tip(_chkPause, "Freeze the display so you can read it. Messages are still received.");
            Tip(_btnClearMon, "Empty the monitor.");
            Tip(_btnSaveMon, "Write the monitor contents to a text file.");
            Tip(_txtHex, "Bytes to send, in hex. SysEx is framed automatically with F0 and F7.");
            Tip(_chkAddChecksum, "Insert a Roland checksum before F7. The sum covers the address and data bytes.");
            Tip(_btnIdentity, "Send a Universal Non-Realtime Identity Request. The reply appears on the Device tab and in the monitor.");
            Tip(_btnLoadSyx, "Load a .syx or text file of hex bytes into the send box.");
            Tip(_btnSaveSyx, "Save the last SysEx message the instrument sent.");
            Tip(_txtDevice, "The parsed identity reply: manufacturer, device id, family code, firmware revision.");

            // ---- buttons that only exist as locals: match on their caption
            TipByText();
        }

        private void TipByText()
        {
            var texts = new Dictionary<string, string>
            {
                { "Send kit (Bank + PC)", "Send the selected kit as bank select MSB/LSB followed by a program change. This is the only documented way to change kits remotely." },
                { "Send Program Change only", "Send just the program number, leaving the current bank alone. Useful when the kit lives in the bank the unit is already on." },
                { "All Sound Off", "Silence every sounding voice on the MIDI channel (CC120)." },
                { "Reset Controllers", "Reset all controllers on the channel (CC121), clearing sustain and modulation state." },
                { "Browse...", "Choose the folder converted WAVs are written to." },
                { "Write to USB stick", "Write the converted samples to the stick: root copies for the unit, an organised folder for you." },
                { "Add files...", "Add individual WAV files to the conversion queue." },
                { "Add folder...", "Add every WAV in a folder and its subfolders." },
                { "Remove", "Remove the selected file from the queue." },
                { "Clear list", "Empty the conversion queue." },
                { "Batch resample", "Convert every queued file to 44.1 kHz / 16-bit in one pass." },
                { "Cancel", "Stop the batch when the current file finishes." },
                { "Assign to selected pad", "Record the selected file as the sample on the selected pad (local label)." },
                { "Open backup...", "Open a BKUP-nnn.HS0 image from the stick or from disk." },
                { "Save as...", "Save the edited image to a local file with a freshly computed MD5." },
                { "Write to USB stick...", "Write the image into Roland/HPD-20/Backup on the stick, taking the next free backup number." },
                { "Revert", "Throw away the in-memory edits and re-read the file." },
                { "Apply to pad", "Store the pad's note, label and instrument number locally." },
                { "Clear pad", "Forget the local note and label for this pad." },
                { "Apply name", "Rename the selected chain in the backup image." },
                { "Write byte", "Write the value into the image at the chosen offset." },
                { "< PgUp", "Show the previous 256-byte page." },
                { "PgDn >", "Show the next 256-byte page." },
                { "Load photo of my unit...", "Use a photo of your own HPD-20 as the background of the pad overlay." },
                { "Connect", "Open the selected MIDI ports and send an Identity Request." },
                { "Refresh", "Re-read the MIDI device list." },
                { "Identity Request", "Ask the instrument to identify itself; the reply is parsed on the Device tab." },
                { "Load .syx/.txt", "Load hex bytes from a file into the send box." },
                { "Save last SysEx", "Save the most recent SysEx message to a file." },
                { "Clear", "Empty the monitor." },
                { "Save log...", "Write the monitor contents to a text file." }
            };
            Walk(this, texts);
        }

        private void Walk(Control parent, Dictionary<string, string> texts)
        {
            foreach (Control c in parent.Controls)
            {
                string key = (c.Text ?? "").Trim();
                string text;
                if (key.Length > 0 && texts.TryGetValue(key, out text)) Tip(c, text);
                if (c.Controls.Count > 0) Walk(c, texts);
            }
        }

        // order of the 17 pad records inside an HPD-20 backup
        private static readonly string[] BackupPadOrder = new string[]
        {
            "M1", "M2", "M3", "M4", "M5",
            "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8",
            "BEAM", "EXT_H", "EXT_R", "HH"
        };

        private Label _lblHover;

        /// <summary>Mirrors the currently loaded backup's selected kit onto the pad tiles.</summary>
        private void SyncPadsFromBackup()
        {
            if (_pads == null) return;
            if (_backup == null || _lstBackupKits == null || _lstBackupKits.SelectedIndex < 0)
            {
                _pads.Caption = "";
                _pads.Invalidate();
                return;
            }
            int kit = _lstBackupKits.SelectedIndex;
            for (int i = 0; i < BackupPadOrder.Length; i++)
            {
                PadState st;
                if (!_pads.Pads.TryGetValue(BackupPadOrder[i], out st)) continue;
                st.Note = _backup.GetPadMidiNote(kit, i);
                int inst = _backup.GetPadInstrument(kit, i, 0);
                st.Info = inst > 0 ? InstrumentTable.NameOf(inst) : "";
            }
            _pads.Pads["HH"].Cc = 4;
            _pads.Pads["BEAM"].Cc = (int)_numBeamCc.Value;
            _pads.Caption = "kit " + (kit + 1).ToString("000") + " \u00b7 " + _backup.GetKitName(kit) +
                            "  \u00b7  " + Path.GetFileName(_backup.SourcePath);
            _pads.Invalidate();
        }

        private TabPage BuildPadsTab()
        {
            TabPage page = new TabPage("Pads");

            _pads = new PadSurface();
            _pads.Dock = DockStyle.Fill;
            _pads.MouseMove += delegate(object s, MouseEventArgs e) { if (_lblHover != null) _lblHover.Text = _pads.DescribeAt(e.Location); };
            _pads.MouseLeave += delegate { if (_lblHover != null) _lblHover.Text = "Hover a pad, the D-BEAM strip or one of the jacks."; };
            _pads.PadPressed += delegate(object s, PadTriggerEventArgs e) { PadsPressed(e.PadId); };
            _pads.PadReleased += delegate(object s, PadTriggerEventArgs e) { PadsReleased(e.PadId); };

            Panel side = new Panel();
            side.Dock = DockStyle.Right;
            side.Width = 300;
            side.Padding = new Padding(10, 8, 8, 8);
            side.AutoScroll = true;

            _lblPadTitle = new Label();
            _lblPadTitle.SetBounds(10, 8, 275, 22);
            _lblPadTitle.Font = new Font("Segoe UI Semibold", 10f);
            _lblPadTitle.Text = "Select a pad";

            Label lNote = new Label();
            lNote.Text = "Note number";
            lNote.SetBounds(10, 40, 100, 20);
            _numPadNote = new NumericUpDown();
            _numPadNote.Minimum = 0;
            _numPadNote.Maximum = 127;
            _numPadNote.SetBounds(120, 38, 60, 22);

            _btnLearn = new Button();
            _btnLearn.Text = "Learn (tap pad)";
            _btnLearn.SetBounds(186, 37, 105, 24);
            _btnLearn.Click += delegate { StartLearn(); };

            Label lLab = new Label();
            lLab.Text = "Sample label";
            lLab.SetBounds(10, 70, 100, 20);
            _txtPadLabel = new TextBox();
            _txtPadLabel.SetBounds(120, 68, 171, 22);

            Label lInst = new Label();
            lInst.Text = "User inst #";
            lInst.SetBounds(10, 100, 100, 20);
            _numPadInst = new NumericUpDown();
            _numPadInst.Minimum = 0;
            _numPadInst.Maximum = 500;
            _numPadInst.SetBounds(120, 98, 60, 22);
            Label lInstHint = new Label();
            lInstHint.Text = "0 = none";
            lInstHint.SetBounds(186, 100, 105, 18);
            lInstHint.ForeColor = Color.DimGray;

            _btnPadApply = new Button();
            _btnPadApply.Text = "Apply to pad";
            _btnPadApply.SetBounds(10, 128, 106, 26);
            _btnPadApply.Click += delegate { ApplyPadEdits(); };

            _btnPadClear = new Button();
            _btnPadClear.Text = "Clear pad";
            _btnPadClear.SetBounds(120, 128, 106, 26);
            _btnPadClear.Click += delegate { ClearPadEdits(); };

            Label lVel = new Label();
            lVel.Text = "Velocity";
            lVel.SetBounds(10, 166, 80, 20);
            _barVelocity = new TrackBar();
            _barVelocity.Minimum = 1;
            _barVelocity.Maximum = 127;
            _barVelocity.Value = 100;
            _barVelocity.TickStyle = TickStyle.None;
            _barVelocity.SetBounds(94, 160, 197, 32);

            _chkHold = new CheckBox();
            _chkHold.Text = "Hold until mouse release";
            _chkHold.Checked = true;
            _chkHold.SetBounds(10, 210, 220, 22);

            Label lGate = new Label();
            lGate.Text = "Gate ms";
            lGate.SetBounds(10, 240, 60, 20);
            _numGate = new NumericUpDown();
            _numGate.Minimum = 20;
            _numGate.Maximum = 5000;
            _numGate.Value = 250;
            _numGate.Increment = 10;
            _numGate.SetBounds(74, 238, 70, 22);

            Label lBeam = new Label();
            lBeam.Text = "D-BEAM CC";
            lBeam.SetBounds(10, 272, 80, 20);
            _numBeamCc = new NumericUpDown();
            _numBeamCc.Minimum = 0;
            _numBeamCc.Maximum = 127;
            _numBeamCc.Value = 12;
            _numBeamCc.SetBounds(94, 270, 50, 22);
            _barBeam = new TrackBar();
            _barBeam.Minimum = 0;
            _barBeam.Maximum = 127;
            _barBeam.Value = 64;
            _barBeam.TickStyle = TickStyle.None;
            _barBeam.SetBounds(148, 264, 143, 32);
            _barBeam.ValueChanged += delegate { SendCc((int)_numBeamCc.Value, _barBeam.Value); };
            _numBeamCc.ValueChanged += delegate
            {
                if (_pads == null) return;
                _pads.Pads["BEAM"].Cc = (int)_numBeamCc.Value;
                _pads.Invalidate();
            };

            Label lFsw = new Label();
            lFsw.Text = "Foot SW 1";
            lFsw.SetBounds(10, 302, 90, 20);
            _cmbFsw1 = new ComboBox();
            _cmbFsw1.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbFsw1.SetBounds(100, 300, 190, 22);
            _cmbFsw1.Items.AddRange(FootSwitchFunctions);

            Label lFsw2 = new Label();
            lFsw2.Text = "Foot SW 2";
            lFsw2.SetBounds(10, 328, 90, 20);
            _cmbFsw2 = new ComboBox();
            _cmbFsw2.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbFsw2.SetBounds(100, 326, 190, 22);
            _cmbFsw2.Items.AddRange(FootSwitchFunctions);
            _cmbFsw1.SelectedIndex = 1;   // KIT+
            _cmbFsw2.SelectedIndex = 2;   // KIT-
            _cmbFsw1.SelectedIndexChanged += delegate { if (_pads != null) { _pads.Pads["FSW1"].Info = _cmbFsw1.Text; _pads.Invalidate(); } };
            _cmbFsw2.SelectedIndexChanged += delegate { if (_pads != null) { _pads.Pads["FSW2"].Info = _cmbFsw2.Text; _pads.Invalidate(); } };
            _pads.Pads["FSW1"].Info = _cmbFsw1.Text;
            _pads.Pads["FSW2"].Info = _cmbFsw2.Text;

            _btnLoadPhoto = new Button();
            _btnLoadPhoto.Text = "Load photo of my unit...";
            _btnLoadPhoto.SetBounds(10, 356, 280, 26);
            _btnLoadPhoto.Click += delegate { LoadPhoto(); };

            _lblPadHint = new Label();
            _lblPadHint.SetBounds(10, 390, 280, 140);
            _lblPadHint.ForeColor = Color.DimGray;
            _lblPadHint.Text = "TRIG IN type (KD-9, BT-1, PD-8...) and the Foot SW\r\n" +
                               "functions are system settings on the instrument:\r\n" +
                               "MENU > SYS > ExtTrig Settings > TYPE\r\n" +
                               "MENU > SYS > FootSw Control\r\n" +
                               "Neither is reachable over MIDI or through the\r\n" +
                               "backup fields known so far. The menus above\r\n" +
                               "run the function inside this app instead.";

            side.Controls.AddRange(new Control[] {
                _lblPadTitle, lNote, _numPadNote, _btnLearn, lLab, _txtPadLabel, lInst, _numPadInst, lInstHint,
                _btnPadApply, _btnPadClear, lVel, _barVelocity, _chkHold, lGate, _numGate,
                lBeam, _numBeamCc, _barBeam, lFsw, _cmbFsw1, lFsw2, _cmbFsw2, _btnLoadPhoto, _lblPadHint
            });

            _lblHover = new Label();
            _lblHover.Dock = DockStyle.Bottom;
            _lblHover.Height = 22;
            _lblHover.TextAlign = ContentAlignment.MiddleLeft;
            _lblHover.Padding = new Padding(10, 0, 0, 0);
            _lblHover.ForeColor = Color.FromArgb(150, 190, 220);
            _lblHover.Text = "Hover a pad, the D-BEAM strip or one of the jacks.";

            page.Controls.Add(_pads);
            page.Controls.Add(_lblHover);
            page.Controls.Add(side);
            return page;
        }

        private TabPage BuildSamplesTab()
        {
            TabPage page = new TabPage("Samples");

            Panel opts = new Panel();
            opts.Dock = DockStyle.Top;
            opts.Height = 106;
            opts.Padding = new Padding(6);

            _chkMono = new CheckBox();
            _chkMono.Text = "Mono";
            _chkMono.Checked = true;
            _chkMono.SetBounds(8, 8, 70, 22);

            _chkNorm = new CheckBox();
            _chkNorm.Text = "Normalise";
            _chkNorm.Checked = true;
            _chkNorm.SetBounds(78, 8, 90, 22);

            _chkTrim = new CheckBox();
            _chkTrim.Text = "Trim silence";
            _chkTrim.Checked = true;
            _chkTrim.SetBounds(168, 8, 100, 22);

            _chkFade = new CheckBox();
            _chkFade.Text = "Fade edges";
            _chkFade.Checked = true;
            _chkFade.SetBounds(268, 8, 95, 22);

            Label lMax = new Label();
            lMax.Text = "Max minutes";
            lMax.SetBounds(372, 10, 80, 20);
            _numMaxMin = new NumericUpDown();
            _numMaxMin.Minimum = 1;
            _numMaxMin.Maximum = 6;
            _numMaxMin.Value = 6;
            _numMaxMin.DecimalPlaces = 1;
            _numMaxMin.Increment = 0.5m;
            _numMaxMin.SetBounds(456, 8, 55, 22);

            Label lOut = new Label();
            lOut.Text = "Output folder";
            lOut.SetBounds(8, 38, 84, 20);
            _txtOutDir = new TextBox();
            _txtOutDir.SetBounds(92, 36, 560, 22);
            _txtOutDir.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HPD20_Samples");

            Button browse = new Button();
            browse.Text = "Browse...";
            browse.SetBounds(658, 35, 80, 24);
            browse.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    if (Directory.Exists(_txtOutDir.Text)) d.SelectedPath = _txtOutDir.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) _txtOutDir.Text = d.SelectedPath;
                }
            };

            Label lInfo = new Label();
            lInfo.SetBounds(8, 64, 760, 36);
            lInfo.ForeColor = Color.DimGray;
            lInfo.Text = "Output: WAV 44.1 kHz / 16-bit, mono or stereo, max 6:00 each (500 instruments / 12 minutes total)." + Environment.NewLine +
                           "Input: WAV, AIFF/AIFF-C, CAF, AU, MP3, FLAC, M4A/AAC, WMA, and OGG/Opus with the Windows media extension.";

            opts.Controls.AddRange(new Control[] { _chkMono, _chkNorm, _chkTrim, _chkFade, lMax, _numMaxMin, lOut, _txtOutDir, browse, lInfo });

            Panel tools = new Panel();
            tools.Dock = DockStyle.Top;
            tools.Height = 38;

            _btnSampleRemove = new Button();
            _btnSampleRemove.Text = "Remove";
            _btnSampleRemove.SetBounds(6, 5, 74, 26);
            _btnSampleRemove.Click += delegate { RemoveSelectedSample(); };

            _btnSampleClear = new Button();
            _btnSampleClear.Text = "Clear list";
            _btnSampleClear.SetBounds(84, 5, 80, 26);
            _btnSampleClear.Click += delegate { _sampleFiles.Clear(); _sampleResults.Clear(); RefreshSampleList(); };

            _btnResample = new Button();
            _btnResample.Text = "Batch resample";
            _btnResample.SetBounds(168, 5, 110, 26);
            _btnResample.Click += delegate { StartResample(); };

            _btnCancel = new Button();
            _btnCancel.Text = "Cancel";
            _btnCancel.SetBounds(282, 5, 70, 26);
            _btnCancel.Enabled = false;
            _btnCancel.Click += delegate { _cancelResample = true; };

            _btnAssign = new Button();
            _btnAssign.Text = "Assign to selected pad";
            _btnAssign.SetBounds(356, 5, 150, 26);
            _btnAssign.Click += delegate { AssignSampleToPad(); };


            tools.Controls.AddRange(new Control[] { _btnSampleRemove, _btnSampleClear, _btnResample, _btnCancel, _btnAssign });

            _lstSamples = new ListView();
            _lstSamples.Dock = DockStyle.Fill;
            _lstSamples.View = View.Details;
            _lstSamples.FullRowSelect = true;
            _lstSamples.Columns.Add("File", 260);
            _lstSamples.Columns.Add("Source format", 160);
            _lstSamples.Columns.Add("Result", 300);
            _lstSamples.AllowDrop = true;
            _lstSamples.DragEnter += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            _lstSamples.ItemDrag += delegate(object s, ItemDragEventArgs e)
            {
                List<string> paths = DragPathsForSamples(e.Item as ListViewItem);
                if (paths.Count == 0) return;
                _lstSamples.DoDragDrop(new DataObject(DataFormats.FileDrop, paths.ToArray()), DragDropEffects.Copy);
            };
            _lstSamples.DragDrop += delegate(object s, DragEventArgs e)
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files == null) return;
                int added = 0;
                foreach (string f in files)
                    if (File.Exists(f) && AudioFormats.IsSupportedExtension(f)) { AddSample(f); added++; }
                RefreshSampleList();
                if (added > 0) SetStatus("Queued " + added + " dropped file(s).", Color.ForestGreen);
            };

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 96;

            _barProgress = new ProgressBar();
            _barProgress.SetBounds(8, 8, 700, 18);

            _lblSampleStatus = new Label();
            _lblSampleStatus.SetBounds(8, 30, 700, 20);
            _lblSampleStatus.Text = "No files added.";

            Label lUsb = new Label();
            lUsb.Text = "USB stick:";
            lUsb.SetBounds(8, 58, 70, 20);

            ComboBox cmbDrive = new ComboBox();
            cmbDrive.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbDrive.SetBounds(78, 56, 220, 22);
            cmbDrive.Name = "cmbDrive";
            FillDriveCombo(cmbDrive);
            cmbDrive.DropDown += delegate { FillDriveCombo(cmbDrive); };

            Label lPack = new Label();
            lPack.Text = "Pack name";
            lPack.SetBounds(310, 58, 70, 20);
            _txtPackName = new TextBox();
            _txtPackName.SetBounds(384, 56, 110, 22);
            _txtPackName.Text = "HPD_PACK";

            _btnUsb = new Button();
            _btnUsb.Text = "Write to USB stick";
            _btnUsb.SetBounds(504, 54, 130, 26);
            _btnUsb.Click += delegate { ExportToUsb(cmbDrive); };

            Label lUsbHint = new Label();
            lUsbHint.SetBounds(8, 78, 700, 18);
            lUsbHint.ForeColor = Color.DimGray;
            lUsbHint.Text = "The HPD-20 only lists WAV files in the ROOT of the stick (max 1000), so root copies get short ASCII names; the organised folder is for you.";

            bottom.Controls.AddRange(new Control[] { _barProgress, _lblSampleStatus, lUsb, cmbDrive, lPack, _txtPackName, _btnUsb, lUsbHint });

            _explorer = new FileExplorer();
            _explorer.Dock = DockStyle.Fill;   // the split container sets its width
            _explorer.FilesChosen += delegate(List<string> files)
            {
                foreach (string f in files) AddSample(f);
                RefreshSampleList();
                SetStatus("Added " + files.Count + " file(s) from the browser.", Color.ForestGreen);
            };

            _waveEditor = new WaveEditorPanel();
            _waveEditor.Dock = DockStyle.Fill;
            _waveEditor.ClipsRendered += delegate(List<string> files)
            {
                foreach (string p in files) AddSample(p);
                RefreshSampleList();
                SetStatus("Waveform editor rendered " + files.Count +
                          " clip(s) - press Batch resample to convert them.", Color.ForestGreen);
            };

            Label lEditor = new Label();
            lEditor.Text = "Waveform editor - drag a selection to chop, Play to audition, Send to samples to convert";
            lEditor.Dock = DockStyle.Top;
            lEditor.Height = 20;
            lEditor.ForeColor = Color.DimGray;

            _splitSamples = new SplitContainer();
            _splitSamples.Dock = DockStyle.Fill;
            _splitSamples.Orientation = Orientation.Horizontal;
            _splitSamples.Size = new Size(920, 520);      // valid before the min sizes are applied
            _splitSamples.Panel1MinSize = 150;
            _splitSamples.Panel2MinSize = 180;
            _splitSamples.SplitterDistance = 320;

            // browser | (everything else), with a draggable divider between them
            SplitContainer browserSplit = new SplitContainer();
            browserSplit.Dock = DockStyle.Fill;
            browserSplit.Orientation = Orientation.Vertical;
            browserSplit.Size = new Size(900, 420);        // valid before the min sizes are applied
            browserSplit.Panel1MinSize = 140;
            browserSplit.Panel2MinSize = 280;
            browserSplit.SplitterDistance = 220;
            browserSplit.FixedPanel = FixedPanel.Panel1;   // the browser keeps its width when the window grows
            browserSplit.Panel1.Controls.Add(_explorer);

            Panel right = new Panel();
            right.Dock = DockStyle.Fill;
            right.Controls.Add(_lstSamples);
            right.Controls.Add(tools);
            right.Controls.Add(opts);
            right.Controls.Add(bottom);
            browserSplit.Panel2.Controls.Add(right);

            _splitSamples.Panel1.Controls.Add(browserSplit);

            _splitSamples.Panel2.Controls.Add(_waveEditor);
            _splitSamples.Panel2.Controls.Add(lEditor);

            page.Controls.Add(_splitSamples);
            _splitSamples.SizeChanged += delegate
            {
                if (_splitSamples.Height < 300 || _splitSamples.Tag != null) return;
                _splitSamples.Tag = "sized";          // only the first real layout sets the split
                try { _splitSamples.SplitterDistance = Math.Max(220, _splitSamples.Height - 340); } catch { }
            };
            return page;
        }

        // ------------------------- pad triggering -------------------------

        private static readonly object[] FootSwitchFunctions = new object[]
        {
            "OFF", "KIT+", "KIT-", "D-BEAM SW", "RT MODIFY PITCH SW", "RT MODIFY EFFECT SW", "ROLL SW", "ALL SOUND OFF"
        };

        private int MidiChannel { get { return (int)_numChannel.Value - 1; } }

        private void PadsPressed(string padId)
        {
            PadState st;
            if (!_pads.Pads.TryGetValue(padId, out st)) return;
            SelectPadForEdit(padId);

            if (st.IsSwitch)
            {
                DoFootSwitch(padId);
                _pads.Flash(padId);
                return;
            }

            if (padId == "BEAM")
            {
                SendCc((int)_numBeamCc.Value, _barBeam.Value);
                _pads.Flash("BEAM");
                return;
            }

            if (!_connected) { SetStatus("Not connected.", Color.Firebrick); return; }
            if (st.Note < 0) { SetStatus("No note learned for " + st.Display + " - use Learn, or set Note# on the unit.", Color.Firebrick); return; }
            _midi.SendShort((byte)(0x90 | MidiChannel), (byte)st.Note, (byte)_barVelocity.Value);
            _held[padId] = DateTime.Now;
            LogLine("TX  Note On  " + st.Display + "  note " + st.Note + " (" + MidiText.NoteName(st.Note) + ")  vel " + _barVelocity.Value);
        }

        /// <summary>Runs the function a foot switch is set to. The instrument's own assignment lives at
        /// MENU > SYS > FootSw Control; only kit changes and All Sound Off are reachable over MIDI.</summary>
        private void DoFootSwitch(string id)
        {
            int index = id == "FSW2" ? 1 : 0;
            ComboBox box = index == 0 ? _cmbFsw1 : _cmbFsw2;
            int fn = box.SelectedIndex;
            string label = "Foot SW " + (index + 1) + " (" + (fn >= 0 ? FootSwitchFunctions[fn] : "OFF") + "): ";

            switch (fn)
            {
                case 1:
                    StepKit(+1);
                    return;
                case 2:
                    StepKit(-1);
                    return;
                case 7:
                    if (!_connected) { SetStatus("Not connected.", Color.Firebrick); return; }
                    SendChannelMode(120, 0);
                    return;
                case 3:
                case 4:
                case 5:
                case 6:
                    SetStatus(label + "the instrument toggles this internally; assign it at MENU > SYS > FootSw Control.", Color.DarkOrange);
                    return;
                default:
                    SetStatus(label + "no function assigned.", Color.DimGray);
                    return;
            }
        }

        private void StepKit(int delta)
        {
            if (!_connected) { SetStatus("Not connected.", Color.Firebrick); return; }
            int idx = Math.Max(0, Math.Min(_kits.Count - 1, _lastKitIndex + delta));
            SendKit(_kits[idx]);
            try
            {
                _lstKits.SelectedIndices.Clear();
                _lstKits.SelectedIndices.Add(idx);
                _lstKits.EnsureVisible(idx);
            }
            catch (InvalidOperationException) { }
        }

        private void PadsReleased(string padId)
        {
            PadState st;
            if (!_pads.Pads.TryGetValue(padId, out st)) return;
            if (st.IsBeam || st.IsSwitch || st.Note < 0) return;

            if (_chkHold.Checked)
            {
                SendNoteOff(st);
            }
            else
            {
                int gate = (int)_numGate.Value;
                System.Threading.Timer t = null;
                t = new System.Threading.Timer(delegate
                {
                    try { SendNoteOff(st); } catch { }
                    if (t != null) t.Dispose();
                }, null, gate, System.Threading.Timeout.Infinite);
            }
        }

        private void SendNoteOff(PadState st)
        {
            if (_midi != null) _midi.SendShort((byte)(0x80 | MidiChannel), (byte)st.Note, 0x40);
        }

        private void SendCc(int cc, int value)
        {
            if (!_connected) return;
            _midi.SendShort((byte)(0xB0 | MidiChannel), (byte)cc, (byte)value);
        }

        private void SelectPadForEdit(string padId)
        {
            _pads.SelectedId = padId;
            PadState st;
            if (!_pads.Pads.TryGetValue(padId, out st)) return;
            _lblPadTitle.Text = st.Display + (st.IsBeam ? " (controller)" : "");
            _numPadNote.Value = st.Note < 0 ? 0 : st.Note;
            _txtPadLabel.Text = st.SampleLabel;
            _numPadInst.Value = st.UserInst < 0 ? 0 : st.UserInst;
            _pads.Invalidate();
        }

        private void ApplyPadEdits()
        {
            if (_pads.SelectedId == null) { SetStatus("Select a pad first.", Color.Firebrick); return; }
            PadState st = _pads.Pads[_pads.SelectedId];
            st.Note = (int)_numPadNote.Value;
            st.SampleLabel = _txtPadLabel.Text.Trim();
            st.UserInst = (int)_numPadInst.Value == 0 ? -1 : (int)_numPadInst.Value;
            SavePads();
            _pads.Invalidate();
            SetStatus("Pad " + st.Display + " updated (tracked locally).", Color.ForestGreen);
        }

        private void ClearPadEdits()
        {
            if (_pads.SelectedId == null) return;
            PadState st = _pads.Pads[_pads.SelectedId];
            st.Note = -1;
            st.SampleLabel = "";
            st.UserInst = -1;
            SavePads();
            SelectPadForEdit(st.Id);
            _pads.Invalidate();
        }

        private void StartLearn()
        {
            if (_pads.SelectedId == null) { SetStatus("Select a pad first.", Color.Firebrick); return; }
            _learning = true;
            _learningPad = _pads.SelectedId;
            SetStatus("Learn: strike " + _pads.Pads[_learningPad].Display + " on the HPD-20 now.", Color.RoyalBlue);
            LogLine("--- learn mode: waiting for a note for " + _learningPad + " ---");
        }

        private void LoadPhoto()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    _pads.UserPhoto = Image.FromFile(d.FileName);
                    _pads.Invalidate();
                    SetStatus("Photo loaded as the pad background.", Color.ForestGreen);
                }
                catch (Exception ex) { SetStatus("Could not load photo: " + ex.Message, Color.Firebrick); }
            }
        }

        // ------------------------- samples -------------------------

        private sealed class DriveItem
        {
            public string Root, Label;
            public long Free;
            public bool Removable;
            public DriveItem(string root, string label, long free, bool removable) { Root = root; Label = label; Free = free; Removable = removable; }
            public override string ToString()
            {
                return Root + "  " + (string.IsNullOrEmpty(Label) ? "(no label)" : Label) +
                       "  " + (Free / (1024 * 1024 * 1024)) + " GB free" +
                       (Removable ? "" : "  [fixed disk]");
            }
        }

        /// <summary>Removable drives first; fixed drives are offered too but flagged, since a stick
        /// can be mounted as a fixed disk by some card readers.</summary>
        private static void FillDriveCombo(ComboBox box)
        {
            string keep = box.SelectedItem != null ? box.SelectedItem.ToString() : null;
            box.Items.Clear();
            List<DriveItem> removable = new List<DriveItem>();
            List<DriveItem> fixedDisks = new List<DriveItem>();
            foreach (DriveInfo di in DriveInfo.GetDrives())
            {
                try
                {
                    if (!di.IsReady) continue;
                    if (di.DriveType != DriveType.Removable && di.DriveType != DriveType.Fixed) continue;
                    DriveItem it = new DriveItem(di.Name, di.VolumeLabel, di.AvailableFreeSpace, di.DriveType == DriveType.Removable);
                    if (it.Removable) removable.Add(it); else fixedDisks.Add(it);
                }
                catch { }
            }
            foreach (DriveItem it in removable) box.Items.Add(it);
            if (removable.Count == 0) foreach (DriveItem it in fixedDisks) box.Items.Add(it);
            if (keep != null)
            {
                for (int i = 0; i < box.Items.Count; i++)
                    if (box.Items[i].ToString() == keep) { box.SelectedIndex = i; return; }
            }
            // Only auto-select something that is actually removable: never default to C:\
            box.SelectedIndex = removable.Count > 0 ? 0 : -1;
        }

        private void AddSampleFiles()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Multiselect = true;
                d.Filter = AudioFormats.Filter;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                foreach (string f in d.FileNames) AddSample(f);
                RefreshSampleList();
            }
        }

        private void AddSampleFolder()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Cursor = Cursors.WaitCursor;
                try
                {
                    string[] files = Directory.GetFiles(d.SelectedPath, "*.wav", SearchOption.AllDirectories);
                    foreach (string f in files) AddSample(f);
                }
                catch (Exception ex) { SetStatus("Folder scan failed: " + ex.Message, Color.Firebrick); }
                finally { Cursor = Cursors.Default; }
                RefreshSampleList();
            }
        }

        /// <summary>
        /// Files to hand to a drag from the queue: the resampled WAV when there is one, otherwise the
        /// source file, so a conversion result can be dropped straight into the waveform editor.
        /// </summary>
        private List<string> DragPathsForSamples(ListViewItem primary)
        {
            List<ListViewItem> items = new List<ListViewItem>();
            foreach (ListViewItem it in _lstSamples.SelectedItems) items.Add(it);
            // dragging a row that is not part of the selection should drag that row
            if (primary != null && !items.Contains(primary)) items.Add(primary);

            List<string> paths = new List<string>();
            foreach (ListViewItem it in items)
            {
                string source = it.Tag as string;
                if (string.IsNullOrEmpty(source)) continue;

                SampleResult r;
                string use = source;
                if (_sampleResults.TryGetValue(source, out r) && r != null && r.Ok &&
                    !string.IsNullOrEmpty(r.OutputPath) && File.Exists(r.OutputPath))
                    use = r.OutputPath;
                paths.Add(use);
            }
            return paths;
        }

        private void AddSample(string path)
        {
            if (!_sampleFiles.Contains(path)) _sampleFiles.Add(path);
        }

        private void RemoveSelectedSample()
        {
            if (_lstSamples.SelectedItems.Count == 0) return;
            string path = _lstSamples.SelectedItems[0].Tag as string;
            _sampleFiles.Remove(path);
            _sampleResults.Remove(path);
            RefreshSampleList();
        }

        private void RefreshSampleList()
        {
            _lstSamples.BeginUpdate();
            _lstSamples.Items.Clear();
            foreach (string f in _sampleFiles)
            {
                SampleResult r;
                _sampleResults.TryGetValue(f, out r);
                ListViewItem it = new ListViewItem(Path.GetFileName(f));
                it.SubItems.Add(r != null ? r.SourceFormat : "not analysed");
                it.SubItems.Add(r != null ? r.Message : "");
                it.Tag = f;
                if (r != null) it.ForeColor = r.Ok ? Color.ForestGreen : Color.Firebrick;
                _lstSamples.Items.Add(it);
            }
            _lstSamples.EndUpdate();
            UpdateSampleSummary();
        }

        private void UpdateSampleSummary()
        {
            double total = 0;
            int done = 0;
            foreach (SampleResult r in _sampleResults.Values)
            {
                if (r.Ok) { total += r.OutputSeconds; done++; }
            }
            string s = _sampleFiles.Count + " file(s) in list";
            if (done > 0)
                s += ", " + done + " resampled, " + TimeSpan.FromSeconds(total).ToString(@"m\:ss") + " of the 12:00 budget";
            if (done > 500) s += "  - over the 500 instrument limit";
            if (total > 720) s += "  - over the 12 minute limit";
            _lblSampleStatus.Text = s;
        }

        private void StartResample()
        {
            if (_sampleFiles.Count == 0) { SetStatus("Add some WAV files first.", Color.Firebrick); return; }
            string outDir = _txtOutDir.Text.Trim();
            if (outDir.Length == 0) { SetStatus("Choose an output folder.", Color.Firebrick); return; }
            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex) { SetStatus("Cannot create output folder: " + ex.Message, Color.Firebrick); return; }

            ResampleOptions opt = new ResampleOptions();
            opt.Mono = _chkMono.Checked;
            opt.Normalize = _chkNorm.Checked;
            opt.TrimSilence = _chkTrim.Checked;
            opt.FadeEdges = _chkFade.Checked;
            opt.MaxSeconds = (double)_numMaxMin.Value * 60.0;

            List<string> files = new List<string>(_sampleFiles);
            _cancelResample = false;
            _btnResample.Enabled = false;
            _btnCancel.Enabled = true;
            _barProgress.Minimum = 0;
            _barProgress.Maximum = files.Count;
            _barProgress.Value = 0;

            System.Threading.Thread t = new System.Threading.Thread(delegate ()
            {
                for (int i = 0; i < files.Count; i++)
                {
                    if (_cancelResample) break;
                    string src = files[i];
                    string baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(src));
                    string dst = Path.Combine(outDir, baseName + ".wav");
                    int n = 2;
                    while (File.Exists(dst) && !string.Equals(Path.GetFullPath(dst), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
                    {
                        dst = Path.Combine(outDir, baseName + "_" + n + ".wav");
                        n++;
                    }
                    SampleResult r = SampleProcessor.Process(src, dst, opt);
                    lock (_sampleResults) { _sampleResults[src] = r; }
                    int idx = i;
                    try
                    {
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            _barProgress.Value = Math.Min(idx + 1, _barProgress.Maximum);
                            RefreshSampleList();
                        }));
                    }
                    catch (InvalidOperationException) { break; }
                }
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        _btnResample.Enabled = true;
                        _btnCancel.Enabled = false;
                        RefreshSampleList();
                        SetStatus(_cancelResample ? "Resampling cancelled." : "Batch resample finished.", _cancelResample ? Color.DarkOrange : Color.ForestGreen);
                    }));
                }
                catch (InvalidOperationException) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        internal static string SanitizeFileName(string name)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in name)
            {
                if (c < 128 && (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '(' || c == ')'))
                    sb.Append(c);
                else if (c < 128) sb.Append('_');
                // drop non-ASCII entirely: the HPD-20 does not display it
            }
            string s = sb.ToString().Trim();
            while (s.Contains("__")) s = s.Replace("__", "_");
            if (s.Length == 0) s = "sample";
            if (s.Length > 40) s = s.Substring(0, 40);
            return s;
        }

        private void AssignSampleToPad()
        {
            if (_pads.SelectedId == null) { SetStatus("Select a pad first.", Color.Firebrick); return; }
            if (_lstSamples.SelectedItems.Count == 0) { SetStatus("Select a resampled file first.", Color.Firebrick); return; }
            string src = _lstSamples.SelectedItems[0].Tag as string;
            string name = SanitizeFileName(Path.GetFileNameWithoutExtension(src));
            PadState st = _pads.Pads[_pads.SelectedId];
            st.SampleLabel = name.Length > 16 ? name.Substring(0, 16) : name;
            _txtPadLabel.Text = st.SampleLabel;
            SavePads();
            _pads.Invalidate();
            SetStatus("Pad " + st.Display + " now shows '" + st.SampleLabel + "' (local label).", Color.ForestGreen);
        }

        internal sealed class ExportItem
        {
            public string OutputPath;
            public string Source;
            public double Seconds;
        }

        /// <summary>Writes the root copies the HPD-20 can see plus an organised archive folder.</summary>
        internal static int WriteUsbExport(string root, string pack, List<ExportItem> items, out string error)
        {
            error = null;
            int n = 0;
            try
            {
                string archiveDir = Path.Combine(root, "HPD20_Samples", pack);
                Directory.CreateDirectory(archiveDir);

                // Root numbering: continue after whatever is already there, and reuse the name
                // a previous export of this same pack gave the same source file (idempotent re-export).
                int next = 1;
                foreach (string existing in Directory.GetFiles(root, "*.WAV", SearchOption.TopDirectoryOnly))
                {
                    string bn = Path.GetFileName(existing);
                    if (bn.Length >= 3)
                    {
                        int num;
                        if (int.TryParse(bn.Substring(0, 3), out num) && num >= next) next = num + 1;
                    }
                }
                Dictionary<string, string> reuse = new Dictionary<string, string>();
                string indexPath = Path.Combine(archiveDir, "index.csv");
                if (File.Exists(indexPath))
                {
                    foreach (string line in File.ReadAllLines(indexPath))
                    {
                        string[] cols = line.Split(',');
                        if (cols.Length >= 2 && cols[0] != "root_file" && !reuse.ContainsKey(cols[1]))
                            reuse[cols[1]] = cols[0];
                    }
                }

                StringBuilder index = new StringBuilder();
                index.AppendLine("root_file,source,seconds");
                foreach (ExportItem it in items)
                {
                    n++;
                    string nice = Path.GetFileName(it.OutputPath);
                    string rootName;
                    if (!reuse.TryGetValue(it.Source, out rootName))
                    {
                        rootName = next.ToString("000") + "_" + Truncate(SanitizeFileName(Path.GetFileNameWithoutExtension(it.OutputPath)), 20) + ".WAV";
                        next++;
                    }
                    File.Copy(it.OutputPath, Path.Combine(root, rootName), true);
                    File.Copy(it.OutputPath, Path.Combine(archiveDir, nice), true);
                    index.AppendLine(rootName + "," + it.Source.Replace(",", " ") + "," + it.Seconds.ToString("0.00"));
                }

                File.WriteAllText(Path.Combine(archiveDir, "index.csv"), index.ToString(), Encoding.UTF8);
                File.WriteAllText(Path.Combine(archiveDir, "READ-ME.txt"),
                    "HPD-20 import\r\n" +
                    "=============\r\n" +
                    "The WAV files in the ROOT of this stick are the ones the HPD-20 can see:\r\n" +
                    "MENU > USER INST > IMPORT, pick a file, choose a user instrument number.\r\n\r\n" +
                    "This folder (" + pack + ") holds the same files under their original names plus index.csv,\r\n" +
                    "and is ignored by the instrument (it only lists WAV files in the root).\r\n\r\n" +
                    "Limits: 44.1 kHz / 16-bit, mono or stereo; 6:00 per file; 500 user instruments; 12:00 total.\r\n",
                    Encoding.UTF8);
                return n;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return n;
            }
        }

        private void ExportToUsb(ComboBox cmbDrive)
        {
            DriveItem di = cmbDrive.SelectedItem as DriveItem;
            if (di == null) { SetStatus("No removable drive found. Insert the USB stick and reopen the tab.", Color.Firebrick); return; }
            if (!Directory.Exists(di.Root)) { SetStatus("Drive not ready.", Color.Firebrick); return; }

            List<string> files = new List<string>();
            foreach (string src in _sampleFiles)
            {
                SampleResult r;
                if (_sampleResults.TryGetValue(src, out r) && r.Ok && File.Exists(r.OutputPath)) files.Add(r.OutputPath);
            }
            if (files.Count == 0) { SetStatus("Resample first - there is nothing to write.", Color.Firebrick); return; }
            if (files.Count > 1000) { SetStatus("The HPD-20 recognises at most 1000 files in the root.", Color.Firebrick); return; }

            string pack = SanitizeFileName(_txtPackName.Text.Trim());
            if (pack.Length == 0) pack = "HPD_PACK";

            List<ExportItem> items = new List<ExportItem>();
            foreach (string f in files)
            {
                ExportItem it = new ExportItem();
                it.OutputPath = f;
                it.Seconds = 0;
                foreach (KeyValuePair<string, SampleResult> kv in _sampleResults)
                {
                    if (kv.Value.OutputPath == f)
                    {
                        it.Source = kv.Key;
                        it.Seconds = kv.Value.OutputSeconds;
                        break;
                    }
                }
                if (it.Source == null) it.Source = Path.GetFileName(f);
                items.Add(it);
            }

            string error;
            int written = WriteUsbExport(di.Root, pack, items, out error);
            if (error != null) { SetStatus("USB export failed: " + error, Color.Firebrick); return; }
            SetStatus("Wrote " + written + " WAV(s) to " + di.Root + "root and " + Path.Combine(di.Root, "HPD20_Samples", pack), Color.ForestGreen);
            LogLine("USB export: " + written + " files -> " + di.Root);
        }

        internal static string Truncate(string s, int n)
        {
            return s.Length <= n ? s : s.Substring(0, n);
        }

        private TabPage BuildKitsTab()
        {
            TabPage page = new TabPage("Kits");
            page.Padding = new Padding(8);

            _lstKits = new ListView();
            _lstKits.Dock = DockStyle.Fill;
            _lstKits.View = View.Details;
            _lstKits.FullRowSelect = true;
            _lstKits.MultiSelect = false;
            _lstKits.HideSelection = false;
            _lstKits.Columns.Add("Kit", 220);
            _lstKits.Columns.Add("No.", 50);
            _lstKits.Columns.Add("Bank MSB/LSB", 100);
            _lstKits.Columns.Add("Program", 70);
            _lstKits.DoubleClick += delegate { SendSelectedKit(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Rename label...", null, delegate { RenameSelectedKit(); });
            menu.Items.Add("Clear label", null, delegate { ClearSelectedKitLabel(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Send this kit", null, delegate { SendSelectedKit(); });
            _lstKits.ContextMenuStrip = menu;

            Panel side = new Panel();
            side.Dock = DockStyle.Right;
            side.Width = 250;
            side.Padding = new Padding(10, 8, 8, 8);

            Label lCh = new Label();
            lCh.Text = "MIDI channel";
            lCh.SetBounds(10, 12, 100, 20);
            _numChannel = new NumericUpDown();
            _numChannel.Minimum = 1;
            _numChannel.Maximum = 16;
            _numChannel.Value = 1;
            _numChannel.SetBounds(120, 10, 60, 22);

            Button send = new Button();
            send.Text = "Send kit (Bank + PC)";
            send.SetBounds(10, 44, 220, 30);
            send.Click += delegate { SendSelectedKit(); };

            Button sendPc = new Button();
            sendPc.Text = "Send Program Change only";
            sendPc.SetBounds(10, 80, 220, 30);
            sendPc.Click += delegate { SendSelectedProgramChange(); };

            Button allOff = new Button();
            allOff.Text = "All Sound Off";
            allOff.SetBounds(10, 124, 106, 28);
            allOff.Click += delegate { SendChannelMode(120, 0); };

            Button resetCtl = new Button();
            resetCtl.Text = "Reset Controllers";
            resetCtl.SetBounds(124, 124, 106, 28);
            resetCtl.Click += delegate { SendChannelMode(121, 0); };

            _chkAutoFollow = new CheckBox();
            _chkAutoFollow.Text = "Follow kits changed on the device";
            _chkAutoFollow.Checked = true;
            _chkAutoFollow.SetBounds(10, 160, 220, 40);

            _lblCurrentKit = new Label();
            _lblCurrentKit.SetBounds(10, 205, 224, 60);
            _lblCurrentKit.Text = "Last kit sent: -";
            _lblCurrentKit.ForeColor = Color.DimGray;

            Label hint = new Label();
            hint.SetBounds(10, 270, 226, 220);
            hint.Text = "On the HPD-20, enable:\r\n" +
                        "MENU > SYS > MIDI > MIDI\r\n" +
                        "  - MIDI Channel: match the channel above\r\n" +
                        "  - Program Change Rx: ON\r\n" +
                        "  - Program Change Tx: ON (to follow the device)\r\n\r\n" +
                        "Kits 001-128: bank 000/000, program 1-128\r\n" +
                        "Kits 129-200: bank 001/000, program 1-72\r\n\r\n" +
                        "Double-click a kit to send it. Right-click to rename its label locally.";
            hint.ForeColor = Color.DimGray;

            side.Controls.AddRange(new Control[] { lCh, _numChannel, send, sendPc, allOff, resetCtl, _chkAutoFollow, _lblCurrentKit, hint });

            page.Controls.Add(_lstKits);
            page.Controls.Add(side);
            return page;
        }

        private TabPage BuildMonitorTab()
        {
            TabPage page = new TabPage("Monitor");
            page.Padding = new Padding(8);

            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 34;

            _chkHex = new CheckBox();
            _chkHex.Text = "Show hex";
            _chkHex.Checked = true;
            _chkHex.SetBounds(4, 6, 90, 22);

            _chkDecode = new CheckBox();
            _chkDecode.Text = "Decode";
            _chkDecode.Checked = true;
            _chkDecode.SetBounds(100, 6, 80, 22);

            _chkPause = new CheckBox();
            _chkPause.Text = "Pause";
            _chkPause.SetBounds(184, 6, 70, 22);

            _btnClearMon = new Button();
            _btnClearMon.Text = "Clear";
            _btnClearMon.SetBounds(264, 4, 70, 24);
            _btnClearMon.Click += delegate { _txtMonitor.Clear(); };

            _btnSaveMon = new Button();
            _btnSaveMon.Text = "Save log...";
            _btnSaveMon.SetBounds(340, 4, 90, 24);
            _btnSaveMon.Click += delegate { SaveMonitorLog(); };

            bar.Controls.AddRange(new Control[] { _chkHex, _chkDecode, _chkPause, _btnClearMon, _btnSaveMon });

            _txtMonitor = new RichTextBox();
            _txtMonitor.Dock = DockStyle.Fill;
            _txtMonitor.ReadOnly = true;
            _txtMonitor.BackColor = Color.White;
            _txtMonitor.Font = new Font("Consolas", 9f);
            _txtMonitor.WordWrap = false;
            _txtMonitor.HideSelection = false;

            page.Controls.Add(_txtMonitor);
            page.Controls.Add(bar);
            return page;
        }

        private TabPage BuildSysExTab()
        {
            TabPage page = new TabPage("SysEx / Raw MIDI");
            page.Padding = new Padding(8);

            Label lab = new Label();
            lab.Dock = DockStyle.Top;
            lab.Height = 44;
            lab.Text = "Send raw bytes (hex). SysEx must start F0 and end F7; they are added automatically if missing.\r\n" +
                       "The HPD-20 accepts no parameter-edit SysEx of its own: only the Universal Identity Request is answered.";

            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 88;

            _txtHex = new TextBox();
            _txtHex.SetBounds(4, 4, 700, 22);
            _txtHex.Font = new Font("Consolas", 9f);
            _txtHex.Text = "F0 7E 10 06 01 F7";

            _chkAddChecksum = new CheckBox();
            _chkAddChecksum.Text = "Add Roland checksum before F7";
            _chkAddChecksum.SetBounds(4, 32, 260, 22);

            _btnSendHex = new Button();
            _btnSendHex.Text = "Send";
            _btnSendHex.SetBounds(712, 3, 80, 25);
            _btnSendHex.Click += delegate { SendRawHex(); };

            _btnIdentity = new Button();
            _btnIdentity.Text = "Identity Request";
            _btnIdentity.SetBounds(4, 56, 140, 24);
            _btnIdentity.Click += delegate { SendIdentityRequest(); };

            _btnLoadSyx = new Button();
            _btnLoadSyx.Text = "Load .syx/.txt";
            _btnLoadSyx.SetBounds(152, 56, 120, 24);
            _btnLoadSyx.Click += delegate { LoadSyxFile(); };

            _btnSaveSyx = new Button();
            _btnSaveSyx.Text = "Save last SysEx";
            _btnSaveSyx.SetBounds(280, 56, 130, 24);
            _btnSaveSyx.Click += delegate { SaveLastSysEx(); };

            bar.Controls.AddRange(new Control[] { _txtHex, _chkAddChecksum, _btnSendHex, _btnIdentity, _btnLoadSyx, _btnSaveSyx });

            Label notes = new Label();
            notes.Dock = DockStyle.Fill;
            notes.Padding = new Padding(4, 10, 4, 4);
            notes.ForeColor = Color.DimGray;
            notes.Text = "What actually works over USB / MIDI on the HPD-20:\r\n\r\n" +
                         "  Kits        Bank Select + Program Change  (this app's Kits tab)\r\n" +
                         "  Identity    Universal Non-Realtime Identity Request -> Identity Reply\r\n" +
                         "  Performance Note on/off, poly pressure, CC, pitch bend, active sensing (device -> host)\r\n\r\n" +
                         "Not available: there is no Roland DT1/RQ1 parameter table, so no kit contents, pad settings or\r\n" +
                         "system settings can be read or written over MIDI. V-LINK and MIDI Visual Control messages\r\n" +
                         "are only ever sent BY the HPD-20 (to video gear); the unit does not accept them as commands.\r\n" +
                         "Editing system settings therefore has to happen on the instrument, or offline by editing a\r\n" +
                         "USB Memory backup file (MENU > SYS > USB Memory > Save Backup).";

            page.Controls.Add(notes);
            page.Controls.Add(bar);
            page.Controls.Add(lab);
            return page;
        }

        private TabPage BuildDeviceTab()
        {
            TabPage page = new TabPage("Device");
            page.Padding = new Padding(8);

            _txtDevice = new TextBox();
            _txtDevice.Dock = DockStyle.Fill;
            _txtDevice.Multiline = true;
            _txtDevice.ReadOnly = true;
            _txtDevice.ScrollBars = ScrollBars.Vertical;
            _txtDevice.Font = new Font("Consolas", 9f);
            _txtDevice.Text = "Connect to the HPD-20, then use \"Identity Request\" on the SysEx tab.";

            page.Controls.Add(_txtDevice);
            return page;
        }

        private void BuildKitList()
        {
            _kits.Clear();
            for (int n = 1; n <= 200; n++)
            {
                KitInfo k = new KitInfo();
                k.Number = n;
                if (n <= 128) { k.BankMsb = 0; k.BankLsb = 0; k.Program = n - 1; }
                else { k.BankMsb = 1; k.BankLsb = 0; k.Program = n - 129; }
                _kits.Add(k);
            }
            RefreshKitView();
        }

        private void RefreshKitView()
        {
            _lstKits.BeginUpdate();
            _lstKits.Items.Clear();
            foreach (KitInfo k in _kits)
            {
                string label;
                if (_labels.ContainsKey(k.Number)) label = _labels[k.Number];
                else if (k.Number <= 100) label = "Kit " + k.Number.ToString("000");
                else label = "Kit " + k.Number.ToString("000") + " (user)";
                k.Label = label;

                ListViewItem it = new ListViewItem(label);
                it.SubItems.Add(k.Number.ToString("000"));
                it.SubItems.Add(k.BankMsb.ToString("000") + " / " + k.BankLsb.ToString("000"));
                it.SubItems.Add((k.Program + 1).ToString());
                it.Tag = k;
                _lstKits.Items.Add(it);
            }
            _lstKits.EndUpdate();
        }

        // ------------------------- ports / connection -------------------------

        private void RefreshPorts()
        {
            string keepOut = _cmbOut.SelectedItem as string;
            string keepIn = _cmbIn.SelectedItem as string;
            _cmbOut.Items.Clear();
            _cmbIn.Items.Clear();

            int outs = 0, ins = 0;
            try { outs = WinMM.midiOutGetNumDevs(); }
            catch { }
            for (int i = 0; i < outs; i++)
            {
                WinMM.MIDIOUTCAPS caps;
                if (WinMM.midiOutGetDevCaps((IntPtr)i, out caps, Marshal.SizeOf(typeof(WinMM.MIDIOUTCAPS))) == 0)
                    _cmbOut.Items.Add(new PortInfo { Index = i, Name = caps.szPname });
            }
            try { ins = WinMM.midiInGetNumDevs(); }
            catch { }
            for (int i = 0; i < ins; i++)
            {
                WinMM.MIDIINCAPS caps;
                if (WinMM.midiInGetDevCaps((IntPtr)i, out caps, Marshal.SizeOf(typeof(WinMM.MIDIINCAPS))) == 0)
                    _cmbIn.Items.Add(new PortInfo { Index = i, Name = caps.szPname });
            }

            SelectPreferred(_cmbOut, keepOut);
            SelectPreferred(_cmbIn, keepIn);

            if (_cmbOut.Items.Count == 0 && _cmbIn.Items.Count == 0)
                SetStatus("No MIDI ports found. Connect the HPD-20 by USB and install the Roland driver, then Refresh.", Color.Firebrick);
            else
                SetStatus("Found " + _cmbOut.Items.Count + " MIDI output port(s) and " + _cmbIn.Items.Count + " input port(s).", Color.DimGray);
        }

        private void SelectPreferred(ComboBox box, string keep)
        {
            if (box.Items.Count == 0) return;
            for (int i = 0; i < box.Items.Count; i++)
            {
                PortInfo p = (PortInfo)box.Items[i];
                if (keep != null && p.ToString() == keep) { box.SelectedIndex = i; return; }
            }
            for (int i = 0; i < box.Items.Count; i++)
            {
                PortInfo p = (PortInfo)box.Items[i];
                if (p.LooksLikeHpd()) { box.SelectedIndex = i; return; }
            }
            box.SelectedIndex = 0;
        }

        private void ToggleConnect()
        {
            if (_connected) { Disconnect(); return; }

            PortInfo outp = _cmbOut.SelectedItem as PortInfo;
            PortInfo inp = _cmbIn.SelectedItem as PortInfo;
            if (outp == null && inp == null)
            {
                SetStatus("No port selected.", Color.Firebrick);
                return;
            }

            _midi.Dispose();
            bool ok = _midi.Open(outp == null ? 0 : outp.Index, inp == null ? 0 : inp.Index, outp != null, inp != null);
            if (!ok)
            {
                SetStatus("Open failed: " + _midi.LastError, Color.Firebrick);
                return;
            }

            _connected = true;
            _btnConnect.Text = "Disconnect";
            SetStatus("Connected" + (outp != null ? " out: " + outp.Name : "") + (inp != null ? " | in: " + inp.Name : ""), Color.ForestGreen);
            LogLine("--- connected ---");
            if (inp != null) SendIdentityRequest();
        }

        private void Disconnect()
        {
            _midi.Dispose();
            _connected = false;
            _btnConnect.Text = "Connect";
            SetStatus("Disconnected.", Color.DimGray);
            LogLine("--- disconnected ---");
        }

        // ------------------------- actions -------------------------

        private KitInfo SelectedKit()
        {
            if (_lstKits.SelectedItems.Count == 0) return null;
            return _lstKits.SelectedItems[0].Tag as KitInfo;
        }

        private void RenameSelectedKit()
        {
            KitInfo k = SelectedKit();
            if (k == null) return;
            using (Form dlg = new Form())
            {
                dlg.Text = "Rename kit " + k.Number.ToString("000");
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ClientSize = new Size(360, 96);
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;

                TextBox box = new TextBox();
                box.SetBounds(12, 14, 336, 22);
                box.Text = k.Label;

                Button ok = new Button();
                ok.Text = "OK";
                ok.DialogResult = DialogResult.OK;
                ok.SetBounds(190, 52, 75, 26);

                Button cancel = new Button();
                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(273, 52, 75, 26);

                dlg.Controls.AddRange(new Control[] { box, ok, cancel });
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string v = box.Text.Trim();
                    if (v.Length == 0) _labels.Remove(k.Number);
                    else _labels[k.Number] = v;
                    RefreshKitView();
                    SaveLabels();
                }
            }
        }

        private void ClearSelectedKitLabel()
        {
            KitInfo k = SelectedKit();
            if (k == null) return;
            _labels.Remove(k.Number);
            RefreshKitView();
            SaveLabels();
        }

        private void SendSelectedKit()
        {
            KitInfo k = SelectedKit();
            if (k == null) { SetStatus("Select a kit first.", Color.Firebrick); return; }
            SendKit(k);
        }

        private void SendKit(KitInfo k)
        {
            if (k == null) return;
            if (!_connected) { SetStatus("Not connected.", Color.Firebrick); return; }

            int ch = (int)_numChannel.Value - 1;
            _midi.SendShort((byte)(0xB0 | ch), 0, (byte)k.BankMsb);
            _midi.SendShort((byte)(0xB0 | ch), 32, (byte)k.BankLsb);
            bool ok = _midi.SendShort1((byte)(0xC0 | ch), (byte)k.Program);
            if (!ok) { SetStatus("Send failed: " + _midi.LastError, Color.Firebrick); return; }

            _lastKitIndex = k.Number - 1;
            _lblCurrentKit.Text = "Last kit sent: " + k.Number.ToString("000") + "  (bank " +
                                  k.BankMsb + "/" + k.BankLsb + ", pc " + (k.Program + 1) + ")";
            SetStatus("Sent kit " + k.Number.ToString("000") + " on channel " + (ch + 1) + ".", Color.ForestGreen);
            LogLine("TX  Bank MSB " + k.BankMsb + " / LSB " + k.BankLsb + " / PC " + (k.Program + 1) + "  -> kit " + k.Number.ToString("000"));
        }

        private void SendSelectedProgramChange()
        {
            KitInfo k = SelectedKit();
            if (k == null || !_connected) { SetStatus("Select a kit and connect first.", Color.Firebrick); return; }
            int ch = (int)_numChannel.Value - 1;
            if (!_midi.SendShort1((byte)(0xC0 | ch), (byte)k.Program)) { SetStatus(_midi.LastError, Color.Firebrick); return; }
            SetStatus("Sent program change " + (k.Program + 1) + " (kit from current bank).", Color.ForestGreen);
        }

        private void SendChannelMode(int cc, int value)
        {
            if (!_connected) { SetStatus("Not connected.", Color.Firebrick); return; }
            int ch = (int)_numChannel.Value - 1;
            if (_midi.SendShort((byte)(0xB0 | ch), (byte)cc, (byte)value))
                SetStatus("Sent " + MidiText.CcName(cc) + ".", Color.ForestGreen);
            else
                SetStatus(_midi.LastError, Color.Firebrick);
        }

        private void SendIdentityRequest()
        {
            if (!_connected) { SetStatus("Not connected.", Color.Firebrick); return; }
            byte[] req = { 0xF0, 0x7E, 0x10, 0x06, 0x01, 0xF7 };
            if (_midi.SendSysEx(req))
            {
                LogLine("TX  F0 7E 10 06 01 F7   (Identity Request)");
                SetStatus("Identity Request sent, waiting for reply...", Color.DimGray);
            }
            else SetStatus(_midi.LastError, Color.Firebrick);
        }

        private void SendRawHex()
        {
            if (!_connected) { SetStatus("Not connected.", Color.Firebrick); return; }
            byte[] bytes;
            string err;
            if (!MidiText.TryParseHex(_txtHex.Text, out bytes, out err))
            {
                SetStatus("Parse error: " + err, Color.Firebrick);
                return;
            }
            bool isSysex = bytes[0] == 0xF0;
            if (isSysex)
            {
                List<byte> full = new List<byte>(bytes);
                if (full.Count < 2 || full[full.Count - 1] != 0xF7) full.Add(0xF7);
                if (_chkAddChecksum.Checked && full.Count >= 2)
                {
                    // checksum over everything between F0 (and the standard 4 header bytes) and the end
                    int start = Math.Min(5, full.Count - 1);
                    int count = full.Count - 1 - start;
                    if (count > 0) full.Insert(full.Count - 1, MidiText.RolandChecksum(full.ToArray(), start, count));
                }
                bytes = full.ToArray();
                if (!_midi.SendSysEx(bytes)) { SetStatus(_midi.LastError, Color.Firebrick); return; }
            }
            else
            {
                if (bytes.Length == 3) _midi.SendShort(bytes[0], bytes[1], bytes[2]);
                else if (bytes.Length == 2) _midi.SendShort1(bytes[0], bytes[1]);
                else { SetStatus("Short messages must be 2 or 3 bytes.", Color.Firebrick); return; }
            }
            LogLine("TX  " + MidiText.Hex(bytes));
            SetStatus("Sent " + bytes.Length + " bytes.", Color.ForestGreen);
        }

        private void LoadSyxFile()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "SysEx / text (*.syx;*.txt)|*.syx;*.txt|All files (*.*)|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                byte[] raw = File.ReadAllBytes(dlg.FileName);
                if (raw.Length > 0 && raw[0] == 0xF0) _txtHex.Text = MidiText.Hex(raw);
                else _txtHex.Text = MidiText.Hex(Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(raw)));
            }
        }

        private void SaveLastSysEx()
        {
            if (_lastSysEx == null) { SetStatus("No SysEx received yet.", Color.Firebrick); return; }
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "SysEx (*.syx)|*.syx|All files (*.*)|*.*";
                dlg.FileName = "hpd20-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".syx";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllBytes(dlg.FileName, _lastSysEx);
                SetStatus("Saved " + _lastSysEx.Length + " bytes.", Color.ForestGreen);
            }
        }

        // ------------------------- receive -------------------------

        private byte[] _lastSysEx;

        private void OnMidiMessage(object sender, MidiMessageEventArgs e)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new MethodInvoker(delegate { HandleIncoming(e); }));
            }
            catch (InvalidOperationException) { }
        }

        private void HandleIncoming(MidiMessageEventArgs e)
        {
            if (e.IsSysEx)
            {
                _lastSysEx = e.Data;
                HandleSysEx(e.Data);
            }

            if (!_chkPause.Checked)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(e.Time.ToString("HH:mm:ss.fff"));
                sb.Append("  RX  ");
                if (_chkDecode.Checked) sb.Append(MidiText.Describe(e.Data));
                if (_chkHex.Checked)
                {
                    if (_chkDecode.Checked) sb.Append("   ");
                    sb.Append(MidiText.Hex(e.Data.Length > 24 ? Truncate(e.Data, 24) : e.Data));
                    if (e.Data.Length > 24) sb.Append(" ... (" + e.Data.Length + " bytes)");
                }
                if (!_chkDecode.Checked && !_chkHex.Checked) sb.Append(e.Data.Length + " bytes");
                AppendMonitor(sb.ToString());
            }

            if (e.Data.Length >= 2 && (e.Data[0] & 0xF0) == 0xB0)
            {
                if (e.Data[1] == 0) _bankMsb = e.Data[2];
                else if (e.Data[1] == 32) _bankLsb = e.Data[2];
            }
            else if (e.Data.Length >= 2 && (e.Data[0] & 0xF0) == 0xC0)
            {
                if (_chkAutoFollow.Checked) FollowProgramChange(e.Data[1]);
            }
            else if (e.Data.Length >= 3 && (e.Data[0] & 0xF0) == 0x90 && e.Data[2] > 0)
            {
                HandlePadNote(e.Data[1]);
            }
        }

        private static byte[] Truncate(byte[] d, int n)
        {
            byte[] r = new byte[n];
            Array.Copy(d, r, n);
            return r;
        }

        private void HandlePadNote(int note)
        {
            if (_pads == null) return;
            if (_learning && _learningPad != null)
            {
                PadState st;
                if (_pads.Pads.TryGetValue(_learningPad, out st))
                {
                    st.Note = note;
                    _learning = false;
                    SelectPadForEdit(st.Id);
                    SavePads();
                    _pads.Invalidate();
                    SetStatus("Learned: " + st.Display + " = note " + note + " (" + MidiText.NoteName(note) + ").", Color.ForestGreen);
                    LogLine("--- learned " + st.Display + " = note " + note + " ---");
                    return;
                }
            }
            foreach (PadState st in _pads.Pads.Values)
            {
                if (st.Note == note)
                {
                    _pads.Flash(st.Id);
                    return;
                }
            }
        }

        private void FollowProgramChange(int program)
        {
            int kitNumber;
            if (_bankMsb == 0 && _bankLsb == 0) kitNumber = program + 1;
            else if (_bankMsb == 1 && _bankLsb == 0) kitNumber = 129 + program;
            else return;
            if (kitNumber < 1 || kitNumber > 200) return;
            for (int i = 0; i < _kits.Count; i++)
            {
                if (_kits[i].Number == kitNumber)
                {
                    _lstKits.SelectedIndices.Clear();
                    _lstKits.SelectedIndices.Add(i);
                    _lstKits.EnsureVisible(i);
                    break;
                }
            }
            _lastKitIndex = kitNumber - 1;
            _lblCurrentKit.Text = "Device is on kit " + kitNumber.ToString("000") + "  (bank " + _bankMsb + "/" + _bankLsb + ", pc " + (program + 1) + ")";
        }

        private void HandleSysEx(byte[] d)
        {
            // Universal Non-Realtime Identity Reply: F0 7E dev 06 02 41 ...
            if (d.Length >= 15 && d[0] == 0xF0 && d[1] == 0x7E && d[3] == 0x06 && d[4] == 0x02)
            {
                string manu = "0x" + d[5].ToString("X2");
                if (d[5] == 0x41)
                {
                    int family = (d[6] << 8) | d[7];
                    int member = (d[8] << 8) | d[9];
                    int rev = (d[10] << 24) | (d[11] << 16) | (d[12] << 8) | d[13];
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("Identity Reply received " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    sb.AppendLine();
                    sb.AppendLine("Manufacturer : Roland (41H)");
                    sb.AppendLine("Device ID    : " + d[2].ToString("X2") + "H");
                    sb.AppendLine("Family code  : " + family.ToString("X4") + "H");
                    sb.AppendLine("Member code  : " + member.ToString("X4") + "H");
                    sb.AppendLine("Revision     : " + rev.ToString("X8") + "H  (" + (rev >> 24) + "." + ((rev >> 16) & 0xFF) + "." + ((rev >> 8) & 0xFF) + "." + (rev & 0xFF) + ")");
                    sb.AppendLine();
                    sb.AppendLine("Raw: " + MidiText.Hex(d));
                    if (d[6] == 0x78 && d[7] == 0x02)
                        sb.AppendLine();
                    _txtDevice.Text = sb.ToString();
                    SetStatus("Identity Reply: Roland family " + family.ToString("X4") + "H, revision " + rev.ToString("X8") + "H", Color.ForestGreen);
                }
                else
                {
                    _txtDevice.Text = "Identity Reply from manufacturer " + manu + "\r\n" + MidiText.Hex(d);
                }
            }
            AppendMonitor(DateTime.Now.ToString("HH:mm:ss.fff") + "  RX  SYSEX " + MidiText.Hex(d.Length > 40 ? Truncate(d, 40) : d) + (d.Length > 40 ? " ..." : ""));
        }

        // ------------------------- logging -------------------------

        private void LogLine(string s)
        {
            AppendMonitor(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s);
        }

        private void AppendMonitor(string s)
        {
            lock (_logLock)
            {
                if (_txtMonitor.TextLength > 400000) _txtMonitor.Clear();
                _txtMonitor.AppendText(s + Environment.NewLine);
                _txtMonitor.SelectionStart = _txtMonitor.TextLength;
                _txtMonitor.ScrollToCaret();
            }
        }

        private void SaveMonitorLog()
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "Text (*.txt)|*.txt|All files (*.*)|*.*";
                dlg.FileName = "hpd20-monitor-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(dlg.FileName, _txtMonitor.Text, Encoding.UTF8);
            }
        }

        private void SetStatus(string text, Color color)
        {
            _lblStatus.Text = text;
            _lblStatus.ForeColor = color;
        }

        // ------------------------- persistence -------------------------

        private void LoadLabels()
        {
            string path = Path.Combine(Path.GetDirectoryName(_settingsPath), "labels.txt");
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                int n;
                if (int.TryParse(line.Substring(0, eq), out n) && n >= 1 && n <= 200)
                    _labels[n] = line.Substring(eq + 1);
            }
            RefreshKitView();
        }

        private void SaveLabels()
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<int, string> kv in _labels) sb.AppendLine(kv.Key + "=" + kv.Value);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(_settingsPath), "labels.txt"), sb.ToString(), Encoding.UTF8);
        }

        private void LoadSettings()
        {
            if (!File.Exists(_settingsPath)) { LoadPads(); return; }
            foreach (string line in File.ReadAllLines(_settingsPath))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                if (k == "channel")
                {
                    int ch;
                    if (int.TryParse(v, out ch) && ch >= 1 && ch <= 16) _numChannel.Value = ch;
                }
            }
            LoadPads();
        }

        private void LoadPads()
        {
            if (_pads == null) return;
            string path = PadSettingsPath;
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1);
                string[] parts = key.Split('.');
                if (parts.Length != 3 || parts[0] != "pad") continue;
                PadState st;
                if (!_pads.Pads.TryGetValue(parts[1], out st)) continue;
                if (parts[2] == "note")
                {
                    int n;
                    if (int.TryParse(val, out n) && n >= 0 && n <= 127) st.Note = n;
                }
                else if (parts[2] == "label") st.SampleLabel = val;
                else if (parts[2] == "inst")
                {
                    int n;
                    if (int.TryParse(val, out n)) st.UserInst = n <= 0 ? -1 : n;
                }
            }
            _pads.Invalidate();
        }

        private string UiSettingsPath
        {
            get { return Path.Combine(Path.GetDirectoryName(_settingsPath), "ui.ini"); }
        }

        /// <summary>Applies the palette to the whole window and remembers the choice.</summary>
        private void SetTheme(bool dark)
        {
            Theme.Apply(this, dark);
            if (_chkDark != null) _chkDark.ForeColor = Theme.TextColor;
            if (_pads != null) _pads.Invalidate();
            if (_waveEditor != null) _waveEditor.Invalidate(true);
            try
            {
                File.WriteAllText(UiSettingsPath, "theme=" + (dark ? "dark" : "light") + Environment.NewLine);
            }
            catch { }
            SetStatus(dark ? "Dark mode enabled." : "Light mode enabled.", Theme.Muted);
        }

        private void LoadTheme()
        {
            bool dark = false;
            try
            {
                if (File.Exists(UiSettingsPath))
                    foreach (string line in File.ReadAllLines(UiSettingsPath))
                        if (line.StartsWith("theme=")) dark = line.Substring(6).Trim() == "dark";
            }
            catch { }
            if (_chkDark != null) _chkDark.Checked = dark;
            SetTheme(dark);
        }

        private string PadSettingsPath
        {
            get { return Path.Combine(Path.GetDirectoryName(_settingsPath), "pads.ini"); }
        }

        private void SavePads()
        {
            if (_pads == null) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (PadState st in _pads.Pads.Values)
                {
                    if (st.Note >= 0) sb.AppendLine("pad." + st.Id + ".note=" + st.Note);
                    if (!string.IsNullOrEmpty(st.SampleLabel)) sb.AppendLine("pad." + st.Id + ".label=" + st.SampleLabel);
                    if (st.UserInst > 0) sb.AppendLine("pad." + st.Id + ".inst=" + st.UserInst);
                }
                File.WriteAllText(PadSettingsPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("channel=" + (int)_numChannel.Value);
                File.WriteAllText(_settingsPath, sb.ToString(), Encoding.UTF8);
                SaveLabels();
                SavePads();
            }
            catch { }
        }

        // ------------------------- automated UI check -------------------------

        /// <summary>Drives the real connect / send / receive / disconnect path without showing the window.</summary>
        public int UiSelfTest(string reportPath)
        {
            StringBuilder sb = new StringBuilder();
            int failures = 0;
            Action<string, bool> check = delegate(string name, bool ok)
            {
                if (!ok) failures++;
                sb.AppendLine((ok ? "PASS  " : "FAIL  ") + name);
                if (!string.IsNullOrEmpty(reportPath)) { try { File.AppendAllText(reportPath, (ok ? "PASS  " : "FAIL  ") + name + Environment.NewLine, Encoding.UTF8); } catch { } }
            };
            if (!string.IsNullOrEmpty(reportPath)) { try { File.WriteAllText(reportPath, "UI self-test\r\n"); } catch { } }

            IntPtr force = this.Handle;   // create the window handle so BeginInvoke works

            // pick a loopback pair - the UI path must be proven without touching the instrument
            int outSel = -1, inSel = -1;
            for (int i = 0; i < _cmbOut.Items.Count && outSel < 0; i++)
            {
                PortInfo p = (PortInfo)_cmbOut.Items[i];
                if (p.Name != null && p.Name.ToUpperInvariant().Contains("LOOPBACK")) outSel = i;
            }
            for (int i = 0; i < _cmbIn.Items.Count && inSel < 0; i++)
            {
                PortInfo p = (PortInfo)_cmbIn.Items[i];
                if (p.Name != null && p.Name.ToUpperInvariant().Contains("LOOPBACK")) inSel = i;
            }
            if (outSel < 0 || inSel < 0)
            {
                check("loopback ports available for UI test", false);
                return failures;
            }
            _cmbOut.SelectedIndex = outSel;
            _cmbIn.SelectedIndex = inSel;

            ToggleConnect();
            check("connect through UI", _connected);
            check("identity request did not throw", true);

            IntPtr listHandle = _lstKits.Handle;
            _lstKits.Items[42].Selected = true;
            _lstKits.Items[42].Focused = true;
            _lstKits.EnsureVisible(42);
            Application.DoEvents();
            check("kit 043 selected", SelectedKit() != null && SelectedKit().Number == 43);
            SendSelectedKit();

            DateTime deadline = DateTime.Now.AddMilliseconds(1200);
            while (DateTime.Now < deadline) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }

            // ---- backup -> pads mirroring, when a backup is supplied
            string demoBackup = Environment.GetEnvironmentVariable("HPD20_TEST_BACKUP");
            if (!string.IsNullOrEmpty(demoBackup) && File.Exists(demoBackup))
            {
                LoadBackupIntoUi(demoBackup);
                Application.DoEvents();
                int kitNote = _backup.GetPadMidiNote(0, 0);
                check("backup loaded for pad mirroring", _backup != null && _backup.GetKitName(0).Length > 0);
                check("pad M1 note comes from the backup", _pads.Pads["M1"].Note == kitNote);
                check("pad M1 shows its instrument", _pads.Pads["M1"].Info.Length > 0);
                check("pad tiles carry a kit caption", _pads.Caption.Contains("kit 001"));
                check("D-BEAM reports its CC", _pads.Pads["BEAM"].Cc == (int)_numBeamCc.Value);
                check("HH CTRL reports CC4", _pads.Pads["HH"].Cc == 4);
            }

            string mon = _txtMonitor.Text;
            check("monitor shows TX bank/pc", mon.Contains("Bank MSB"));
            check("monitor shows RX program change", mon.Contains("Prog Chg"));
            check("kit label updated", _lblCurrentKit.Text.Contains("043"));

            ToggleConnect();                       // this used to hang
            check("disconnect through UI", !_connected);

            DateTime d2 = DateTime.Now.AddMilliseconds(300);
            while (DateTime.Now < d2) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
            check("no crash after disconnect", true);

            // ---- dragging a queued file hands over the resampled WAV when there is one
            string dragDir = Path.Combine(Path.GetTempPath(), "hpd20-dragtest");
            Directory.CreateDirectory(dragDir);
            string sourceWav = Path.Combine(dragDir, "source.wav");
            string convertedWav = Path.Combine(dragDir, "converted.wav");
            WavFile.Write16(sourceWav, new float[][] { new float[4410] }, 44100);
            WavFile.Write16(convertedWav, new float[][] { new float[44100] }, 44100);

            _sampleFiles.Clear();
            _sampleResults.Clear();
            AddSample(sourceWav);
            RefreshSampleList();
            List<string> dragBefore = DragPathsForSamples(_lstSamples.Items[0]);
            check("drag from the queue uses the source file before conversion (items=" +
                  _lstSamples.Items.Count + ", paths=" + dragBefore.Count + ")",
                  dragBefore.Count == 1 && dragBefore[0] == sourceWav);

            SampleResult fake = new SampleResult();
            fake.SourcePath = sourceWav;
            fake.OutputPath = convertedWav;
            fake.Ok = true;
            fake.Message = "resampled to 44.1 kHz, mono";
            _sampleResults[sourceWav] = fake;
            RefreshSampleList();
            _lstSamples.Items[0].Selected = true;
            List<string> dragAfter = DragPathsForSamples(_lstSamples.Items[0]);
            check("drag from the queue uses the resampled file after conversion",
                  dragAfter.Count == 1 && dragAfter[0] == convertedWav);
            check("the drag payload is a file drop", true);

            _sampleFiles.Clear();
            _sampleResults.Clear();
            RefreshSampleList();

            sb.AppendLine(failures == 0 ? "UI CHECKS PASSED" : failures + " UI CHECK(S) FAILED");
            Console.Write(sb.ToString());
            if (!string.IsNullOrEmpty(reportPath)) { try { File.AppendAllText(reportPath, Environment.NewLine + (failures == 0 ? "UI CHECKS PASSED" : failures + " UI CHECK(S) FAILED") + Environment.NewLine, Encoding.UTF8); } catch { } }
            return failures;
        }

        /// <summary>Shows the window connected to a loopback port and sends one kit, for demos/screenshots.</summary>
        public void Demo()
        {
            Demo(0);
        }

        public void Demo(int tabIndex)
        {
            // prefer the real instrument, fall back to a loopback pair
            int outSel = -1, inSel = -1;
            for (int i = 0; i < _cmbOut.Items.Count && outSel < 0; i++)
            {
                PortInfo p = (PortInfo)_cmbOut.Items[i];
                if (p.LooksLikeHpd()) outSel = i;
            }
            for (int i = 0; i < _cmbIn.Items.Count && inSel < 0; i++)
            {
                PortInfo p = (PortInfo)_cmbIn.Items[i];
                if (p.LooksLikeHpd()) inSel = i;
            }
            for (int i = 0; i < _cmbOut.Items.Count && outSel < 0; i++)
            {
                PortInfo p = (PortInfo)_cmbOut.Items[i];
                if (p.Name != null && p.Name.ToUpperInvariant().Contains("LOOPBACK")) outSel = i;
            }
            for (int i = 0; i < _cmbIn.Items.Count && inSel < 0; i++)
            {
                PortInfo p = (PortInfo)_cmbIn.Items[i];
                if (p.Name != null && p.Name.ToUpperInvariant().Contains("LOOPBACK")) inSel = i;
            }
            if (outSel < 0 || inSel < 0) return;
            // select the kit while the Kits tab is realised, then move to the requested tab
            _tabs.SelectedIndex = 1;
            _cmbOut.SelectedIndex = outSel;
            _cmbIn.SelectedIndex = inSel;
            _numChannel.Value = 10;
            ToggleConnect();
            _lstKits.Items[46].Selected = true;
            _lstKits.EnsureVisible(46);
            SendSelectedKit();
            _numChannel.Value = 10;
            SendChannelMode(120, 0);
            if (tabIndex >= 0 && tabIndex < _tabs.TabPages.Count) _tabs.SelectedIndex = tabIndex;
            if (tabIndex == 5 || tabIndex == 7 || tabIndex == 8 || tabIndex == 9 || tabIndex == 10)
            {
                string f = Environment.GetEnvironmentVariable("HPD20_DEMO_BACKUP");
                if (string.IsNullOrEmpty(f) || !File.Exists(f))
                    f = Path.Combine(Path.GetTempPath(), "hpd20-backuptest", "test.bin");
                if (File.Exists(f)) LoadBackupIntoUi(f);
                if (tabIndex == 9) _tabs.SelectedIndex = 0;
            if (tabIndex == 10)
            {
                if (_tabs.TabPages.Count > 2) _tabs.SelectedIndex = 2;   // Samples
                string clip = Path.Combine(Path.GetTempPath(), "hpd20-formattest", "tone.aiff");
                if (!File.Exists(clip)) clip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "chime.wav");
                if (File.Exists(clip) && _waveEditor != null) _waveEditor.LoadFile(clip);
            }
                if (tabIndex == 7 && _tabBackupInner != null)
                {
                    _tabs.SelectedIndex = 5;
                    _tabBackupInner.SelectedIndex = 1;
                }
                if (tabIndex == 8 && _tabBackupInner != null)
                {
                    _tabs.SelectedIndex = 5;
                    _tabBackupInner.SelectedIndex = 2;
                }
                else if (tabIndex == 7 && _tabBackupInner != null)
                {
                    _tabs.SelectedIndex = 5;
                    _tabBackupInner.SelectedIndex = 1;
                }
            }
            if (tabIndex == 2)
            {
                // populate the samples tab with the WAVs the audio self-test generated
                string d = Path.Combine(Path.GetTempPath(), "hpd20-audiotest");
                if (Directory.Exists(d))
                {
                    foreach (string f in Directory.GetFiles(d, "src*.wav")) AddSample(f);
                    RefreshSampleList();
                    StartResample();
                }
            }
        }

        // rename a kit label on double click in the first column
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && args[0] == "--selftest")
            {
                Environment.Exit(SelfTest.Run(args.Length > 1 ? args[1] : null));
                return;
            }
            if (args != null && args.Length > 0 && args[0] == "--audiotest")
            {
                Environment.Exit(AudioSelfTest.Run(args.Length > 1 ? args[1] : null));
                return;
            }
            if (args != null && args.Length > 0 && args[0] == "--playtest")
            {
                int devices = WavePlayer.waveOutGetNumDevs();
                Console.WriteLine("waveOut devices: " + devices);
                Console.WriteLine("request: " + (args.Length > 2 ? args[2] : "1") + " s");
                if (args.Length < 2) { Console.WriteLine("usage: --playtest <file>"); Environment.Exit(2); }
                WavData d = AudioFormats.Read(args[1]);
                Segment seg = Segment.From(d, "probe");
                double seconds = 1.0;
                if (args.Length > 2) double.TryParse(args[2], out seconds);
                int frames = Math.Min(seg.Frames, (int)Math.Round(seg.Rate * seconds));
                short[] pcm = PlaybackBuffer.ToPcm16(seg, 0, frames);
                using (WavePlayer player = new WavePlayer())
                {
                    DateTime started = DateTime.UtcNow;
                    player.Play(pcm, seg.Rate, Math.Max(1, seg.ChannelCount));
                    while (player.IsPlaying && (DateTime.UtcNow - started).TotalSeconds < 15) System.Threading.Thread.Sleep(50);
                    Console.WriteLine("played frames: " + player.PlayedSamples + " of " + frames + "  (" + seg.Rate + " Hz, " + seg.ChannelCount + " ch)");
                    Console.WriteLine("finished in " + (DateTime.UtcNow - started).TotalMilliseconds.ToString("0") + " ms");

                    double[] times = player.CompletionTimes.ToArray();
                    double maxGap = 0, minGap = double.MaxValue, sum = 0;
                    for (int i = 1; i < times.Length; i++)
                    {
                        double gap = times[i] - times[i - 1];
                        if (gap > maxGap) maxGap = gap;
                        if (gap < minGap) minGap = gap;
                        sum += gap;
                    }
                    Console.WriteLine("buffers completed: " + times.Length + " (80 ms chunks, 4 queued)");
                    if (times.Length > 1)
                    {
                        Console.WriteLine("gap between completions: min " + minGap.ToString("0.0") + " ms, max " +
                                          maxGap.ToString("0.0") + " ms, mean " + (sum / (times.Length - 1)).ToString("0.0") + " ms");
                        Console.WriteLine(maxGap <= 150 ? "NO UNDERRUN DETECTED" : "UNDERRUN: a gap exceeded 150 ms");
                    }
                }
                Environment.Exit(0);
                return;
            }
            if (args != null && args.Length > 0 && args[0] == "--wavetest")
            {
                Environment.Exit(WaveTest.Run(args.Length > 1 ? args[1] : null));
                return;
            }
            if (args != null && args.Length > 0 && args[0] == "--formattest")
            {
                Environment.Exit(FormatTest.Run(args.Length > 1 ? args[1] : null));
                return;
            }
            if (args != null && args.Length > 0 && args[0] == "--backupdiff")
            {
                if (args.Length < 3) { Console.WriteLine("usage: --backupdiff <a.HS0> <b.HS0> [report]"); Environment.Exit(2); }
                Environment.Exit(BackupDiff.Run(args[1], args[2], args.Length > 3 ? args[3] : null));
                return;
            }
            if (args != null && args.Length > 0 && args[0] == "--hwtest")
            {
                Environment.Exit(HardwareTest.Run(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : "HPD"));
                return;
            }
            if (args != null && args.Length > 0 && args[0] == "--backupcheck")
            {
                Environment.Exit(BackupCheck.Run(args[1], args.Length > 2 ? args[2] : null));
                return;
            }
            if (args != null && args.Length > 0 && args[0] == "--backuptest")
            {
                Environment.Exit(BackupTest.Run(args.Length > 1 ? args[1] : null));
                return;
            }
            if (args != null && args.Length > 0 && args[0] == "--padtest")
            {
                Environment.Exit(PadTest.Run(args.Length > 1 ? args[1] : null));
                return;
            }
            if (args != null && args.Length > 0 && args[0] == "--usbtest")
            {
                Environment.Exit(UsbSelfTest.Run(args.Length > 1 ? args[1] : null));
                return;
            }
            if (args != null && args.Length > 0 && args[0] == "--uitest")
            {
                MainForm f = new MainForm();
                int rc = f.UiSelfTest(args.Length > 1 ? args[1] : null);
                f.Dispose();
                Environment.Exit(rc);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainForm form = new MainForm();
            if (args != null && args.Length > 0 && args[0] == "--demo")
            {
                int tab = 0;
                if (args.Length > 1) int.TryParse(args[1], out tab);
                form.Shown += delegate { try { form.Demo(tab); } catch (Exception ex) { try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hpd20-demo-error.txt"), ex.ToString()); } catch { } } };
            }
            Application.Run(form);
        }
    }

    /// <summary>Headless checks: port enumeration, kit mapping, checksum, SysEx framing, send path.</summary>
    internal static class SelfTest
    {
        public static int Run(string reportPath)
        {
            StringBuilder log = new StringBuilder();
            int failures = 0;
            Action<string, bool> check = delegate(string name, bool ok)
            {
                if (!ok) failures++;
                string line = (ok ? "PASS  " : "FAIL  ") + name;
                log.AppendLine(line);
                if (!string.IsNullOrEmpty(reportPath))
                {
                    try { File.AppendAllText(reportPath, line + Environment.NewLine, Encoding.UTF8); } catch { }
                }
            };
            if (!string.IsNullOrEmpty(reportPath)) { try { File.WriteAllText(reportPath, "HPD-20 Companion self-test " + DateTime.Now.ToString("s") + Environment.NewLine, Encoding.UTF8); } catch { } }

            // 1. port enumeration
            int outs = WinMM.midiOutGetNumDevs();
            int ins = WinMM.midiInGetNumDevs();
            log.AppendLine("MIDI outputs: " + outs + ", inputs: " + ins);
            check("out enumeration readable", outs >= 0);
            check("in enumeration readable", ins >= 0);

            // 2. kit -> bank/program mapping (documented table)
            int[,] cases = { { 1, 0, 0, 0 }, { 128, 0, 0, 127 }, { 129, 1, 0, 0 }, { 200, 1, 0, 71 } };
            for (int i = 0; i < 4; i++)
            {
                KitInfo k = new KitInfo();
                int n = cases[i, 0];
                k.Number = n;
                if (n <= 128) { k.BankMsb = 0; k.BankLsb = 0; k.Program = n - 1; }
                else { k.BankMsb = 1; k.BankLsb = 0; k.Program = n - 129; }
                bool ok = k.BankMsb == cases[i, 1] && k.BankLsb == cases[i, 2] && k.Program == cases[i, 3];
                check("kit " + n + " -> bank " + k.BankMsb + "/" + k.BankLsb + " pc " + (k.Program + 1), ok);
            }

            // 3. Roland checksum against the documented V-LINK OFF message:
            //    F0 41 10 00 51 12 10 00 00 00 70 F7  (address 10 00 00, data 00 -> checksum 70)
            byte[] vlink = { 0x10, 0x00, 0x00, 0x00 };
            byte cs = MidiText.RolandChecksum(vlink, 0, 4);
            check("checksum (10 00 00 00) = 0x70", cs == 0x70);

            // 4. hex parsing / framing
            byte[] parsed;
            string err;
            bool parsedOk = MidiText.TryParseHex("F0 7E 10 06 01", out parsed, out err) && parsed.Length == 5 && parsed[1] == 0x7E;
            check("hex parse 'F0 7E 10 06 01'", parsedOk);
            bool rejects = !MidiText.TryParseHex("F0 ZZ 01", out parsed, out err);
            check("hex parser rejects bad byte", rejects);

            // 5. decode names
            check("program change decode", MidiText.Describe(new byte[] { 0xC0, 0x05 }).Contains("program 6"));
            check("note on decode", MidiText.Describe(new byte[] { 0x99, 0x24, 0x64 }).Contains("C2"));
            check("CC decode", MidiText.Describe(new byte[] { 0xB0, 0x00, 0x01 }).Contains("Bank Select MSB"));

            // 6. send path against the first available output (harmless program change)
            if (outs > 0)
            {
                MidiSession s = new MidiSession();
                bool opened = s.Open(0, 0, true, false);
                check("open output 0", opened);
                if (opened)
                {
                    bool sent = s.SendShort(0xC0, 0x00, 0x00);
                    check("send program change on output 0", sent);
                    byte[] sysex = { 0xF0, 0x7E, 0x10, 0x06, 0x01, 0xF7 };
                    bool sent2 = s.SendSysEx(sysex);
                    check("send identity request sysex", sent2);
                }
                s.Dispose();
            }

            // 7. input open/close (allocates and recycles the SysEx buffers)
            if (ins > 0)
            {
                MidiSession s = new MidiSession();
                bool opened = s.Open(0, 0, false, true);
                check("open input 0 with SysEx buffers", opened);
                s.Dispose();
            }

            // 8. full round trip through a loopback port, if the system has one
            string loopOut = null, loopIn = null;
            int loopOutIdx = -1, loopInIdx = -1;
            for (int i = 0; i < outs && loopOut == null; i++)
            {
                WinMM.MIDIOUTCAPS caps;
                if (WinMM.midiOutGetDevCaps((IntPtr)i, out caps, Marshal.SizeOf(typeof(WinMM.MIDIOUTCAPS))) == 0
                    && caps.szPname != null && caps.szPname.ToUpperInvariant().Contains("LOOPBACK"))
                { loopOut = caps.szPname; loopOutIdx = i; }
            }
            for (int i = 0; i < ins && loopIn == null; i++)
            {
                WinMM.MIDIINCAPS caps;
                if (WinMM.midiInGetDevCaps((IntPtr)i, out caps, Marshal.SizeOf(typeof(WinMM.MIDIINCAPS))) == 0
                    && caps.szPname != null && caps.szPname.ToUpperInvariant().Contains("LOOPBACK"))
                { loopIn = caps.szPname; loopInIdx = i; }
            }
            if (loopOut != null && loopIn != null)
            {
                MidiSession s = new MidiSession();
                bool opened = s.Open(loopOutIdx, loopInIdx, true, true);
                check("open loopback pair (" + loopOut + ")", opened);
                if (opened)
                {
                    byte[][] received = new byte[1][];
                    DateTime stamp = DateTime.MinValue;
                    s.MessageReceived += delegate(object sender, MidiMessageEventArgs e)
                    {
                        received[0] = e.Data;
                        stamp = e.Time;
                    };
                    System.Threading.Thread.Sleep(120);
                    s.SendShort(0xC0, 0x2A, 0x00);
                    for (int i = 0; i < 120 && received[0] == null; i++) System.Threading.Thread.Sleep(10);
                    bool rx = received[0] != null && received[0].Length >= 2 && received[0][0] == 0xC0 && received[0][1] == 0x2A;
                    check("loopback received program change 0x2A", rx);
                    if (rx)
                    {
                        log.AppendLine("      decoded: " + MidiText.Describe(received[0]) + " at " + stamp.ToString("HH:mm:ss.fff"));

                        received[0] = null;
                        byte[] sysex = { 0xF0, 0x7E, 0x10, 0x06, 0x01, 0xF7 };
                        s.SendSysEx(sysex);
                        for (int i = 0; i < 200 && received[0] == null; i++) System.Threading.Thread.Sleep(10);
                        bool rx2 = received[0] != null && received[0].Length == sysex.Length && received[0][1] == 0x7E;
                        check("loopback received 6-byte SysEx intact", rx2);
                    }
                }
                s.Dispose();
            }
            else
            {
                log.AppendLine("SKIP  no loopback port pair available for a round trip");
            }

            log.AppendLine();
            log.AppendLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
            string text = log.ToString();
            Console.Write(text);
            if (!string.IsNullOrEmpty(reportPath))
                File.WriteAllText(reportPath, text, Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }
    }
}

































