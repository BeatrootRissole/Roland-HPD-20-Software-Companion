using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Hpd20Ctrl
{
    /// <summary>
    /// Applies a dark or light palette to every control in a window, including the custom-drawn pad
    /// surface and waveform view, and the Windows title bar.
    /// </summary>
    public static class Theme
    {
        public static bool Dark { get; private set; }

        public static Color Window;
        public static Color Surface;
        public static Color Field;
        public static Color TextColor;
        public static Color Muted;
        public static Color Border;
        public static Color Selection;

        static Theme()
        {
            SetPalette(false);
        }

        private static void SetPalette(bool dark)
        {
            Dark = dark;
            if (dark)
            {
                Window = Color.FromArgb(30, 31, 34);
                Surface = Color.FromArgb(37, 38, 42);
                Field = Color.FromArgb(45, 47, 52);
                TextColor = Color.FromArgb(232, 233, 236);
                Muted = Color.FromArgb(150, 152, 160);
                Border = Color.FromArgb(66, 68, 74);
                Selection = Color.FromArgb(58, 92, 150);
            }
            else
            {
                Window = SystemColors.Control;
                Surface = SystemColors.Control;
                Field = SystemColors.Window;
                TextColor = SystemColors.ControlText;
                Muted = Color.DimGray;
                Border = SystemColors.ControlDark;
                Selection = SystemColors.Highlight;
            }
        }

        /// <summary>Switches the palette and repaints the whole window.</summary>
        public static void Apply(Form form, bool dark)
        {
            SetPalette(dark);
            ApplyTo(form);
            form.BackColor = Window;
            form.ForeColor = TextColor;
            ApplyTitleBar(form, dark);
            form.Invalidate(true);
            foreach (Control c in form.Controls) c.Invalidate(true);
        }

        public static void ApplyTo(Control root)
        {
            if (root == null) return;

            TabControl tabs = root as TabControl;
            if (tabs != null)
            {
                // light mode keeps the native tab look; dark mode draws its own
                tabs.DrawMode = Dark ? TabDrawMode.OwnerDrawFixed : TabDrawMode.Normal;
                if (Dark)
                {
                    tabs.DrawItem -= DrawTab;
                    tabs.DrawItem += DrawTab;
                }
                else
                {
                    tabs.DrawItem -= DrawTab;
                }
                tabs.BackColor = Window;
            }

            Button button = root as Button;
            if (button != null)
            {
                button.FlatStyle = Dark ? FlatStyle.Flat : FlatStyle.Standard;
                button.BackColor = Dark ? Field : SystemColors.Control;
                button.ForeColor = TextColor;
                if (Dark)
                {
                    button.FlatAppearance.BorderColor = Border;
                    button.FlatAppearance.MouseOverBackColor = Color.FromArgb(58, 60, 66);
                    button.FlatAppearance.MouseDownBackColor = Color.FromArgb(50, 52, 57);
                }
            }
            else if (root is TextBox || root is ComboBox || root is NumericUpDown || root is ListView || root is TreeView || root is CheckBox || root is RadioButton)
            {
                root.BackColor = Field;
                root.ForeColor = TextColor;
            }
            else if (root is TabPage)
            {
                root.BackColor = Dark ? Surface : SystemColors.Control;
                root.ForeColor = TextColor;
            }
            else if (root is Label || root is GroupBox || root is Panel || root is Form || root is UserControl)
            {
                root.BackColor = (root is Panel || root is UserControl) ? Surface : Window;
                root.ForeColor = TextColor;
            }
            else if (root is SplitContainer)
            {
                root.BackColor = Window;
            }

            if (root is ProgressBar || root is TrackBar) root.BackColor = Dark ? Field : SystemColors.Control;

            foreach (Control child in root.Controls) ApplyTo(child);
        }

        private static void DrawTab(object sender, DrawItemEventArgs e)
        {
            TabControl tabs = (TabControl)sender;
            TabPage page = tabs.TabPages[e.Index];
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

            Color back = Dark
                ? (selected ? Field : Window)
                : (selected ? SystemColors.Control : SystemColors.ControlDark);
            using (SolidBrush b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
            if (Dark && selected)
            {
                using (SolidBrush accent = new SolidBrush(Selection))
                    e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top, e.Bounds.Width, 2);
            }
            TextRenderer.DrawText(e.Graphics, page.Text, tabs.Font, e.Bounds,
                                  Dark ? (selected ? TextColor : Muted) : (selected ? SystemColors.ControlText : SystemColors.ControlDarkDark),
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ---- title bar
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void ApplyTitleBar(Form form, bool dark)
        {
            if (!form.IsHandleCreated) return;
            int value = dark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int)) != 0)   // Win10 2004+
                    DwmSetWindowAttribute(form.Handle, 19, ref value, sizeof(int));       // older builds
            }
            catch { }
        }
    }
}
