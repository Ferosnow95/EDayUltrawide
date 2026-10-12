// EDayUltrawide - the window: independent cutscene and gameplay FOV, game launch, launch hook.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace EDayUltrawide
{
    internal sealed class MainForm : Form
    {
        readonly bool launchedByXbox;      // started by the Xbox app: engine runs here, window starts minimised and closes with the game
        bool loading;                       // true while controls are filled from settings (no write-back)

        Label lblStatus, lblDetail, lblCutInfo, lblCutLast, lblGpInfo, lblHook, lblHookHint;
        Control[] gated;
        TableLayoutPanel root;
        CheckBox chkEnabled, chkHorPlus, chkGameplay, chkOnline;
        TrackBar trkCut, trkGp;
        NumericUpDown numCut, numGp;
        Button btnPlay, btnCutReset, btnGpReset, btnHookOn, btnHookOff, btnLog, btnFolder, btnGame;
        TextBox txtLog;
        readonly Timer refresh = new Timer { Interval = 400 };
        readonly Timer saveDebounce = new Timer { Interval = 120 };
        int logVersion = -1;
        long hookCheckedAt;
        bool hookBusy;

        static readonly Color Good = Color.FromArgb(34, 139, 34), Warn = Color.FromArgb(196, 120, 0), Bad = Color.FromArgb(190, 40, 40), Dim = Color.FromArgb(105, 105, 105);

        public MainForm(bool xbox)
        {
            launchedByXbox = xbox;
            Text = "E-Day Ultrawide";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(680, 800);
            MinimumSize = new Size(640, 700);
            MaximizeBox = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Build();
            FitWidth();
            LoadFromSettings();
            Settings.Changed += OnSettingsChanged;
            refresh.Tick += (s, e) => Refresh_();
            saveDebounce.Tick += (s, e) => { saveDebounce.Stop(); Save(); };
            Shown += (s, e) => { FitWidth(); RefreshHook(); Refresh_(); refresh.Start(); };
            if (launchedByXbox) { WindowState = FormWindowState.Minimized; ShowInTaskbar = true; }
        }

        /// <summary>The window can never be narrower than its content (any DPI / font scale): width follows the layout's preferred size.</summary>
        void FitWidth()
        {
            try
            {
                int need = root.GetPreferredSize(Size.Empty).Width + root.Padding.Horizontal + SystemInformation.VerticalScrollBarWidth + 6;
                var min = SizeFromClientSize(new Size(need, 600));
                MinimumSize = new Size(Math.Max(MinimumSize.Width, min.Width), MinimumSize.Height);
                if (ClientSize.Width < need) ClientSize = new Size(need, ClientSize.Height);
            }
            catch { }
        }

        protected override bool ShowWithoutActivation { get { return launchedByXbox; } }

        // ------------------------------------------------------------------ layout

        void Build()
        {
            root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(12), AutoScroll = true };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // status
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // step 1: hook
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // master switch
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // cutscenes
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // gameplay
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // game
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // log
            Controls.Add(root);

            // status
            var st = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
            lblStatus = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 12f), Text = "Starting...", Margin = new Padding(0, 0, 0, 2) };
            lblDetail = new Label { AutoSize = true, ForeColor = Dim, Text = "", MaximumSize = new Size(560, 0) };
            st.Controls.Add(lblStatus); st.Controls.Add(lblDetail);
            root.Controls.Add(st);

            // step 1: install the hook
            var gh = Group("Step 1 - install the Xbox launch hook (once)");
            var th = Grid(4);
            lblHook = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = "Xbox launch hook: checking...", Margin = new Padding(0, 8, 8, 0) };
            th.Controls.Add(lblHook, 0, 0); th.SetColumnSpan(lblHook, 2);
            btnHookOn = new Button { AutoSize = true, Text = "Install hook" };
            btnHookOn.Click += (s, e) => HookAsync(true);
            btnHookOff = new Button { AutoSize = true, Text = "Remove hook" };
            btnHookOff.Click += (s, e) => HookAsync(false);
            th.Controls.Add(btnHookOn, 2, 0); th.Controls.Add(btnHookOff, 3, 0);
            btnGame = new Button { AutoSize = true, Text = "Choose game folder..." };
            btnGame.Click += (s, e) => ChooseGameFolder();
            th.Controls.Add(btnGame, 0, 1);
            lblHookHint = new Label { AutoSize = true, ForeColor = Warn, MaximumSize = new Size(560, 0), Margin = new Padding(0, 6, 0, 0),
                Text = "The settings below stay locked until the hook is installed: the Game Pass build only runs the fix when the Xbox app starts it." };
            th.Controls.Add(lblHookHint, 0, 2); th.SetColumnSpan(lblHookHint, 4);
            gh.Controls.Add(th); root.Controls.Add(gh);

            var g2 = Group("Step 2 - Ultrawide fix");
            chkEnabled = new CheckBox { AutoSize = true, Text = "Ultrawide fix on (removes the black bars in cutscenes; F8 in game toggles)" };
            chkEnabled.CheckedChanged += (s, e) => Edit(c => c.Enabled = chkEnabled.Checked);
            var t2 = Grid(3); // same grid and margins as the other steps, so the checkboxes line up exactly
            t2.Controls.Add(chkEnabled, 0, 0); t2.SetColumnSpan(chkEnabled, 3);
            g2.Controls.Add(t2); root.Controls.Add(g2);

            // cutscenes
            var gc = Group("Step 3 - Cutscenes");
            var tc = Grid(3);
            chkHorPlus = new CheckBox { AutoSize = true, Text = "Keep the 16:9 vertical framing (Hor+, recommended)" };
            chkHorPlus.CheckedChanged += (s, e) => Edit(c => c.CutsceneHorPlus = chkHorPlus.Checked);
            tc.Controls.Add(chkHorPlus, 0, 0); tc.SetColumnSpan(chkHorPlus, 3);
            tc.Controls.Add(new Label { AutoSize = true, Text = "Cutscene FOV", Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 0) }, 0, 1);
            trkCut = new TrackBar { Minimum = (int)Config.CutscenePercentMin, Maximum = (int)Config.CutscenePercentMax, TickFrequency = 10, LargeChange = 10, SmallChange = 1, Dock = DockStyle.Fill, Value = 100 };
            trkCut.ValueChanged += (s, e) => { if (loading) return; loading = true; numCut.Value = trkCut.Value; loading = false; Edit(c => c.CutsceneFovPercent = trkCut.Value); };
            tc.Controls.Add(trkCut, 1, 1);
            numCut = new NumericUpDown { Minimum = (decimal)Config.CutscenePercentMin, Maximum = (decimal)Config.CutscenePercentMax, Value = 100, Width = 58, Anchor = AnchorStyles.Left };
            numCut.ValueChanged += (s, e) => { if (loading) return; loading = true; trkCut.Value = (int)numCut.Value; loading = false; Edit(c => c.CutsceneFovPercent = (float)numCut.Value); };
            var cutRight = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            cutRight.Controls.Add(numCut); cutRight.Controls.Add(new Label { AutoSize = true, Text = "%", Margin = new Padding(2, 5, 0, 0) });
            tc.Controls.Add(cutRight, 2, 1);
            lblCutInfo = new Label { AutoSize = true, ForeColor = Dim, MaximumSize = new Size(440, 0) };
            tc.Controls.Add(lblCutInfo, 0, 2); tc.SetColumnSpan(lblCutInfo, 3);
            lblCutLast = new Label { AutoSize = true, ForeColor = Dim, Text = "" };
            tc.Controls.Add(lblCutLast, 0, 3); tc.SetColumnSpan(lblCutLast, 2);
            btnCutReset = new Button { AutoSize = true, Text = "Reset to 100%", Anchor = AnchorStyles.Right };
            btnCutReset.Click += (s, e) => { trkCut.Value = 100; };
            tc.Controls.Add(btnCutReset, 2, 3);
            gc.Controls.Add(tc); root.Controls.Add(gc);

            // gameplay
            var gg = Group("Step 4 - Gameplay");
            var tg = Grid(3);
            chkGameplay = new CheckBox { AutoSize = true, Text = "Override the gameplay FOV" };
            chkGameplay.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                if (chkGameplay.Checked)
                {
                    float seed = Live.GameplayFinal >= 60f && Live.GameplayFinal <= 150f ? (float)Math.Round(Live.GameplayFinal) : 100f;
                    loading = true; trkGp.Value = (int)Math.Max(60, Math.Min(150, seed)); numGp.Value = trkGp.Value; loading = false;
                }
                SyncGameplayEnabled();
                Edit(c => c.GameplayFov = chkGameplay.Checked ? (float)numGp.Value : 0f);
            };
            tg.Controls.Add(chkGameplay, 0, 0); tg.SetColumnSpan(chkGameplay, 3);
            tg.Controls.Add(new Label { AutoSize = true, Text = "Gameplay FOV", Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 0) }, 0, 1);
            trkGp = new TrackBar { Minimum = 60, Maximum = 150, TickFrequency = 10, LargeChange = 10, SmallChange = 1, Dock = DockStyle.Fill, Value = 100 };
            trkGp.ValueChanged += (s, e) => { if (loading) return; loading = true; numGp.Value = trkGp.Value; loading = false; Edit(c => c.GameplayFov = chkGameplay.Checked ? trkGp.Value : 0f); };
            tg.Controls.Add(trkGp, 1, 1);
            numGp = new NumericUpDown { Minimum = 60, Maximum = 150, Value = 100, Width = 58, Anchor = AnchorStyles.Left };
            numGp.ValueChanged += (s, e) => { if (loading) return; loading = true; trkGp.Value = (int)numGp.Value; loading = false; Edit(c => c.GameplayFov = chkGameplay.Checked ? (float)numGp.Value : 0f); };
            var gpRight = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            gpRight.Controls.Add(numGp); gpRight.Controls.Add(new Label { AutoSize = true, Text = "°", Margin = new Padding(2, 5, 0, 0) });
            tg.Controls.Add(gpRight, 2, 1);
            lblGpInfo = new Label { AutoSize = true, ForeColor = Dim, MaximumSize = new Size(440, 0) };
            tg.Controls.Add(lblGpInfo, 0, 2); tg.SetColumnSpan(lblGpInfo, 2);
            btnGpReset = new Button { AutoSize = true, Text = "Use the game's own", Anchor = AnchorStyles.Right };
            btnGpReset.Click += (s, e) => { chkGameplay.Checked = false; };
            tg.Controls.Add(btnGpReset, 2, 2);
            gg.Controls.Add(tg); root.Controls.Add(gg);

            // game
            var gm = Group("Play");
            var tm = Grid(4);
            btnPlay = new Button { AutoSize = true, Text = "Start the game (Xbox app)", Padding = new Padding(8, 3, 8, 3) };
            btnPlay.Click += (s, e) => Play();
            tm.Controls.Add(btnPlay, 0, 0); tm.SetColumnSpan(btnPlay, 2);
            chkOnline = new CheckBox { AutoSize = true, Text = "Always start online (anti-cheat on, no fix)", Anchor = AnchorStyles.Left };
            chkOnline.CheckedChanged += (s, e) => Edit(c => c.OnlineMode = chkOnline.Checked);
            tm.Controls.Add(chkOnline, 2, 0); tm.SetColumnSpan(chkOnline, 2);
            btnLog = new Button { AutoSize = true, Text = "Open log" };
            btnLog.Click += (s, e) => OpenPath(Path.Combine(Program.DataDir, "EDayUltrawide.log"));
            btnFolder = new Button { AutoSize = true, Text = "Open folder" };
            btnFolder.Click += (s, e) => OpenPath(Program.DataDir);
            tm.Controls.Add(btnLog, 2, 1); tm.Controls.Add(btnFolder, 3, 1);
            gm.Controls.Add(tm); root.Controls.Add(gm);
            gated = new Control[] { g2, gc, gg };

            txtLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 8.5f), BackColor = SystemColors.Window, Margin = new Padding(0, 8, 0, 0), WordWrap = true };
            root.Controls.Add(txtLog);
        }

        static GroupBox Group(string title)
        {
            return new GroupBox { Text = title, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 6, 0, 0) };
        }

        static TableLayoutPanel Grid(int cols)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = cols };
            if (cols == 3) { t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f)); t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); }
            else for (int i = 0; i < cols; i++) t.ColumnStyles.Add(new ColumnStyle(i == 1 ? SizeType.Percent : SizeType.AutoSize, i == 1 ? 100f : 0f));
            return t;
        }

        // ------------------------------------------------------------------ settings <-> controls

        void LoadFromSettings()
        {
            var c = Settings.Current;
            loading = true;
            try
            {
                chkEnabled.Checked = c.Enabled;
                chkHorPlus.Checked = c.CutsceneHorPlus;
                int pc = (int)Math.Round(c.CutsceneFovPercent);
                trkCut.Value = Math.Max(trkCut.Minimum, Math.Min(trkCut.Maximum, pc)); numCut.Value = trkCut.Value;
                chkGameplay.Checked = c.GameplayFov > 0f;
                float g = c.GameplayFov > 0f ? c.GameplayFov : (trkGp.Value);
                trkGp.Value = (int)Math.Max(60, Math.Min(150, Math.Round(g))); numGp.Value = trkGp.Value;
                chkOnline.Checked = c.OnlineMode;
            }
            finally { loading = false; }
            SyncGameplayEnabled();
        }

        void SyncGameplayEnabled()
        {
            bool avail = GameplayKnownUnavailable() == false;
            trkGp.Enabled = numGp.Enabled = chkGameplay.Checked && avail;
            chkGameplay.Enabled = avail;
        }

        // true only when the engine is running here and has proven the control is not available in this build
        static bool GameplayKnownUnavailable() { return Live.EngineRunning && Live.GameAttached && Live.Phase == "Fix active" && !Live.GameplayAvailable; }

        void OnSettingsChanged()
        {
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke((Action)(() => { if (!saveDebounce.Enabled) LoadFromSettings(); })); } catch { }
        }

        Action<Config> pendingEdit;

        void Edit(Action<Config> change)
        {
            if (loading) return;
            var prev = pendingEdit;
            pendingEdit = c => { if (prev != null) prev(c); change(c); };
            saveDebounce.Stop(); saveDebounce.Start();
        }

        void Save()
        {
            var edit = pendingEdit; pendingEdit = null;
            if (edit == null) return;
            var c = Settings.Current.Clone();
            edit(c);
            Settings.Update(c);
        }

        // ------------------------------------------------------------------ actions

        void Play()
        {
            if (Live.EngineRunning) { return; }
            if (Program.FindGameRunning() && Live.GameAttached == false && Live.ReadRemote(Program.DataDir) != null)
            {
                MessageBox.Show(this, "The game is already running with the fix (see its launcher window).", "E-Day Ultrawide", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Save();
            Program.StartEngine(() => Program.RunAutoPublic());
        }

        void HookAsync(bool install)
        {
            if (hookBusy) return;
            hookBusy = true; btnHookOn.Enabled = btnHookOff.Enabled = false;
            lblHook.Text = "Xbox launch hook: " + (install ? "installing - approve the Windows prompt..." : "removing - approve the Windows prompt...");
            var th = new System.Threading.Thread(() =>
            {
                string err = null; bool close = false;
                try { if (install) err = Program.InstallFromWindow(); else err = Program.RemoveFromWindow(out close); }
                catch (Exception e) { err = e.Message; Log.Line("ERROR: " + e.Message); }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        hookBusy = false;
                        if (err != null) MessageBox.Show(this, err, "E-Day Ultrawide", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        else if (close) { Close(); return; }
                        else if (install) MessageBox.Show(this, "Installed. Close this window, then press Play in the Xbox app (hold Shift for a normal online launch).\r\n\r\nThe tool now lives in the game folder - you can delete the downloaded copy.", "E-Day Ultrawide", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        else MessageBox.Show(this, "Removed. The Xbox app starts the game normally again.", "E-Day Ultrawide", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        RefreshHook();
                    }));
                }
                catch { }
            }) { IsBackground = true };
            th.Start();
        }

        void ChooseGameFolder()
        {
            using (var d = new FolderBrowserDialog { Description = "Select the game's Content folder (the one that contains MicrosoftGame.config), e.g. ...\\XboxGames\\Gears of War- E-Day\\Content" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!Program.SetGameFolder(d.SelectedPath)) { MessageBox.Show(this, "That is not the E-Day game folder (no MicrosoftGame.config and FairlightConcept\\Binaries\\WinGDK\\GoWEDay.exe).", "E-Day Ultrawide", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                RefreshHook();
            }
        }

        void RefreshHook()
        {
            bool installed;
            string text = Program.HookStatus(out installed);
            lblHook.Text = "Xbox launch hook: " + text;
            lblHook.ForeColor = installed ? Good : Dim;
            bool xbox = Program.IsXboxBuild();
            bool atHome = Program.RunningInstalled;
            btnGame.Visible = !xbox;
            bool unlocked = installed || launchedByXbox || Live.EngineRunning;
            foreach (var c in gated) c.Enabled = unlocked;
            lblHookHint.Visible = !unlocked;
            btnHookOn.Visible = btnHookOff.Visible = xbox;
            btnHookOn.Text = installed && !atHome ? "Update hook" : "Install hook";
            btnHookOn.Enabled = xbox && (!installed || !atHome) && !hookBusy;
            btnHookOff.Enabled = xbox && Program.AnythingInstalled && !hookBusy;
            btnPlay.Text = xbox ? "Start the game (Xbox app)" : "Start the game";
            hookCheckedAt = Stopwatch.GetTimestamp();
        }

        static void OpenPath(string p)
        {
            try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch (Exception e) { Log.Line("Could not open " + p + ": " + e.Message); }
        }

        // ------------------------------------------------------------------ live refresh

        void Refresh_()
        {
            var ci = CultureInfo.InvariantCulture;
            bool engine = Live.EngineRunning;
            bool running = Program.FindGameRunning();
            Dictionary<string, string> remote = engine ? null : Live.ReadRemote(Program.DataDir);
            bool attached = engine ? Live.GameAttached : remote != null && remote.ContainsKey("attached") && remote["attached"] == "1";
            bool fix = engine ? Live.FixActive : remote != null && remote.ContainsKey("fix") && remote["fix"] == "1";

            // status line
            string head; Color col; string detail = "";
            if (Live.Error != null && !engine) { head = "Problem: " + Live.Error; col = Bad; }
            else if (attached) { head = fix ? "Game running - fix active" : "Game running - fix off"; col = fix ? Good : Warn; }
            else if (engine) { head = Live.Phase; col = Warn; }
            else if (running) { head = "Game running without the fix"; col = Warn; detail = "Close it and start it with the button below (or Play in the Xbox app) to get the fix."; }
            else { head = "Game not running"; col = Dim; detail = "Press 'Start the game', or Play in the Xbox app. Changes you make here are saved and apply at once while the game runs."; }
            lblStatus.Text = head; lblStatus.ForeColor = col;

            float aspect = engine ? Live.Aspect : Parse(remote, "aspect");
            if (attached && aspect > 0f)
            {
                string d = "Screen " + aspect.ToString("0.00", ci) + ":1";
                if (engine) d += "   cinematic cap " + Live.CapApplied.ToString("0.00", ci);
                detail = d;
            }
            lblDetail.Text = detail;

            // cutscene info
            var cfg = Settings.Current;
            float ar = aspect > 0f ? aspect : ScreenAspect();
            float render = Math.Max(1f, ar);
            float kHor = cfg.CutsceneHorPlus ? HorPlus.K(render, cfg.CutsceneFovBasis) : 1f;
            float k = kHor * cfg.CutsceneFovPercent / 100f;
            lblCutInfo.Text = "100% = the same top-to-bottom view as 16:9 (nothing cropped). Higher shows more, lower zooms in. " +
                              "At " + ar.ToString("0.0", ci) + ":1 a 40° camera shot is shown as " + HorPlus.Widen(40f, k).ToString("0.0", ci) + "°.";
            bool hook = engine ? Live.CutsceneHookOn : (remote != null && remote.ContainsKey("cine") && remote["cine"] == "1");
            bool known = engine && Live.GameAttached && Live.Phase == "Fix active";
            chkHorPlus.Enabled = trkCut.Enabled = numCut.Enabled = btnCutReset.Enabled = !(known && !hook);
            float cin = engine ? Live.CineIn : Parse(remote, "cinein"), cout = engine ? Live.CineOut : Parse(remote, "cineout");
            long views = engine ? Live.CineViews : (long)Parse(remote, "cineviews");
            lblCutLast.Text = known && !hook ? "Cutscene FOV control isn't available in this game build (bars fix only)."
                            : views > 0 && cin > 0f ? "Last cutscene camera: " + cin.ToString("0.0", ci) + "° → " + cout.ToString("0.0", ci) + "°" : "Shows the last cutscene camera once one plays.";

            // gameplay info
            bool gpAvail = engine ? (!known || Live.GameplayAvailable) : (remote == null || !remote.ContainsKey("gpavail") || remote["gpavail"] == "1");
            int objs = engine ? Live.GameplayObjects : (int)Parse(remote, "gpobjs");
            float gbase = engine ? Live.GameplayBase : Parse(remote, "gpbase"), gfin = engine ? Live.GameplayFinal : Parse(remote, "gpfinal");
            if (!gpAvail) lblGpInfo.Text = "Gameplay FOV control isn't available in this game build.";
            else if (objs > 0) lblGpInfo.Text = "Now " + gfin.ToString("0", ci) + "° (base " + gbase.ToString("0", ci) + "°). The game's own slider covers 60–90°; this goes up to 150°.";
            else lblGpInfo.Text = chkGameplay.Checked && attached ? "Looking for the gameplay camera (it appears once you are in a level)..." : "The game's own slider covers 60–90°; this goes up to 150° and is applied while you play.";
            SyncGameplayEnabled();
            if (!gpAvail) { chkGameplay.Enabled = trkGp.Enabled = numGp.Enabled = false; }

            btnPlay.Enabled = !engine && !attached && !running;
            if (engine && !attached) btnPlay.Text = "Starting...";
            else if (!engine) btnPlay.Text = Program.IsXboxBuild() ? "Start the game (Xbox app)" : "Start the game";

            // log tail
            int v = Log.TailVersion;
            if (v != logVersion)
            {
                logVersion = v;
                txtLog.Text = string.Join(Environment.NewLine, Log.Tail());
                txtLog.SelectionStart = txtLog.TextLength; txtLog.ScrollToCaret();
            }
            if (Stopwatch.GetTimestamp() - hookCheckedAt > Stopwatch.Frequency * 5 && !hookBusy) RefreshHook();

            // launched by the Xbox app: the window goes away with the game
            if (launchedByXbox && Live.Exited && Live.Error == null && !engine) { refresh.Stop(); Program.StopEngine(); Close(); }
        }

        static float Parse(Dictionary<string, string> d, string key)
        {
            float f; return d != null && d.ContainsKey(key) && float.TryParse(d[key], NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : 0f;
        }

        static float ScreenAspect()
        {
            try { var b = Screen.PrimaryScreen.Bounds; return b.Height > 0 ? (float)b.Width / b.Height : 16f / 9f; } catch { return 16f / 9f; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (saveDebounce.Enabled) { saveDebounce.Stop(); Save(); }
            if (Live.EngineRunning && Live.GameAttached && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true; WindowState = FormWindowState.Minimized; // the fix lives in this process: keep it until the game closes
                return;
            }
            refresh.Stop();
            Program.StopEngine();
            base.OnFormClosing(e);
        }
    }
}
