using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Hpd20Ctrl
{
    public sealed partial class MainForm
    {
        // backup editor state
        private HpdBackup _backup;
        private ListBox _lstBackupKits;
        private ListBox _lstBackupPads;
        private Label _lblBackupStatus, _lblBackupFile, _lblPadHex;
        private TextBox _txtKitName, _txtKitSubName;
        private NumericUpDown _numKitVolume, _numKitHhVolume, _numKitSensitivity, _numKitBalance;
        private RadioButton _radLayerA, _radLayerB;
        private ComboBox _cmbInstrument;
        private NumericUpDown _numPitch, _numPadVolume, _numPan, _numAmbient, _numMuffling, _numSweep;
        private NumericUpDown _numMidiNote, _numMidiGate, _numVelocityFade, _numFixedVelocity;
        private NumericUpDown _numMfxAssign, _numMuteGroup, _numMonoPoly, _numLayerMode;
        private ComboBox _cmbTriggerMode;
        private Button _btnBackupOpen, _btnBackupSave, _btnBackupUsb, _btnBackupRevert;
        private bool _loadingBackupUi;
        private TabControl _tabBackupInner;

        private string BackupFileName
        {
            get
            {
                if (_backup == null || _backup.SourcePath == null) return "backup.bin";
                return Path.GetFileName(_backup.SourcePath);
            }
        }

        private TabPage BuildBackupTab()
        {
            TabPage page = new TabPage("Backup Editor");

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 62;
            top.Padding = new Padding(6);

            _btnBackupOpen = new Button();
            _btnBackupOpen.Text = "Open backup...";
            _btnBackupOpen.SetBounds(6, 6, 120, 26);
            _btnBackupOpen.Click += delegate { OpenBackup(); };

            _btnBackupSave = new Button();
            _btnBackupSave.Text = "Save as...";
            _btnBackupSave.SetBounds(130, 6, 90, 26);
            _btnBackupSave.Enabled = false;
            _btnBackupSave.Click += delegate { SaveBackupAs(); };

            _btnBackupUsb = new Button();
            _btnBackupUsb.Text = "Write to USB stick...";
            _btnBackupUsb.SetBounds(224, 6, 150, 26);
            _btnBackupUsb.Enabled = false;
            _btnBackupUsb.Click += delegate { WriteBackupToUsb(); };

            _btnBackupRevert = new Button();
            _btnBackupRevert.Text = "Revert";
            _btnBackupRevert.SetBounds(378, 6, 70, 26);
            _btnBackupRevert.Enabled = false;
            _btnBackupRevert.Click += delegate { if (_backup != null && _backup.SourcePath != null) LoadBackupIntoUi(_backup.SourcePath); };

            _lblBackupFile = new Label();
            _lblBackupFile.SetBounds(456, 10, 470, 20);
            _lblBackupFile.Text = "No backup open.";

            _lblBackupStatus = new Label();
            _lblBackupStatus.SetBounds(6, 36, 920, 20);
            _lblBackupStatus.ForeColor = Color.DimGray;
            _lblBackupStatus.Text = "Open a backup from your USB stick (Roland/HPD-20/Backup), edit it, then write it back.";

            top.Controls.AddRange(new Control[] { _btnBackupOpen, _btnBackupSave, _btnBackupUsb, _btnBackupRevert, _lblBackupFile, _lblBackupStatus });

            Panel main = new Panel();
            main.Dock = DockStyle.Fill;

            _lstBackupKits = new ListBox();
            _lstBackupKits.Dock = DockStyle.Left;
            _lstBackupKits.Width = 200;
            _lstBackupKits.IntegralHeight = false;
            _lstBackupKits.SelectedIndexChanged += delegate { OnBackupKitChanged(); };

            Label kitCap = new Label();
            kitCap.Dock = DockStyle.Top;
            kitCap.Height = 18;
            kitCap.Text = "Kits";
            kitCap.ForeColor = Color.DimGray;

            Panel kitSide = new Panel();
            kitSide.Dock = DockStyle.Left;
            kitSide.Width = 204;
            kitSide.Controls.Add(_lstBackupKits);
            kitSide.Controls.Add(kitCap);

            Panel kitEditor = new Panel();
            kitEditor.Dock = DockStyle.Top;
            kitEditor.Height = 152;
            kitEditor.Padding = new Padding(6);

            Label kitTitle = new Label();
            kitTitle.Text = "Kit settings";
            kitTitle.Font = new Font("Segoe UI Semibold", 9.5f);
            kitTitle.SetBounds(6, 4, 200, 20);

            AddLabelled(kitEditor, "Name (12 chars)", 6, 28);
            _txtKitName = new TextBox();
            _txtKitName.SetBounds(130, 26, 130, 22);
            _txtKitName.MaxLength = 12;
            _txtKitName.TextChanged += delegate { if (!_loadingBackupUi) ApplyKitFields(); };

            AddLabelled(kitEditor, "Sub name (16)", 6, 54);
            _txtKitSubName = new TextBox();
            _txtKitSubName.SetBounds(130, 52, 160, 22);
            _txtKitSubName.MaxLength = 16;
            _txtKitSubName.TextChanged += delegate { if (!_loadingBackupUi) ApplyKitFields(); };

            AddLabelled(kitEditor, "Kit volume", 6, 80);
            _numKitVolume = MakeNum(130, 78, 0, 255);
            _numKitVolume.ValueChanged += delegate { if (!_loadingBackupUi) ApplyKitFields(); };

            AddLabelled(kitEditor, "HH volume", 6, 106);
            _numKitHhVolume = MakeNum(130, 104, 0, 255);
            _numKitHhVolume.ValueChanged += delegate { if (!_loadingBackupUi) ApplyKitFields(); };

            AddLabelled(kitEditor, "Pad sensitivity", 300, 28);
            _numKitSensitivity = MakeNum(430, 26, -128, 127);
            _numKitSensitivity.ValueChanged += delegate { if (!_loadingBackupUi) ApplyKitFields(); };

            AddLabelled(kitEditor, "Balance", 300, 54);
            _numKitBalance = MakeNum(430, 52, -128, 127);
            _numKitBalance.ValueChanged += delegate { if (!_loadingBackupUi) ApplyKitFields(); };

            Label kitNote = new Label();
            kitNote.SetBounds(300, 82, 330, 62);
            kitNote.ForeColor = Color.DimGray;
            kitNote.Text = "Six fields come from the known layout. Everything else on\n" +
                           "this tab is a raw byte from the file; the unit's display is the\n" +
                           "authority on what each value means.";

            kitEditor.Controls.AddRange(new Control[] { kitTitle, _txtKitName, _txtKitSubName,
                _numKitVolume, _numKitHhVolume, _numKitSensitivity, _numKitBalance, kitNote });

            // ---- pad editor ----
            Panel padArea = new Panel();
            padArea.Dock = DockStyle.Fill;

            Panel padListSide = new Panel();
            padListSide.Dock = DockStyle.Left;
            padListSide.Width = 130;

            Label padCap = new Label();
            padCap.Dock = DockStyle.Top;
            padCap.Height = 18;
            padCap.Text = "Pads";
            padCap.ForeColor = Color.DimGray;

            _lstBackupPads = new ListBox();
            _lstBackupPads.Dock = DockStyle.Fill;
            _lstBackupPads.IntegralHeight = false;
            _lstBackupPads.SelectedIndexChanged += delegate { OnBackupPadChanged(); };
            padListSide.Controls.Add(_lstBackupPads);
            padListSide.Controls.Add(padCap);

            Panel padFields = new Panel();
            padFields.Dock = DockStyle.Fill;
            padFields.Padding = new Padding(8);

            Label padTitle = new Label();
            padTitle.Text = "Pad settings";
            padTitle.Font = new Font("Segoe UI Semibold", 9.5f);
            padTitle.SetBounds(6, 4, 200, 20);

            _radLayerA = new RadioButton();
            _radLayerA.Text = "Layer A";
            _radLayerA.Checked = true;
            _radLayerA.SetBounds(6, 26, 80, 22);
            _radLayerA.CheckedChanged += delegate { if (!_loadingBackupUi) OnBackupLayerChanged(); };

            _radLayerB = new RadioButton();
            _radLayerB.Text = "Layer B";
            _radLayerB.SetBounds(88, 26, 80, 22);
            _radLayerB.CheckedChanged += delegate { if (!_loadingBackupUi) OnBackupLayerChanged(); };

            AddLabelled(padFields, "Instrument", 6, 54);
            _cmbInstrument = new ComboBox();
            _cmbInstrument.SetBounds(110, 52, 480, 22);
            _cmbInstrument.DropDownStyle = ComboBoxStyle.DropDown;
            _cmbInstrument.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _cmbInstrument.AutoCompleteSource = AutoCompleteSource.ListItems;
            foreach (InstrumentInfo ins in InstrumentTable.All)
                _cmbInstrument.Items.Add(ins.Number.ToString("000") + "  " + ins.Name);
            _cmbInstrument.SelectedIndexChanged += delegate { if (!_loadingBackupUi) ApplyPadFields(); };
            _cmbInstrument.Leave += delegate { if (!_loadingBackupUi) ApplyPadFields(); };

            AddLabelled(padFields, "Pitch (cents)", 6, 82);
            _numPitch = MakeNum(110, 80, -32768, 32767);
            _numPitch.Increment = 10;
            _numPitch.ValueChanged += delegate { if (!_loadingBackupUi) ApplyPadFields(); };

            AddLabelled(padFields, "Volume", 6, 108);
            _numPadVolume = MakeNum(110, 106, 0, 255);
            _numPadVolume.ValueChanged += delegate { if (!_loadingBackupUi) ApplyPadFields(); };

            AddLabelled(padFields, "Pan", 6, 134);
            _numPan = MakeNum(110, 132, -128, 127);
            _numPan.ValueChanged += delegate { if (!_loadingBackupUi) ApplyPadFields(); };

            AddLabelled(padFields, "Ambient send", 6, 160);
            _numAmbient = MakeNum(110, 158, 0, 255);
            _numAmbient.ValueChanged += delegate { if (!_loadingBackupUi) ApplyPadFields(); };

            AddLabelled(padFields, "Muffling", 6, 186);
            _numMuffling = MakeNum(110, 184, 0, 255);
            _numMuffling.ValueChanged += delegate { if (!_loadingBackupUi) ApplyPadFields(); };

            AddLabelled(padFields, "Sweep", 6, 212);
            _numSweep = MakeNum(110, 210, -128, 127);
            _numSweep.ValueChanged += delegate { if (!_loadingBackupUi) ApplyPadFields(); };

            AddLabelled(padFields, "MFX assign", 310, 82);
            _numMfxAssign = MakeNum(414, 80, 0, 255);
            _numMfxAssign.ValueChanged += delegate { if (!_loadingBackupUi) ApplyPadFields(); };

            AddLabelled(padFields, "MIDI note", 310, 108);
            _numMidiNote = MakeNum(414, 106, 0, 255);
            _numMidiNote.ValueChanged += delegate { if (!_loadingBackupUi) ApplyPadFields(); };

            AddLabelled(padFields, "MIDI gate", 310, 134);
            _numMidiGate = MakeNum(414, 132, 0, 255);
            _numMidiGate.ValueChanged += delegate { if (!_loadingBackupUi) ApplyPadFields(); };

            AddLabelled(padFields, "Velocity fade", 310, 160);
            _numVelocityFade = MakeNum(414, 158, 0, 255);
            _numVelocityFade.ValueChanged += delegate { if (!_loadingBackupUi) ApplyPadFields(); };

            AddLabelled(padFields, "Fixed velocity", 310, 186);
            _numFixedVelocity = MakeNum(414, 184, 0, 255);
            _numFixedVelocity.ValueChanged += delegate { if (!_loadingBackupUi) ApplyPadFields(); };

            AddLabelled(padFields, "Trigger mode", 310, 212);
            _cmbTriggerMode = new ComboBox();
            _cmbTriggerMode.SetBounds(414, 210, 130, 22);
            _cmbTriggerMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbTriggerMode.Items.AddRange(new object[] { "0  SHOT", "1  GATE", "2  ALT", "3  ?" });
            _cmbTriggerMode.SelectedIndexChanged += delegate { if (!_loadingBackupUi) ApplyPadFields(); };

            AddLabelled(padFields, "Mute group", 310, 238);
            _numMuteGroup = MakeNum(414, 236, 0, 255);
            _numMuteGroup.ValueChanged += delegate { if (!_loadingBackupUi) ApplyPadFields(); };

            AddLabelled(padFields, "Mono/Poly", 310, 264);
            _numMonoPoly = MakeNum(414, 262, 0, 255);
            _numMonoPoly.ValueChanged += delegate { if (!_loadingBackupUi) ApplyPadFields(); };

            AddLabelled(padFields, "Layer mode", 6, 264);
            _numLayerMode = MakeNum(110, 262, 0, 255);
            _numLayerMode.ValueChanged += delegate { if (!_loadingBackupUi) ApplyPadFields(); };

            _lblPadHex = new Label();
            _lblPadHex.SetBounds(6, 292, 600, 78);
            _lblPadHex.Font = new Font("Consolas", 7.5f);
            _lblPadHex.ForeColor = Color.DimGray;



            padFields.Controls.AddRange(new Control[] { padTitle, _radLayerA, _radLayerB, _cmbInstrument,
                _numPitch, _numPadVolume, _numPan, _numAmbient, _numMuffling, _numSweep,
                _numMfxAssign, _numMidiNote, _numMidiGate, _numVelocityFade, _numFixedVelocity,
                _cmbTriggerMode, _numMuteGroup, _numMonoPoly, _numLayerMode, _lblPadHex });

            padArea.Controls.Add(padFields);
            padArea.Controls.Add(padListSide);

            main.Controls.Add(padArea);
            main.Controls.Add(kitEditor);
            main.Controls.Add(kitSide);

            TabControl inner = new TabControl();
            _tabBackupInner = inner;
            inner.Dock = DockStyle.Fill;
            TabPage pageFields = new TabPage("Kits & pads");
            pageFields.Controls.Add(main);
            inner.TabPages.Add(pageFields);
            inner.TabPages.Add(BuildChainsPage());
            inner.TabPages.Add(BuildRawPage());

            page.Controls.Add(inner);
            page.Controls.Add(top);
            return page;
        }

        // ------------------------------ header and chains ------------------------------

        private Label _lblHeaderInfo;
        private ListBox _lstChains;
        private TextBox _txtChainName;

        private TabPage BuildChainsPage()
        {
            TabPage page = new TabPage("Chains & header");

            _lblHeaderInfo = new Label();
            _lblHeaderInfo.Dock = DockStyle.Top;
            _lblHeaderInfo.Height = 96;
            _lblHeaderInfo.Padding = new Padding(8, 8, 8, 8);
            _lblHeaderInfo.Font = new Font("Consolas", 9f);
            _lblHeaderInfo.Text = "";

            Panel chainArea = new Panel();
            chainArea.Dock = DockStyle.Fill;
            chainArea.Padding = new Padding(8);

            Label cap = new Label();
            cap.Text = "Chains (15)";
            cap.SetBounds(8, 6, 100, 20);
            cap.ForeColor = Color.DimGray;

            _lstChains = new ListBox();
            _lstChains.SetBounds(8, 28, 260, 260);
            _lstChains.IntegralHeight = false;
            _lstChains.SelectedIndexChanged += delegate
            {
                if (_backup == null || _lstChains.SelectedIndex < 0) return;
                _txtChainName.Text = _backup.GetChainName(_lstChains.SelectedIndex);
            };

            Label lName = new Label();
            lName.Text = "Name (10 chars)";
            lName.SetBounds(286, 30, 110, 20);

            _txtChainName = new TextBox();
            _txtChainName.SetBounds(286, 52, 200, 22);
            _txtChainName.MaxLength = 10;

            Button apply = new Button();
            apply.Text = "Apply name";
            apply.SetBounds(286, 82, 100, 26);
            apply.Click += delegate
            {
                if (_backup == null || _lstChains.SelectedIndex < 0) return;
                _backup.SetChainName(_lstChains.SelectedIndex, _txtChainName.Text);
                int idx = _lstChains.SelectedIndex;
                _lstChains.Items[idx] = _txtChainName.Text.PadRight(10);
                SetStatus("Renamed chain " + (idx + 1) + " (not saved yet).", Color.DarkOrange);
            };

            Label note = new Label();
            note.SetBounds(286, 118, 420, 150);
            note.ForeColor = Color.DimGray;
            note.Text = "Each chain block is 128 bytes: the 10-byte name, then bytes that\n" +
                        "reference the kits belonging to that chain. The kit list inside a\n" +
                        "chain is not decoded, so edit those bytes on the Expert page if\n" +
                        "you need to change which kits a chain holds.\n\n" +
                        "Chain names and the header are read back from the file itself\n" +
                        "(header: name, firmware version, MD5).";

            chainArea.Controls.AddRange(new Control[] { cap, _lstChains, lName, _txtChainName, apply, note });

            page.Controls.Add(chainArea);
            page.Controls.Add(_lblHeaderInfo);
            return page;
        }

        private void RefreshHeaderAndChains()
        {
            if (_backup == null) return;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("file      " + (string.IsNullOrEmpty(_backup.SourcePath) ? "(not saved yet)" : Path.GetFileName(_backup.SourcePath)));
            sb.AppendLine("name      '" + _backup.HeaderName + "'      firmware " + _backup.FirmwareVersion);
            sb.AppendLine("size      " + _backup.MemorySize + " bytes + 16 MD5      md5 " + (_backup.Md5Valid ? "valid" : "MISMATCH"));
            sb.AppendLine("contents  " + HpdBackup.KitCount + " kits, " + HpdBackup.PadCount + " pads, " + HpdBackup.ChainCount + " chains");
            _lblHeaderInfo.Text = sb.ToString();

            _lstChains.BeginUpdate();
            _lstChains.Items.Clear();
            for (int i = 0; i < HpdBackup.ChainCount; i++)
                _lstChains.Items.Add(_backup.GetChainName(i).PadRight(10));
            _lstChains.EndUpdate();
            if (_lstChains.Items.Count > 0) _lstChains.SelectedIndex = 0;
        }

        // ------------------------------ expert raw bytes ------------------------------

        private NumericUpDown _numRawOffset, _numRawValue, _numRawPage;
        private TextBox _txtRawDump;
        private Label _lblRawRegion;

        private TabPage BuildRawPage()
        {
            TabPage page = new TabPage("Expert (raw bytes)");

            Label top = new Label();
            top.Dock = DockStyle.Top;
            top.Height = 44;
            top.ForeColor = Color.DimGray;
            top.Text = "Direct access to every byte of the backup image. Use it for the areas that are not mapped\n" +
                       "(system settings, chains, effects). Offset 0 is the first byte of the image; the MD5 trailer is not shown.";

            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 66;

            Label lOff = new Label();
            lOff.Text = "Offset";
            lOff.SetBounds(6, 8, 50, 20);
            _numRawOffset = MakeNum(58, 6, 0, 400000);
            _numRawOffset.SetBounds(58, 6, 90, 22);
            _numRawOffset.ValueChanged += delegate { ShowRawDump(); };

            Label lVal = new Label();
            lVal.Text = "Value";
            lVal.SetBounds(160, 8, 46, 20);
            _numRawValue = MakeNum(206, 6, 0, 255);
            _numRawValue.SetBounds(206, 6, 60, 22);

            Button write = new Button();
            write.Text = "Write byte";
            write.SetBounds(276, 5, 90, 24);
            write.Click += delegate { WriteRawByte(); };

            Label lPage = new Label();
            lPage.Text = "Page";
            lPage.SetBounds(380, 8, 40, 20);
            _numRawPage = MakeNum(420, 6, 0, 4000);
            _numRawPage.SetBounds(420, 6, 60, 22);
            _numRawPage.ValueChanged += delegate
            {
                if (_backup == null) return;
                _numRawOffset.Value = Math.Min(_numRawPage.Value * 256, _backup.MemorySize - 1);
            };

            Button prev = new Button();
            prev.Text = "< PgUp";
            prev.SetBounds(490, 5, 70, 24);
            prev.Click += delegate { if (_numRawPage.Value > 0) _numRawPage.Value -= 1; };

            Button next = new Button();
            next.Text = "PgDn >";
            next.SetBounds(564, 5, 70, 24);
            next.Click += delegate { _numRawPage.Value += 1; };

            _lblRawRegion = new Label();
            _lblRawRegion.SetBounds(6, 36, 900, 20);
            _lblRawRegion.ForeColor = Color.DimGray;

            bar.Controls.AddRange(new Control[] { lOff, _numRawOffset, lVal, _numRawValue, write, lPage, _numRawPage, prev, next, _lblRawRegion });

            _txtRawDump = new TextBox();
            _txtRawDump.Dock = DockStyle.Fill;
            _txtRawDump.Multiline = true;
            _txtRawDump.ReadOnly = true;
            _txtRawDump.ScrollBars = ScrollBars.Vertical;
            _txtRawDump.Font = new Font("Consolas", 9f);
            _txtRawDump.WordWrap = false;
            _txtRawDump.BackColor = Color.White;

            page.Enter += delegate { ShowRawDump(); };

            page.Controls.Add(_txtRawDump);
            page.Controls.Add(bar);
            page.Controls.Add(top);
            return page;
        }

        private void ShowRawDump()
        {
            if (_backup == null)
            {
                _txtRawDump.Text = "Open a backup first.";
                _lblRawRegion.Text = "";
                return;
            }
            int start = (int)_numRawOffset.Value;
            int pageStart = (start / 256) * 256;
            _numRawPage.Value = pageStart / 256;

            StringBuilder sb = new StringBuilder();
            for (int row = 0; row < 16; row++)
            {
                int addr = pageStart + row * 16;
                if (addr >= _backup.MemorySize) break;
                sb.Append(addr.ToString("X6")).Append("  ");
                StringBuilder ascii = new StringBuilder();
                for (int i = 0; i < 16; i++)
                {
                    int a = addr + i;
                    if (a >= _backup.MemorySize) { sb.Append("   "); continue; }
                    int v = _backup.GetByte(a);
                    if (a == start) sb.Append("[").Append(v.ToString("X2")).Append("]");
                    else sb.Append(v.ToString("X2")).Append(' ');
                    ascii.Append(v >= 32 && v < 127 ? (char)v : '.');
                }
                sb.Append(" ").Append(ascii).AppendLine();
            }
            _txtRawDump.Text = sb.ToString();
            _txtRawDump.Select(0, 0);
            _txtRawDump.ScrollToCaret();
            _numRawValue.Value = start < _backup.MemorySize ? _backup.GetByte(start) : 0;

            int kit, pad;
            string region = RegionOf(start, out kit, out pad);
            _lblRawRegion.Text = "0x" + start.ToString("X") + "  -  " + region;
        }

        private string RegionOf(int offset, out int kit, out int pad)
        {
            kit = -1; pad = -1;
            if (offset >= HpdBackup.KitIndex && offset < HpdBackup.KitIndex + HpdBackup.KitCount * HpdBackup.KitSize)
            {
                kit = (offset - HpdBackup.KitIndex) / HpdBackup.KitSize;
                return "kit " + (kit + 1) + " block, byte " + ((offset - HpdBackup.KitIndex) % HpdBackup.KitSize);
            }
            if (offset >= HpdBackup.PadIndex && offset < HpdBackup.PadIndex + HpdBackup.PadCount * HpdBackup.PadSize)
            {
                int rel = offset - HpdBackup.PadIndex;
                int padIndex = rel / HpdBackup.PadSize;
                kit = padIndex / HpdBackup.PadsPerKit;
                pad = padIndex % HpdBackup.PadsPerKit;
                return "kit " + (kit + 1) + ", pad " + HpdBackup.PadNames[pad] + ", byte " + (rel % HpdBackup.PadSize);
            }
            if (offset >= HpdBackup.ChainIndex && offset < HpdBackup.ChainIndex + HpdBackup.ChainCount * HpdBackup.ChainSize)
                return "chain block, byte " + ((offset - HpdBackup.ChainIndex) % HpdBackup.ChainSize);
            if (offset < 0x28) return "header (name / firmware)";
            if (offset < HpdBackup.ChainIndex) return "system settings (undecoded)";
            if (offset < HpdBackup.KitIndex) return "system settings, second block (undecoded)";
            return "unmapped area";
        }

        private void WriteRawByte()
        {
            if (_backup == null) return;
            int off = (int)_numRawOffset.Value;
            if (off >= _backup.MemorySize) return;
            _backup.SetByte(off, (int)_numRawValue.Value);
            _backup.Modified = true;
            ShowRawDump();
            SetStatus("Byte 0x" + off.ToString("X") + " set to " + ((int)_numRawValue.Value).ToString("X2") + " (not saved yet).", Color.DarkOrange);
        }

        private static void AddLabelled(Control parent, string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, 100, 20);
            parent.Controls.Add(l);
        }

        private static NumericUpDown MakeNum(int x, int y, int min, int max)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.SetBounds(x, y, 80, 22);
            return n;
        }

        // ------------------------------ file actions ------------------------------

        private void OpenBackup()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Open an HPD-20 backup";
                d.Filter = "HPD-20 backup (*.HS0;*.hs0)|*.HS0;*.hs0|All files (*.*)|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                LoadBackupIntoUi(d.FileName);
            }
        }

        private void LoadBackupIntoUi(string path)
        {
            string error;
            HpdBackup b = HpdBackup.Load(path, out error);
            if (b == null)
            {
                MessageBox.Show(this, "Cannot open this file:\r\n\r\n" + error, "HPD-20 backup",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _backup = b;
            _btnBackupSave.Enabled = true;
            _btnBackupUsb.Enabled = true;
            _btnBackupRevert.Enabled = true;

            _lstBackupKits.BeginUpdate();
            _lstBackupKits.Items.Clear();
            for (int i = 0; i < HpdBackup.KitCount; i++)
                _lstBackupKits.Items.Add((i + 1).ToString("000") + "  " + Safe(_backup.GetKitName(i)));
            _lstBackupKits.EndUpdate();

            _lstBackupPads.BeginUpdate();
            _lstBackupPads.Items.Clear();
            foreach (string p in HpdBackup.PadNames) _lstBackupPads.Items.Add(p);
            _lstBackupPads.EndUpdate();

            _lblBackupFile.Text = Path.GetFileName(path) + "   " + _backup.MemorySize + " bytes   MD5 " +
                                  (_backup.Md5Valid ? "OK" : "MISMATCH");

            if (!_backup.Md5Valid)
                _lblBackupStatus.Text = "Warning: the MD5 trailer does not match the data. View and re-save, but do not load this into the instrument.";
            else
                _lblBackupStatus.Text = "Loaded. " + HpdBackup.KitCount + " kits, " + HpdBackup.PadCount + " pads. Edits are only written when you save.";

            RefreshHeaderAndChains();

            if (_numRawOffset != null && _backup != null)
                _numRawOffset.Maximum = Math.Max(0, _backup.MemorySize - 1);
            if (_txtRawDump != null) ShowRawDump();

            if (_lstBackupKits.Items.Count > 0) _lstBackupKits.SelectedIndex = 0;
            if (_lstBackupPads.Items.Count > 0) _lstBackupPads.SelectedIndex = 0;
            SyncPadsFromBackup();
        }

        private static string Safe(string s) { return string.IsNullOrEmpty(s) ? "(no name)" : s; }

        private void SaveBackupAs()
        {
            if (_backup == null) return;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "Save the edited backup";
                d.Filter = "HPD-20 backup (*.HS0)|*.HS0|All files (*.*)|*.*";
                d.FileName = Path.GetFileNameWithoutExtension(BackupFileName) + "_edit" + Path.GetExtension(BackupFileName);
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    _backup.Save(d.FileName);
                    _lblBackupFile.Text = Path.GetFileName(d.FileName) + "   " + _backup.MemorySize + " bytes   MD5 OK";
                    _lblBackupStatus.Text = "Saved with a freshly computed MD5. Copy it to Roland/HPD-20/Backup on the stick.";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Save failed: " + ex.Message, "HPD-20 backup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void WriteBackupToUsb()
        {
            if (_backup == null) return;
            using (Form dlg = new Form())
            {
                dlg.Text = "Write backup to USB stick";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ClientSize = new Size(560, 190);
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;

                Label lDrive = new Label();
                lDrive.Text = "USB stick:";
                lDrive.SetBounds(12, 14, 70, 20);

                ComboBox cmb = new ComboBox();
                cmb.DropDownStyle = ComboBoxStyle.DropDownList;
                cmb.SetBounds(86, 12, 450, 22);
                FillDriveCombo(cmb);

                Label lName = new Label();
                lName.Text = "File name:";
                lName.SetBounds(12, 46, 70, 20);

                TextBox name = new TextBox();
                name.SetBounds(86, 44, 300, 22);
                name.Text = Path.GetFileName(BackupFileName);
                if (!name.Text.ToUpperInvariant().EndsWith(".HS0"))
                    name.Text = Path.GetFileNameWithoutExtension(name.Text) + ".HS0";
                bool nameEdited = false;
                name.TextChanged += delegate { nameEdited = true; };

                Label hint = new Label();
                hint.SetBounds(12, 74, 530, 62);
                hint.ForeColor = Color.DimGray;
                hint.Text = "The file goes to Roland/HPD-20/Backup on the selected stick, with a fresh MD5.\r\n" +
                            "The instrument numbers its backups BKUP-001.HS0 ... BKUP-100.HS0; unless you type\r\n" +
                            "your own name, the next free number is used so an existing backup is never overwritten.";

                Button ok = new Button();
                ok.Text = "Write";
                ok.DialogResult = DialogResult.OK;
                ok.SetBounds(356, 146, 90, 28);

                Button cancel = new Button();
                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(450, 146, 90, 28);

                dlg.Controls.AddRange(new Control[] { lDrive, cmb, lName, name, hint, ok, cancel });
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                object sel = cmb.SelectedItem;
                if (sel == null)
                {
                    MessageBox.Show(this, "No drive selected. Insert the USB stick and try again.", "Write to USB");
                    return;
                }
                string root = sel.ToString().Substring(0, 3);
                try
                {
                    string folder = Path.Combine(root, Path.Combine("Roland", Path.Combine("HPD-20", "Backup")));
                    if (!Directory.Exists(folder))
                    {
                        DialogResult r = MessageBox.Show(this,
                            "The folder " + folder + " does not exist.\r\n\r\nCreate it? (A stick formatted by the HPD-20 already has it.)",
                            "Write to USB", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (r != DialogResult.Yes) return;
                        Directory.CreateDirectory(folder);
                    }
                    string typed = name.Text.Trim();
                    if (typed.Length == 0) typed = "BKUP-001.HS0";
                    string target = Path.Combine(folder, typed);

                    // If the user kept the suggested name, never overwrite: take the next free BKUP slot.
                    if (!nameEdited)
                    {
                        int slot = NextFreeSlot(folder);
                        target = Path.Combine(folder, "BKUP-" + slot.ToString("000") + ".HS0");
                    }
                    else if (File.Exists(target))
                    {
                        if (MessageBox.Show(this, "Overwrite " + target + "?", "Write to USB",
                                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    }
                    _backup.Save(target);
                    _lblBackupStatus.Text = "Written to " + target + " (" + _backup.MemorySize + " bytes + MD5).";
                    MessageBox.Show(this, "Backup written to:\r\n" + target +
                        "\r\n\r\nOn the HPD-20: MENU > SYS > USB Memory > Load Backup.",
                        "Write to USB", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Write failed: " + ex.Message, "Write to USB", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>Finds the first unused BKUP-nnn.HS0 number in a backup folder.</summary>
        private static int NextFreeSlot(string folder)
        {
            bool[] used = new bool[101];
            try
            {
                foreach (string f in Directory.GetFiles(folder, "*.HS0"))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    if (n.Length > 5 && n.ToUpperInvariant().StartsWith("BKUP-"))
                    {
                        int v;
                        if (int.TryParse(n.Substring(5), out v) && v >= 1 && v <= 100) used[v] = true;
                    }
                }
            }
            catch { }
            for (int i = 1; i <= 100; i++) if (!used[i]) return i;
            return 100;
        }

        // ------------------------------ field binding ------------------------------

        private int CurrentKit { get { return _lstBackupKits.SelectedIndex; } }
        private int CurrentPad { get { return _lstBackupPads.SelectedIndex; } }
        private int CurrentLayer { get { return _radLayerB.Checked ? 1 : 0; } }

        private void OnBackupKitChanged()
        {
            if (_backup == null || CurrentKit < 0) return;
            _loadingBackupUi = true;
            try
            {
                _txtKitName.Text = _backup.GetKitName(CurrentKit);
                _txtKitSubName.Text = _backup.GetKitSubName(CurrentKit);
                _numKitVolume.Value = Clamp(_backup.GetKitVolume(CurrentKit), 0, 255);
                _numKitHhVolume.Value = Clamp(_backup.GetKitHhVolume(CurrentKit), 0, 255);
                _numKitSensitivity.Value = Clamp(_backup.GetKitPadSensitivity(CurrentKit), -128, 127);
                _numKitBalance.Value = Clamp(_backup.GetKitBalance(CurrentKit), -128, 127);
            }
            finally { _loadingBackupUi = false; }
            LoadPadFields();
            UpdateKitListEntry();
            SyncPadsFromBackup();
        }

        private void OnBackupPadChanged()
        {
            LoadPadFields();
        }

        private void OnBackupLayerChanged()
        {
            LoadPadFields();
        }

        private void LoadPadFields()
        {
            if (_backup == null || CurrentKit < 0 || CurrentPad < 0) return;
            _loadingBackupUi = true;
            try
            {
                int kit = CurrentKit, pad = CurrentPad, layer = CurrentLayer;
                int inst = _backup.GetPadInstrument(kit, pad, layer);
                SelectInstrument(inst);
                _numPitch.Value = Clamp(_backup.GetPadPitch(kit, pad, layer), -32768, 32767);
                _numPadVolume.Value = Clamp(_backup.GetPadVolume(kit, pad, layer), 0, 255);
                _numPan.Value = Clamp(_backup.GetPadPan(kit, pad, layer), -128, 127);
                _numAmbient.Value = Clamp(_backup.GetPadAmbient(kit, pad, layer), 0, 255);
                _numMuffling.Value = Clamp(_backup.GetPadMuffling(kit, pad, layer), 0, 255);
                _numSweep.Value = Clamp(_backup.GetPadSweep(kit, pad, layer), -128, 127);
                _numMfxAssign.Value = Clamp(_backup.GetPadMfxAssign(kit, pad, layer), 0, 255);
                _numMidiNote.Value = Clamp(_backup.GetPadMidiNote(kit, pad), 0, 255);
                _numMidiGate.Value = Clamp(_backup.GetPadMidiGate(kit, pad), 0, 255);
                _numVelocityFade.Value = Clamp(_backup.GetPadVelocityFade(kit, pad), 0, 255);
                _numFixedVelocity.Value = Clamp(_backup.GetPadFixedVelocity(kit, pad), 0, 255);
                int trig = _backup.GetPadTriggerMode(kit, pad);
                _cmbTriggerMode.SelectedIndex = trig >= 0 && trig < _cmbTriggerMode.Items.Count ? trig : -1;
                _numMuteGroup.Value = Clamp(_backup.GetPadMuteGroup(kit, pad), 0, 255);
                _numMonoPoly.Value = Clamp(_backup.GetPadMonoPoly(kit, pad), 0, 255);
                _numLayerMode.Value = Clamp(_backup.GetPadLayerMode(kit, pad), 0, 255);
                _lblPadHex.Text = HexDump(kit, pad);
            }
            finally { _loadingBackupUi = false; }
        }

        private static decimal Clamp(int v, int min, int max)
        {
            if (v < min) v = min;
            if (v > max) v = max;
            return v;
        }

        private void SelectInstrument(int number)
        {
            _cmbInstrument.SelectedIndex = -1;
            if (number <= 0) { _cmbInstrument.Text = "0  (none)"; return; }
            int idx = number - 1;   // table is ordered by instrument number
            if (idx >= 0 && idx < _cmbInstrument.Items.Count && InstrumentTable.All[idx].Number == number)
                _cmbInstrument.SelectedIndex = idx;
            else
                _cmbInstrument.Text = number + "  " + InstrumentTable.NameOf(number);
        }

        private int ReadInstrument()
        {
            if (_cmbInstrument.SelectedIndex >= 0 && _cmbInstrument.SelectedIndex < InstrumentTable.All.Length)
                return InstrumentTable.All[_cmbInstrument.SelectedIndex].Number;
            string t = (_cmbInstrument.Text ?? "").Trim();
            int sp = t.IndexOf(' ');
            string head = sp > 0 ? t.Substring(0, sp) : t;
            int n;
            if (int.TryParse(head, out n)) return n;
            return 0;
        }

        private void ApplyKitFields()
        {
            if (_backup == null || CurrentKit < 0) return;
            _backup.SetKitName(CurrentKit, _txtKitName.Text);
            _backup.SetKitSubName(CurrentKit, _txtKitSubName.Text);
            _backup.SetKitVolume(CurrentKit, (int)_numKitVolume.Value);
            _backup.SetKitHhVolume(CurrentKit, (int)_numKitHhVolume.Value);
            _backup.SetKitPadSensitivity(CurrentKit, (int)_numKitSensitivity.Value);
            _backup.SetKitBalance(CurrentKit, (int)_numKitBalance.Value);
            UpdateKitListEntry();
            SetStatus("Edited kit " + (CurrentKit + 1) + " (not saved yet).", Color.DarkOrange);
        }

        private void ApplyPadFields()
        {
            if (_backup == null || CurrentKit < 0 || CurrentPad < 0) return;
            int kit = CurrentKit, pad = CurrentPad, layer = CurrentLayer;
            int inst = ReadInstrument();
            if (inst > 0) _backup.SetPadInstrument(kit, pad, layer, inst);
            _backup.SetPadPitch(kit, pad, layer, (int)_numPitch.Value);
            _backup.SetPadVolume(kit, pad, layer, (int)_numPadVolume.Value);
            _backup.SetPadPan(kit, pad, layer, (int)_numPan.Value);
            _backup.SetPadAmbient(kit, pad, layer, (int)_numAmbient.Value);
            _backup.SetPadMuffling(kit, pad, layer, (int)_numMuffling.Value);
            _backup.SetPadSweep(kit, pad, layer, (int)_numSweep.Value);
            _backup.SetPadMfxAssign(kit, pad, layer, (int)_numMfxAssign.Value);
            _backup.SetPadMidiNote(kit, pad, (int)_numMidiNote.Value);
            _backup.SetPadMidiGate(kit, pad, (int)_numMidiGate.Value);
            _backup.SetPadVelocityFade(kit, pad, (int)_numVelocityFade.Value);
            _backup.SetPadFixedVelocity(kit, pad, (int)_numFixedVelocity.Value);
            if (_cmbTriggerMode.SelectedIndex >= 0) _backup.SetPadTriggerMode(kit, pad, _cmbTriggerMode.SelectedIndex);
            _backup.SetPadMuteGroup(kit, pad, (int)_numMuteGroup.Value);
            _backup.SetPadMonoPoly(kit, pad, (int)_numMonoPoly.Value);
            _backup.SetPadLayerMode(kit, pad, (int)_numLayerMode.Value);
            _lblPadHex.Text = HexDump(kit, pad);
            SetStatus("Edited " + HpdBackup.PadNames[pad] + " of kit " + (kit + 1) + " (not saved yet).", Color.DarkOrange);
        }

        private void UpdateKitListEntry()
        {
            if (_backup == null || CurrentKit < 0 || CurrentKit >= _lstBackupKits.Items.Count) return;
            string text = (CurrentKit + 1).ToString("000") + "  " + Safe(_backup.GetKitName(CurrentKit));
            if (_lstBackupKits.Items[CurrentKit].ToString() != text)
                _lstBackupKits.Items[CurrentKit] = text;
        }

        private string HexDump(int kit, int pad)
        {
            int off = HpdBackup.PadIndex + (kit * HpdBackup.PadsPerKit + pad) * HpdBackup.PadSize;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("pad bytes @0x" + off.ToString("X") + "   -  (raw) values are unverified bytes");
            for (int row = 0; row < 4; row++)
            {
                sb.Append(row * 16 > 0 ? ((row * 16).ToString("X2") + "  ") : "00  ");
                for (int i = 0; i < 16; i++)
                {
                    int idx = row * 16 + i;
                    if (idx >= HpdBackup.PadSize) break;
                    sb.Append(_backup.GetByte(off + idx).ToString("X2"));
                    sb.Append(' ');
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}









