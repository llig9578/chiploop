using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ProjectFileAssociation.Register();

        var studio = new Studio();
        if (args.Length == 1)
            studio.Shown += (sender, eventArgs) => studio.OpenProjectPath(args[0]);
        Application.Run(studio);
    }
}

internal sealed class Studio : Form
{
    private const int Steps = Synth.StepsPerBar;
    internal const int MaxBars = 32;
    internal const int MinBpm = 60;
    internal const int MaxBpm = 190;
    private const string AppTitle = "CHIPLOOP · 8bit 音乐制作器";
    private const string ProjectFilter = "CHIPLOOP 工程 (*.chiploop)|*.chiploop";
    private static readonly Color Paper = Color.FromArgb(249, 249, 240);
    private static readonly Color Ink = Color.FromArgb(25, 37, 31);
    private static readonly Color Board = Color.FromArgb(27, 41, 34);
    private static readonly Color Lime = Color.FromArgb(198, 244, 101);
    private static readonly Color[] TrackColors = { Lime, Color.FromArgb(255, 193, 121), Color.FromArgb(244, 147, 180) };
    private static readonly string[] TrackNames = { "旋律", "低音", "鼓点" };
    private static readonly string[][] Notes = {
        new[] {"C5","B4","A4","G4","F4","E4","D4","C4"},
        new[] {"C3","B2","A2","G2","F2","E2","D2","C2"},
        new[] {"踩镲","军鼓","底鼓"}
    };

    private readonly StrokeGrid grid = new StrokeGrid();
    private readonly Button[] tabs = new Button[3];
    private readonly Label[] headers = new Label[Steps];
    private readonly Button play;
    private readonly Label tempo = new Label();
    private readonly Label status = new Label();
    private readonly TrackBar bpm = new TrackBar();
    private readonly NumericUpDown length = BarInput("总小节数", MaxBars);
    private readonly NumericUpDown editBar = BarInput("编辑小节", 1);
    private readonly Label duration = new Label();
    private readonly Timer cursorTimer = new Timer();
    private readonly Timer refreshAudio = new Timer();
    private readonly Stopwatch time = new Stopwatch();
    private bool[,,] pattern = Demo();
    private Button[,] cells;
    private SoundPlayer player;
    private MemoryStream audio;
    private int selected;
    private int activeStep = -1;
    private int playingBpm;
    private int playingSteps;
    private int cursorOffset;
    private Point? strokeCell;
    private MouseButtons strokeButton;
    private bool playing;
    private bool dirty;
    private string projectPath;

    internal Studio()
    {
        Text = AppTitle;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 10);
        ForeColor = Ink;
        BackColor = Paper;
        ClientSize = new Size(940, 555);
        MinimumSize = new Size(710, 535);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(19, 15, 19, 12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        Controls.Add(layout);

        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = new Label {
            Text = "CHIPLOOP   /   8bit 音乐制作器", AutoSize = true, Anchor = AnchorStyles.Left,
            Font = new Font(Font.FontFamily, 19, FontStyle.Bold)
        };
        var export = ActionButton("导出 WAV", Paper);
        export.Click += (sender, args) => ExportSong();
        var open = ActionButton("打开工程", Paper);
        open.Click += (sender, args) => OpenProject();
        var save = ActionButton("保存工程", Paper);
        save.Click += (sender, args) => SaveProject();
        var files = FlowRow();
        files.Dock = DockStyle.None;
        files.Anchor = AnchorStyles.Right;
        files.AutoSize = true;
        files.Controls.AddRange(new Control[] { open, save, export });
        heading.Controls.Add(title, 0, 0);
        heading.Controls.Add(files, 1, 0);
        layout.Controls.Add(heading, 0, 0);

        var transport = FlowRow();
        play = ActionButton("▶  播放", Lime);
        play.MinimumSize = new Size(100, 35);
        play.Click += (sender, args) => TogglePlayback();
        bpm.Minimum = MinBpm;
        bpm.Maximum = MaxBpm;
        bpm.Value = 120;
        bpm.AutoSize = false;
        bpm.Width = 170;
        bpm.Height = 32;
        bpm.TickStyle = TickStyle.None;
        bpm.AccessibleName = "速度 BPM";
        bpm.Margin = new Padding(10, 6, 2, 0);
        bpm.ValueChanged += (sender, args) => {
            UpdateTempo();
            UpdateDuration();
            MarkChanged();
            QueueAudioRefresh();
        };
        tempo.AutoSize = true;
        tempo.Margin = new Padding(5, 11, 0, 0);
        UpdateTempo();
        transport.Controls.Add(play);
        transport.Controls.Add(bpm);
        transport.Controls.Add(tempo);
        layout.Controls.Add(transport, 0, 1);

        var tracks = FlowRow();
        for (int track = 0; track < tabs.Length; track++)
        {
            int index = track;
            tabs[track] = ActionButton(TrackNames[track], Paper);
            tabs[track].MinimumSize = new Size(86, 34);
            tabs[track].Click += (sender, args) => ShowTrack(index);
            tracks.Controls.Add(tabs[track]);
        }
        AddBarControl(tracks, "总小节", length);
        AddBarControl(tracks, "编辑小节", editBar);
        duration.AutoSize = true;
        duration.Margin = new Padding(8, 11, 0, 0);
        tracks.Controls.Add(duration);
        length.ValueChanged += (sender, args) => {
            editBar.Maximum = length.Value;
            UpdateDuration();
            MarkChanged();
            QueueAudioRefresh();
        };
        editBar.ValueChanged += (sender, args) => {
            EndStroke();
            RefreshCells();
        };
        UpdateDuration();
        layout.Controls.Add(tracks, 0, 2);

        grid.Dock = DockStyle.Fill;
        grid.Margin = Padding.Empty;
        grid.Padding = new Padding(10);
        grid.BackColor = Board;
        grid.MouseDown += (sender, args) => {
            if (args.Button != MouseButtons.Left && args.Button != MouseButtons.Right) return;
            Point? cell = CellAt(args.Location);
            if (cell.HasValue) BeginStroke(cell.Value, args.Button == MouseButtons.Left);
        };
        grid.PointerMoved += (buttons, location) => {
            if (strokeButton == MouseButtons.None) return;
            if ((buttons & strokeButton) == 0)
                EndStroke();
            else
                ContinueStroke(location);
        };
        grid.MouseUp += (sender, args) => {
            if (args.Button != strokeButton) return;
            ContinueStroke(args.Location);
            EndStroke();
        };
        grid.MouseCaptureChanged += (sender, args) => {
            if (!grid.Capture) EndStroke();
        };
        Deactivate += (sender, args) => EndStroke();
        grid.ColumnCount = Steps + 1;
        grid.RowCount = 9;
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        for (int step = 0; step < Steps; step++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / Steps));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        for (int row = 0; row < 8; row++)
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 12.5f));
        layout.Controls.Add(grid, 0, 3);

        var actions = FlowRow();
        actions.Padding = new Padding(0, 8, 0, 0);
        var demo = ActionButton("加载示例", Paper);
        demo.Click += (sender, args) => {
            pattern = Demo();
            MarkChanged();
            RefreshCells();
            QueueAudioRefresh();
            status.Text = "已加载示例";
        };
        var clear = ActionButton("清空本小节音轨", Paper);
        clear.Click += (sender, args) => {
            for (int row = 0; row < Notes[selected].Length; row++)
                for (int step = 0; step < Steps; step++)
                    pattern[selected, row, BarStart + step] = false;
            MarkChanged();
            RefreshCells();
            QueueAudioRefresh();
            status.Text = "已清空第 " + editBar.Value + " 小节的" + TrackNames[selected] + "轨";
        };
        status.AutoSize = true;
        status.Margin = new Padding(8, 11, 0, 0);
        status.Text = "左键添加 · 右键擦除 · 支持拖动";
        actions.Controls.Add(demo);
        actions.Controls.Add(clear);
        actions.Controls.Add(status);
        layout.Controls.Add(actions, 0, 4);

        cursorTimer.Interval = 20;
        cursorTimer.Tick += (sender, args) => {
            int step = (cursorOffset + (int)(time.Elapsed.TotalSeconds * playingBpm / 15.0)) % playingSteps;
            SetPlaybackStep(step);
        };
        refreshAudio.Interval = 120;
        refreshAudio.Tick += (sender, args) => {
            refreshAudio.Stop();
            if (playing) StartLoop();
        };
        ShowTrack(0);
    }

    private int BarStart { get { return ((int)editBar.Value - 1) * Steps; } }
    private int SongSteps { get { return (int)length.Value * Steps; } }

    private static NumericUpDown BarInput(string name, int maximum)
    {
        return new NumericUpDown {
            Minimum = 1, Maximum = maximum, Value = 1, Width = 52,
            AccessibleName = name, Margin = new Padding(2, 8, 5, 0)
        };
    }

    private static void AddBarControl(FlowLayoutPanel row, string text, NumericUpDown input)
    {
        row.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(5, 11, 0, 0) });
        row.Controls.Add(input);
    }

    private void UpdateDuration()
    {
        duration.Text = "时长 " + (SongSteps * 15.0 / bpm.Value).ToString("0.##") + " 秒";
    }

    private static FlowLayoutPanel FlowRow()
    {
        return new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
    }

    private static Button ActionButton(string text, Color background)
    {
        var button = new Button {
            Text = text, AutoSize = true, Height = 35, MinimumSize = new Size(0, 35),
            FlatStyle = FlatStyle.Flat, BackColor = background, ForeColor = Ink,
            UseVisualStyleBackColor = false, Margin = new Padding(0, 3, 8, 3)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(160, 174, 160);
        return button;
    }

    private static bool[,,] Demo()
    {
        int[][][] demo = {
            new[] { new[] {0,8}, new[] {14}, new[] {6,12}, new[] {2,10}, new int[0], new[] {3,9,15}, new int[0], new[] {4} },
            new[] { new int[0], new int[0], new[] {12}, new[] {8}, new int[0], new int[0], new int[0], new[] {0,4,6} },
            new[] { new[] {0,2,4,6,8,10,12,14}, new[] {4,12}, new[] {0,8,11} }
        };
        var song = new bool[3, 8, Steps * MaxBars];
        for (int track = 0; track < demo.Length; track++)
            for (int row = 0; row < demo[track].Length; row++)
                foreach (int step in demo[track][row])
                    song[track, row, step] = true;
        return song;
    }

    private void ShowTrack(int track)
    {
        EndStroke();
        selected = track;
        for (int i = 0; i < tabs.Length; i++)
            tabs[i].BackColor = i == selected ? TrackColors[i] : Paper;

        grid.SuspendLayout();
        while (grid.Controls.Count > 0)
            grid.Controls[0].Dispose();

        for (int row = 1; row < grid.RowCount; row++)
            grid.RowStyles[row].Height = row <= Notes[track].Length ? 100f / Notes[track].Length : 0;
        cells = new Button[Notes[track].Length, Steps];
        for (int step = 0; step < Steps; step++)
        {
            headers[step] = GridLabel((step + 1).ToString("00"), ContentAlignment.MiddleCenter);
            grid.Controls.Add(headers[step], step + 1, 0);
        }
        for (int row = 0; row < Notes[track].Length; row++)
        {
            grid.Controls.Add(GridLabel(Notes[track][row], ContentAlignment.MiddleLeft), 0, row + 1);
            for (int step = 0; step < Steps; step++)
            {
                int pitch = row;
                int column = step;
                var button = new CheckBoxCell {
                    AccessibleName = Notes[track][row] + "，第 " + (step + 1) + " 步",
                    Dock = DockStyle.Fill, Margin = new Padding(2), FlatStyle = FlatStyle.Flat,
                    UseVisualStyleBackColor = false,
                    StartStroke = add => BeginStroke(new Point(column, pitch), add)
                };
                button.Click += (sender, args) => EditCell(pitch, column);
                cells[row, step] = button;
                grid.Controls.Add(button, step + 1, row + 1);
            }
        }
        RefreshCells();
        grid.ResumeLayout();
    }

    private static Label GridLabel(string text, ContentAlignment alignment)
    {
        return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = alignment, ForeColor = Color.Gainsboro };
    }

    private void RefreshCells()
    {
        for (int step = 0; step < Steps; step++)
            PaintColumn(step);
    }

    private void PaintColumn(int step)
    {
        Color muted = step % 4 == 0 ? Color.FromArgb(65, 86, 72) : Color.FromArgb(48, 65, 56);
        bool isPlayingColumn = BarStart + step == activeStep;
        headers[step].ForeColor = isPlayingColumn ? Lime : Color.Gainsboro;
        for (int row = 0; row < cells.GetLength(0); row++)
        {
            var button = (CheckBoxCell)cells[row, step];
            button.IsOn = pattern[selected, row, BarStart + step];
            button.BackColor = button.IsOn ? TrackColors[selected] : muted;
            button.FlatAppearance.BorderColor = isPlayingColumn ? Color.White : Color.FromArgb(88, 111, 95);
            button.FlatAppearance.BorderSize = isPlayingColumn ? 2 : 1;
        }
    }

    private void BeginStroke(Point cell, bool add)
    {
        if (strokeButton != MouseButtons.None) return;
        strokeButton = add ? MouseButtons.Left : MouseButtons.Right;
        strokeCell = cell;
        grid.Capture = true;
        SetCell(cell.Y, cell.X, add, true);
    }

    private void EndStroke()
    {
        if (strokeButton == MouseButtons.None) return;
        strokeButton = MouseButtons.None;
        strokeCell = null;
        grid.Capture = false;
    }

    private void ContinueStroke(Point location)
    {
        Point? current = CellAt(location);
        if (current.HasValue)
            PaintStroke(strokeCell ?? current.Value, current.Value);
        strokeCell = current;
    }

    private Point? CellAt(Point location)
    {
        int column = FindSlot(location.X - grid.Padding.Left, grid.GetColumnWidths()) - 1;
        int row = FindSlot(location.Y - grid.Padding.Top, grid.GetRowHeights()) - 1;
        if (column < 0 || column >= Steps || row < 0 || row >= cells.GetLength(0))
            return null;
        return new Point(column, row);
    }

    private static int FindSlot(int offset, int[] sizes)
    {
        if (offset < 0) return -1;
        for (int i = 0; i < sizes.Length; i++)
        {
            if (offset < sizes[i]) return i;
            offset -= sizes[i];
        }
        return -1;
    }

    private void PaintStroke(Point from, Point to)
    {
        int x = from.X;
        int y = from.Y;
        int dx = Math.Abs(to.X - x);
        int dy = -Math.Abs(to.Y - y);
        int sx = Math.Sign(to.X - x);
        int sy = Math.Sign(to.Y - y);
        int error = dx + dy;
        while (true)
        {
            SetCell(y, x, strokeButton == MouseButtons.Left, false);
            if (x == to.X && y == to.Y) break;

            // Mouse move events can skip cells when the pointer moves quickly.
            int doubled = 2 * error;
            if (doubled >= dy)
            {
                error += dy;
                x += sx;
            }
            if (doubled <= dx)
            {
                error += dx;
                y += sy;
            }
        }
    }

    private void EditCell(int row, int step)
    {
        if (strokeButton == MouseButtons.None)
            SetCell(row, step, !pattern[selected, row, BarStart + step], true);
    }

    private void SetCell(int row, int step, bool value, bool preview)
    {
        int songStep = BarStart + step;
        if (pattern[selected, row, songStep] == value) return;
        pattern[selected, row, songStep] = value;
        MarkChanged();
        PaintColumn(step);
        QueueAudioRefresh();
        if (!playing && preview && value)
        {
            try { PlayBytes(Synth.Preview(selected, row), false); }
            catch (Exception) { status.Text = "声音预览不可用"; }
        }
    }

    private void UpdateTempo()
    {
        tempo.Text = bpm.Value + " BPM";
    }

    private void QueueAudioRefresh()
    {
        if (!playing) return;
        refreshAudio.Stop();
        refreshAudio.Start();
    }

    private void TogglePlayback()
    {
        if (playing)
        {
            StopLoop();
            status.Text = "左键添加 · 右键擦除 · 支持拖动";
        }
        else
        {
            StartLoop();
        }
    }

    private void StartLoop()
    {
        try
        {
            // Rotate the replacement loop so edits and tempo changes keep the current step.
            int stepCount = SongSteps;
            int firstStep = playing && activeStep >= 0 && activeStep < stepCount ? activeStep : 0;
            PlayBytes(Synth.Render(pattern, bpm.Value, firstStep, stepCount), true);
            playing = true;
            playingBpm = bpm.Value;
            playingSteps = stepCount;
            cursorOffset = firstStep;
            time.Restart();
            cursorTimer.Start();
            play.Text = "■  停止";
            SetPlaybackStep(firstStep);
            ShowPlaybackBar();
        }
        catch (Exception)
        {
            StopLoop();
            status.Text = "无法播放，请检查 Windows 音频输出";
        }
    }

    private void SetPlaybackStep(int step)
    {
        if (step == activeStep) return;
        int previous = activeStep;
        activeStep = step;
        int start = BarStart;
        if (previous >= start && previous < start + Steps) PaintColumn(previous - start);
        if (step >= start && step < start + Steps) PaintColumn(step - start);
        if (step >= 0 && (previous < 0 || previous / Steps != step / Steps)) ShowPlaybackBar();
    }

    private void ShowPlaybackBar()
    {
        status.Text = "正在播放第 " + (activeStep / Steps + 1) + " / " + (playingSteps / Steps) + " 小节";
    }

    private void PlayBytes(byte[] bytes, bool loop)
    {
        ReleaseAudio();
        audio = new MemoryStream(bytes, false);
        player = new SoundPlayer(audio);
        player.Load();
        if (loop) player.PlayLooping();
        else player.Play();
    }

    private void ReleaseAudio()
    {
        if (player != null)
        {
            player.Stop();
            player.Dispose();
            player = null;
        }
        if (audio != null)
        {
            audio.Dispose();
            audio = null;
        }
    }

    private void StopLoop()
    {
        playing = false;
        refreshAudio.Stop();
        cursorTimer.Stop();
        time.Stop();
        ReleaseAudio();
        SetPlaybackStep(-1);
        play.Text = "▶  播放";
    }

    private void MarkChanged()
    {
        if (dirty) return;
        dirty = true;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        string name = projectPath == null ? "未命名工程" : Path.GetFileNameWithoutExtension(projectPath);
        Text = name + (dirty ? " *" : "") + " — " + AppTitle;
    }

    private bool ConfirmSaveChanges()
    {
        if (!dirty) return true;
        var result = MessageBox.Show(this, "当前工程有未保存的修改。是否保存？", AppTitle,
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
        return result == DialogResult.No || (result == DialogResult.Yes && SaveProject());
    }

    private bool SaveProject()
    {
        string path = projectPath;
        if (path == null)
        {
            using (var dialog = new SaveFileDialog {
                Filter = ProjectFilter, DefaultExt = "chiploop", FileName = "我的音乐.chiploop",
                Title = "保存 CHIPLOOP 工程"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                path = dialog.FileName;
            }
        }
        try
        {
            new Project(pattern, bpm.Value, (int)length.Value).Save(path);
            projectPath = path;
            dirty = false;
            UpdateTitle();
            status.Text = "已保存工程 " + Path.GetFileName(path);
            return true;
        }
        catch (Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException)) throw;
            MessageBox.Show(this, "无法保存工程。\n" + error.Message, AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void OpenProject()
    {
        using (var dialog = new OpenFileDialog { Filter = ProjectFilter, Title = "打开 CHIPLOOP 工程" })
        {
            if (dialog.ShowDialog(this) == DialogResult.OK)
                OpenProjectPath(dialog.FileName);
        }
    }

    internal void OpenProjectPath(string path)
    {
        Project project;
        try
        {
            path = Path.GetFullPath(path);
            project = Project.Load(path);
        }
        catch (Exception error)
        {
            if (!(error is InvalidDataException || error is IOException ||
                error is UnauthorizedAccessException || error is ArgumentException)) throw;
            MessageBox.Show(this, "无法打开工程。\n" + error.Message, AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        bool hadChanges = dirty;
        if (!ConfirmSaveChanges()) return;
        // Keep the newly written version if Save updated the file selected in Open.
        if (hadChanges && !dirty && string.Equals(projectPath, path,
            StringComparison.OrdinalIgnoreCase)) return;
        ApplyProject(project, path);
    }

    private void ApplyProject(Project project, string path)
    {
        StopLoop();
        pattern = project.Pattern;
        bpm.Value = project.Bpm;
        length.Value = project.Bars;
        editBar.Value = 1;
        ShowTrack(0);
        projectPath = path;
        dirty = false;
        UpdateTitle();
        status.Text = "已打开工程 " + Path.GetFileName(path);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.O))
        {
            OpenProject();
            return true;
        }
        if (keyData == (Keys.Control | Keys.S))
        {
            SaveProject();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!e.Cancel && e.CloseReason == CloseReason.UserClosing)
            e.Cancel = !ConfirmSaveChanges();
    }

    private void ExportSong()
    {
        using (var dialog = new SaveFileDialog {
            Filter = "WAV 音频 (*.wav)|*.wav", DefaultExt = "wav", FileName = "chiploop.wav",
            Title = "导出完整 8bit 音乐"
        })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllBytes(dialog.FileName, Synth.Render(pattern, bpm.Value, 0, SongSteps));
                status.Text = "已导出 " + Path.GetFileName(dialog.FileName);
            }
            catch (IOException)
            {
                status.Text = "无法写入 WAV，请选择其他文件位置";
            }
            catch (UnauthorizedAccessException)
            {
                status.Text = "无法写入 WAV，请选择其他文件位置";
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            cursorTimer.Dispose();
            refreshAudio.Dispose();
            ReleaseAudio();
        }
        base.Dispose(disposing);
    }

    private sealed class StrokeGrid : TableLayoutPanel
    {
        private const int MouseMoveMessage = 0x0200;
        private const int LeftButtonFlag = 0x0001;
        private const int RightButtonFlag = 0x0002;

        internal event Action<MouseButtons, Point> PointerMoved;

        protected override void WndProc(ref Message message)
        {
            // Use the button state captured with the move, even if it has changed since.
            if (message.Msg == MouseMoveMessage && Capture && PointerMoved != null)
            {
                int flags = message.WParam.ToInt32();
                int position = unchecked((int)message.LParam.ToInt64());
                MouseButtons buttons = MouseButtons.None;
                if ((flags & LeftButtonFlag) != 0) buttons |= MouseButtons.Left;
                if ((flags & RightButtonFlag) != 0) buttons |= MouseButtons.Right;
                var point = new Point(unchecked((short)position), unchecked((short)(position >> 16)));
                PointerMoved(buttons, point);
            }
            base.WndProc(ref message);
        }
    }

    private sealed class CheckBoxCell : Button
    {
        private const int LeftButtonDown = 0x0201;
        private const int LeftButtonDoubleClick = 0x0203;
        private const int RightButtonDown = 0x0204;
        private const int RightButtonDoubleClick = 0x0206;
        internal bool IsOn { get; set; }
        internal Action<bool> StartStroke { get; set; }

        protected override void WndProc(ref Message message)
        {
            bool add = message.Msg == LeftButtonDown || message.Msg == LeftButtonDoubleClick;
            bool erase = message.Msg == RightButtonDown || message.Msg == RightButtonDoubleClick;
            if (add || erase)
            {
                // The grid captures the pointer so the stroke can cross other buttons.
                Focus();
                StartStroke(add);
                return;
            }
            base.WndProc(ref message);
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new CellAccessibility(this);
        }

        private sealed class CellAccessibility : Control.ControlAccessibleObject
        {
            private readonly CheckBoxCell cell;

            internal CellAccessibility(CheckBoxCell cell) : base(cell)
            {
                this.cell = cell;
            }

            public override AccessibleRole Role { get { return AccessibleRole.CheckButton; } }

            public override AccessibleStates State
            {
                get { return base.State | (cell.IsOn ? AccessibleStates.Checked : AccessibleStates.None); }
            }
        }
    }
}

internal static class ProjectFileAssociation
{
    private const string Extension = ".chiploop";
    private const string ProgId = "CHIPLOOP.Project";
    private const int AssociationChanged = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    internal static void Register()
    {
        try
        {
            string currentHandler;
            using (var extension = Registry.ClassesRoot.OpenSubKey(Extension))
                currentHandler = extension == null ? null : extension.GetValue("") as string;

            bool changed = false;
            using (var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
            using (var type = classes.CreateSubKey(ProgId))
            using (var open = type.CreateSubKey(@"shell\open\command"))
            using (var extension = classes.CreateSubKey(Extension))
            using (var openWith = extension.CreateSubKey("OpenWithProgIds"))
            {
                changed |= SetValue(type, "", "CHIPLOOP 工程");
                changed |= SetValue(open, "", "\"" + Application.ExecutablePath + "\" \"%1\"");
                changed |= SetValue(openWith, ProgId, "");
                if (string.IsNullOrEmpty(currentHandler))
                    changed |= SetValue(extension, "", ProgId);
            }
            if (changed) SHChangeNotify(AssociationChanged, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
        catch (IOException) { }
    }

    private static bool SetValue(RegistryKey key, string name, string value)
    {
        if (string.Equals(key.GetValue(name) as string, value, StringComparison.Ordinal)) return false;
        key.SetValue(name, value);
        return true;
    }
}

internal sealed class Project
{
    private const uint Signature = 0x50494843; // CHIP
    private const byte Version = 1;
    private const int Tracks = 3;
    private const int Rows = 8;
    private const int Steps = Synth.StepsPerBar;
    private const int HeaderBytes = 8;

    internal readonly bool[,,] Pattern;
    internal readonly int Bpm;
    internal readonly int Bars;

    internal Project(bool[,,] pattern, int bpm, int bars)
    {
        Pattern = pattern;
        Bpm = bpm;
        Bars = bars;
    }

    internal void Save(string path)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(path), Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                int storedBars = Pattern.GetLength(2) / Steps;
                writer.Write(Signature);
                writer.Write(Version);
                writer.Write((byte)Bpm);
                writer.Write((byte)Bars);
                writer.Write((byte)storedBars);
                for (int bar = 0; bar < storedBars; bar++)
                    for (int track = 0; track < Tracks; track++)
                        for (int row = 0; row < Rows; row++)
                        {
                            ushort notes = 0;
                            for (int step = 0; step < Steps; step++)
                                if (Pattern[track, row, bar * Steps + step])
                                    notes |= (ushort)(1 << step);
                            writer.Write(notes);
                        }
                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static Project Load(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var reader = new BinaryReader(stream))
        {
            if (stream.Length < HeaderBytes || reader.ReadUInt32() != Signature)
                throw new InvalidDataException("文件不是 CHIPLOOP 工程。");
            if (reader.ReadByte() != Version)
                throw new InvalidDataException("不支持这个工程文件版本。");
            int bpm = reader.ReadByte();
            int bars = reader.ReadByte();
            int storedBars = reader.ReadByte();
            if (bpm < Studio.MinBpm || bpm > Studio.MaxBpm || bars < 1 || bars > storedBars ||
                storedBars > Studio.MaxBars || stream.Length != HeaderBytes + storedBars * Tracks * Rows * 2)
                throw new InvalidDataException("工程数据不完整或已损坏。");

            var pattern = new bool[Tracks, Rows, Studio.MaxBars * Steps];
            for (int bar = 0; bar < storedBars; bar++)
                for (int track = 0; track < Tracks; track++)
                    for (int row = 0; row < Rows; row++)
                    {
                        ushort notes = reader.ReadUInt16();
                        for (int step = 0; step < Steps; step++)
                            pattern[track, row, bar * Steps + step] = (notes & (1 << step)) != 0;
                    }
            return new Project(pattern, bpm, bars);
        }
    }
}

internal static class Synth
{
    internal const int StepsPerBar = 16;
    private const int Rate = 44100;
    private const double Tau = Math.PI * 2;
    private static readonly int[,] Midi = {
        { 72,71,69,67,65,64,62,60 },
        { 48,47,45,43,41,40,38,36 }
    };

    internal static byte[] Render(bool[,,] song, int bpm, int firstStep, int stepCount)
    {
        double stepSamples = Rate * 15.0 / bpm;
        var mix = new double[(int)Math.Round(stepSamples * stepCount)];
        for (int step = 0; step < stepCount; step++)
        {
            int start = (int)Math.Round(stepSamples * step);
            int end = (int)Math.Round(stepSamples * (step + 1));
            for (int track = 0; track < 3; track++)
                for (int row = 0; row < (track == 2 ? 3 : 8); row++)
                    if (song[track, row, (step + firstStep) % stepCount])
                        AddNote(mix, start, end - start, track, row);
        }
        return Encode(mix);
    }

    internal static byte[] Preview(int track, int row)
    {
        var mix = new double[(int)(Rate * .16)];
        AddNote(mix, 0, mix.Length, track, row);
        return Encode(mix);
    }

    private static void AddNote(double[] mix, int start, int length, int track, int row)
    {
        double duration = length / (double)Rate;
        if (track < 2) duration *= .92;
        else duration = Math.Min(duration, row == 0 ? .045 : row == 1 ? .13 : .16);

        int samples = (int)(Rate * duration);
        double frequency = track < 2 ? 440 * Math.Pow(2, (Midi[track, row] - 69) / 12.0) : 152;
        double phase = 0;
        double low = 0;
        uint seed = 9183;
        for (int i = 0; i < samples; i++)
        {
            double fraction = i / (double)samples;
            double attack = Math.Min(1, i / (Rate * .003));
            double envelope = attack * Math.Pow(1 - fraction, track == 2 ? 3 : 1.45);
            double sample;
            if (track == 0)
            {
                sample = (phase < .25 ? 1 : -1) * .15;
            }
            else if (track == 1)
            {
                sample = (1 - 4 * Math.Abs(phase - .5)) * .24;
            }
            else if (row == 2)
            {
                sample = Math.Sin(Tau * phase) * .76;
                frequency = 42 + 110 * Math.Exp(-5 * fraction);
            }
            else
            {
                seed = unchecked(seed * 1664525u + 1013904223u);
                double noise = seed / 2147483648.0 - 1;
                low += (noise - low) * (row == 0 ? .55 : .13);
                sample = (noise - low) * (row == 0 ? .20 : .42);
            }
            phase += frequency / Rate;
            phase -= Math.Floor(phase);
            mix[start + i] += sample * envelope;
        }
    }

    private static byte[] Encode(double[] samples)
    {
        using (var memory = new MemoryStream())
        using (var writer = new BinaryWriter(memory))
        {
            writer.Write(new[] {'R','I','F','F'});
            writer.Write(36 + samples.Length * 2);
            writer.Write(new[] {'W','A','V','E','f','m','t',' '});
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(Rate);
            writer.Write(Rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(new[] {'d','a','t','a'});
            writer.Write(samples.Length * 2);
            foreach (double sample in samples)
                writer.Write((short)Math.Round(Math.Tanh(sample) * short.MaxValue));
            return memory.ToArray();
        }
    }
}
