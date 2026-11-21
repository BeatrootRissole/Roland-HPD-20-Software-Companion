using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Hpd20Ctrl
{
    /// <summary>Explorer-style browser: a folder tree on top, the audio files in that folder below.</summary>
    public sealed class FileExplorer : UserControl
    {
        private TreeView _tree;
        private ListView _list;
        private TextBox _path;
        private readonly ToolTip _tips = new ToolTip();

        /// <summary>Raised with the files to enqueue.</summary>
        public event Action<List<string>> FilesChosen;

        public FileExplorer()
        {
            _tips.AutoPopDelay = 30000;

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 26;

            _path = new TextBox();
            _path.Dock = DockStyle.Fill;
            _path.ReadOnly = true;
            _path.BorderStyle = BorderStyle.FixedSingle;
            _path.Font = new Font("Consolas", 7.5f);
            top.Controls.Add(_path);
            _tips.SetToolTip(_path, "The folder currently listed below.");

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterDistance = 150;

            _tree = new TreeView();
            _tree.Dock = DockStyle.Fill;
            _tree.HideSelection = false;
            _tree.BeforeExpand += delegate(object s, TreeViewCancelEventArgs e) { PopulateNode(e.Node); };
            _tree.AfterSelect += delegate(object s, TreeViewEventArgs e) { ShowFolder(e.Node); };
            _tips.SetToolTip(_tree, "Drives and folders. Expand an item to see its subfolders; selecting one lists its audio files below.");

            _list = new ListView();
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = true;
            _list.HideSelection = false;
            _list.Columns.Add("Audio file", 150);
            _list.Columns.Add("Size", 60);
            _list.DoubleClick += delegate { ChooseSelected(); };
            _list.ItemDrag += delegate(object s, ItemDragEventArgs e)
            {
                List<string> files = new List<string>();
                foreach (ListViewItem it in _list.SelectedItems)
                    if (it.Tag is string) files.Add((string)it.Tag);
                if (files.Count == 0 && e.Item is ListViewItem && ((ListViewItem)e.Item).Tag is string)
                    files.Add((string)((ListViewItem)e.Item).Tag);
                if (files.Count == 0) return;
                DataObject data = new DataObject(DataFormats.FileDrop, files.ToArray());
                _list.DoDragDrop(data, DragDropEffects.Copy);
            };
            _tips.SetToolTip(_list, "Audio files in the selected folder. Double-click one (or press Add) to put it in the conversion queue.");

            Panel buttons = new Panel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 28;

            Button add = new Button();
            add.Text = "Add";
            add.SetBounds(0, 2, 64, 24);
            add.Click += delegate { ChooseSelected(); };
            _tips.SetToolTip(add, "Add the selected files to the conversion queue.");

            Button addAll = new Button();
            addAll.Text = "Add all";
            addAll.SetBounds(68, 2, 74, 24);
            addAll.Click += delegate { ChooseAll(); };
            _tips.SetToolTip(addAll, "Add every audio file in this folder to the conversion queue.");

            buttons.Controls.AddRange(new Control[] { add, addAll });

            _split = split;
            split.Panel1.Controls.Add(_tree);
            split.Panel2.Controls.Add(_list);
            split.Panel2.Controls.Add(buttons);

            Controls.Add(split);
            Controls.Add(top);

            PopulateDrives();
        }

        private SplitContainer _split;
        private bool _sized;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_split == null || _sized || Height < 160) return;
            _sized = true;
            try { _split.SplitterDistance = (int)(Height * 0.55); } catch { }
        }

        private void PopulateDrives()
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                    TreeNode n = new TreeNode(d.Name + (string.IsNullOrEmpty(d.VolumeLabel) ? "" : "  " + d.VolumeLabel));
                    n.Tag = d.RootDirectory.FullName;
                    n.Nodes.Add(new TreeNode("..."));     // placeholder so the expander shows
                    _tree.Nodes.Add(n);
                }
                catch { }
            }
            _tree.EndUpdate();
        }

        private void PopulateNode(TreeNode node)
        {
            if (node == null || !(node.Tag is string)) return;
            if (node.Nodes.Count != 1 || node.Nodes[0].Text != "...") return;
            node.Nodes.Clear();
            try
            {
                foreach (string dir in Directory.GetDirectories((string)node.Tag))
                {
                    try
                    {
                        TreeNode child = new TreeNode(Path.GetFileName(dir));
                        child.Tag = dir;
                        if (Directory.GetDirectories(dir).Length > 0) child.Nodes.Add(new TreeNode("..."));
                        node.Nodes.Add(child);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void ShowFolder(TreeNode node)
        {
            if (node == null || !(node.Tag is string)) return;
            string dir = (string)node.Tag;
            _path.Text = dir;
            _list.BeginUpdate();
            _list.Items.Clear();
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    if (!AudioFormats.IsSupportedExtension(f)) continue;
                    FileInfo fi = new FileInfo(f);
                    ListViewItem it = new ListViewItem(Path.GetFileName(f));
                    it.SubItems.Add((fi.Length / 1024) + " KB");
                    it.Tag = f;
                    _list.Items.Add(it);
                }
            }
            catch { }
            _list.EndUpdate();
        }

        public void SetFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            _path.Text = path;
            _list.BeginUpdate();
            _list.Items.Clear();
            try
            {
                foreach (string f in Directory.GetFiles(path))
                {
                    if (!AudioFormats.IsSupportedExtension(f)) continue;
                    FileInfo fi = new FileInfo(f);
                    ListViewItem it = new ListViewItem(Path.GetFileName(f));
                    it.SubItems.Add((fi.Length / 1024) + " KB");
                    it.Tag = f;
                    _list.Items.Add(it);
                }
            }
            catch { }
            _list.EndUpdate();
        }

        private void ChooseSelected()
        {
            List<string> files = new List<string>();
            foreach (ListViewItem it in _list.SelectedItems)
                if (it.Tag is string) files.Add((string)it.Tag);
            if (files.Count > 0 && FilesChosen != null) FilesChosen(files);
        }

        private void ChooseAll()
        {
            List<string> files = new List<string>();
            foreach (ListViewItem it in _list.Items)
                if (it.Tag is string) files.Add((string)it.Tag);
            if (files.Count > 0 && FilesChosen != null) FilesChosen(files);
        }
    }
}

