using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Studio());
    }
}

internal sealed class Studio : Form
{
    private const int Steps = 16;
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

    private readonly TableLayoutPanel grid = new TableLayoutPanel();
    private readonly Button[] tabs = new Button[3];
    private readonly Label[] headers = new Label[Steps];
    private readonly Button play;
    private readonly Label tempo = new Label();
    private readonly Label status = new Label();
    private readonly TrackBar bpm = new TrackBar();
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
    private int cursorOffset;
    private bool playing;

    internal Studio()
    {
        Text = "CHIPLOOP · 8bit 音乐制作器";
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
        export.Anchor = AnchorStyles.Right;
        export.Click += (sender, args) => ExportSong();
        heading.Controls.Add(title, 0, 0);
        heading.Controls.Add(export, 1, 0);
        layout.Controls.Add(heading, 0, 0);

        var transport = FlowRow();
        play = ActionButton("▶  播放", Lime);
        play.MinimumSize = new Size(100, 35);
        play.Click += (sender, args) => TogglePlayback();
        bpm.Minimum = 60;
        bpm.Maximum = 190;
        bpm.Value = 120;
        bpm.AutoSize = false;
        bpm.Width = 170;
        bpm.Height = 32;
        bpm.TickStyle = TickStyle.None;
        bpm.AccessibleName = "速度 BPM";
        bpm.Margin = new Padding(10, 6, 2, 0);
        bpm.ValueChanged += (sender, args) => {
            UpdateTempo();
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
        layout.Controls.Add(tracks, 0, 2);

        grid.Dock = DockStyle.Fill;
        grid.Margin = Padding.Empty;
        grid.Padding = new Padding(10);
        grid.BackColor = Board;
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
            RefreshCells();
            QueueAudioRefresh();
            status.Text = "已加载示例";
        };
        var clear = ActionButton("清空当前轨", Paper);
        clear.Click += (sender, args) => {
            for (int row = 0; row < Notes[selected].Length; row++)
                for (int step = 0; step < Steps; step++)
                    pattern[selected, row, step] = false;
            RefreshCells();
            QueueAudioRefresh();
            status.Text = "已清空" + TrackNames[selected] + "轨";
        };
        status.AutoSize = true;
        status.Margin = new Padding(8, 11, 0, 0);
        status.Text = "16 步 · 4 拍循环";
        actions.Controls.Add(demo);
        actions.Controls.Add(clear);
        actions.Controls.Add(status);
        layout.Controls.Add(actions, 0, 4);

        cursorTimer.Interval = 20;
        cursorTimer.Tick += (sender, args) => {
            int step = (cursorOffset + (int)(time.Elapsed.TotalSeconds * playingBpm / 15.0)) % Steps;
            if (step == activeStep) return;
            int previous = activeStep;
            activeStep = step;
            if (previous >= 0) PaintColumn(previous);
            PaintColumn(step);
        };
        refreshAudio.Interval = 120;
        refreshAudio.Tick += (sender, args) => {
            refreshAudio.Stop();
            if (playing) StartLoop();
        };
        ShowTrack(0);
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
        var song = new bool[3, 8, Steps];
        for (int track = 0; track < demo.Length; track++)
            for (int row = 0; row < demo[track].Length; row++)
                foreach (int step in demo[track][row])
                    song[track, row, step] = true;
        return song;
    }

    private void ShowTrack(int track)
    {
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
                    UseVisualStyleBackColor = false
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
        headers[step].ForeColor = step == activeStep ? Lime : Color.Gainsboro;
        for (int row = 0; row < cells.GetLength(0); row++)
        {
            var button = (CheckBoxCell)cells[row, step];
            button.IsOn = pattern[selected, row, step];
            button.BackColor = button.IsOn ? TrackColors[selected] : muted;
            button.FlatAppearance.BorderColor = step == activeStep ? Color.White : Color.FromArgb(88, 111, 95);
            button.FlatAppearance.BorderSize = step == activeStep ? 2 : 1;
        }
    }

    private void EditCell(int row, int step)
    {
        pattern[selected, row, step] = !pattern[selected, row, step];
        PaintColumn(step);
        if (playing)
        {
            QueueAudioRefresh();
        }
        else if (pattern[selected, row, step])
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
            status.Text = "16 步 · 4 拍循环";
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
            int firstStep = playing && activeStep >= 0 ? activeStep : 0;
            PlayBytes(Synth.Render(pattern, bpm.Value, firstStep), true);
            playing = true;
            playingBpm = bpm.Value;
            cursorOffset = firstStep;
            time.Restart();
            cursorTimer.Start();
            play.Text = "■  停止";
            status.Text = "正在循环播放";
        }
        catch (Exception)
        {
            StopLoop();
            status.Text = "无法播放，请检查 Windows 音频输出";
        }
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
        int previous = activeStep;
        activeStep = -1;
        if (previous >= 0) PaintColumn(previous);
        play.Text = "▶  播放";
    }

    private void ExportSong()
    {
        using (var dialog = new SaveFileDialog {
            Filter = "WAV 音频 (*.wav)|*.wav", DefaultExt = "wav", FileName = "chiploop.wav",
            Title = "导出一轮 8bit 音乐"
        })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllBytes(dialog.FileName, Synth.Render(pattern, bpm.Value, 0));
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

    private sealed class CheckBoxCell : Button
    {
        internal bool IsOn { get; set; }

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

internal static class Synth
{
    private const int Rate = 44100;
    private const double Tau = Math.PI * 2;
    private static readonly int[,] Midi = {
        { 72,71,69,67,65,64,62,60 },
        { 48,47,45,43,41,40,38,36 }
    };

    internal static byte[] Render(bool[,,] song, int bpm, int firstStep)
    {
        double stepSamples = Rate * 15.0 / bpm;
        var mix = new double[(int)Math.Round(stepSamples * 16)];
        for (int step = 0; step < 16; step++)
        {
            int start = (int)Math.Round(stepSamples * step);
            int end = (int)Math.Round(stepSamples * (step + 1));
            for (int track = 0; track < 3; track++)
                for (int row = 0; row < (track == 2 ? 3 : 8); row++)
                    if (song[track, row, (step + firstStep) % 16])
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


