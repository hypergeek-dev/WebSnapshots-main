// MainForm.cs (FULL FILE)
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace WebSnapshots;

public sealed class MainForm : Form
{
    private readonly ListBox _list = new();
    private readonly TextBox _paste = new();
    private readonly NumericUpDown _depth = new();
    private readonly TextBox _out = new();
    private readonly Button _browse = new();
    private readonly CheckBox _landingOnly = new();
    private readonly CheckBox _quickPreview = new();
    private readonly NumericUpDown _previewPagesPerDepth = new();
    private readonly NumericUpDown _previewChildrenPerPage = new();
    private readonly NumericUpDown _previewSeconds = new();

    private readonly Button _run = new();
    private readonly Button _pause = new();
    private readonly Button _stop = new();

    private readonly Button _addPaste = new();
    private readonly Button _remove = new();
    private readonly Button _clear = new();

    private readonly Button _clearLog = new();
    private readonly TextBox _log = new();

    private CancellationTokenSource? _cts;
    private PauseController? _pauseCtl;
    private volatile bool _running;

    // Log batching (prevents "missing" lines under heavy output)
    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly System.Windows.Forms.Timer _logTimer = new();

    // Console capture
    private TextWriter? _originalConsoleOut;
    private TextWriter? _originalConsoleErr;

    // Auto-scroll behaviour:
    // - default follow = true
    // - if user scrolls up, follow becomes false
    // - if user scrolls back to bottom, follow becomes true
    private volatile bool _followTail = true;
    private bool _suppressFollowCheck;

    // Prevent textbox growing forever
    private const int MAX_LOG_CHARS = 2_000_000;

    public MainForm()
    {
        Text = "WebSnapshots";
        Width = 1100;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(12)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        Controls.Add(root);

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 9
        };

        // Make the sites list the expanding row
        for (int i = 0; i < left.RowCount; i++)
            left.RowStyles.Add(new RowStyle(i == 1 ? SizeType.Percent : SizeType.AutoSize, i == 1 ? 100 : 0));

        root.Controls.Add(left, 0, 0);

        var title = new Label { Text = "Sites", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        left.Controls.Add(title);

        _list.Dock = DockStyle.Fill;
        left.Controls.Add(_list);

        var rowBtn = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        _remove.Text = "Remove selected";
        _clear.Text = "Clear";
        rowBtn.Controls.Add(_remove);
        rowBtn.Controls.Add(_clear);
        left.Controls.Add(rowBtn);

        var pasteLabel = new Label { Text = "Add sites (one per line)", AutoSize = true };
        left.Controls.Add(pasteLabel);

        _paste.Multiline = true;
        _paste.ScrollBars = ScrollBars.Vertical;
        _paste.Height = 140;
        _paste.Width = 360;
        left.Controls.Add(_paste);

        var rowPaste = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        _addPaste.Text = "➕ Add";
        rowPaste.Controls.Add(_addPaste);
        left.Controls.Add(rowPaste);

        // RIGHT: settings + log
        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 9
        };

        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // Settings title
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // Depth
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // Landing only checkbox
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // Quick preview
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // Output
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // Note
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // Log label row
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // Log (expands)
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // Buttons row
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // (spare)

        root.Controls.Add(right, 1, 0);

        var settingsTitle = new Label { Text = "Settings", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        right.Controls.Add(settingsTitle);

        var rowDepth = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        rowDepth.Controls.Add(new Label { Text = "Depth:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        _depth.Minimum = 0;
        _depth.Maximum = 30;
        _depth.Value = 3;
        _depth.Width = 80;
        rowDepth.Controls.Add(_depth);
        right.Controls.Add(rowDepth);

        // Landing page only checkbox
        var rowLanding = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        _landingOnly.Text = "Landing page only (For testing)";
        _landingOnly.AutoSize = true;
        _landingOnly.Padding = new Padding(0, 4, 0, 4);
        rowLanding.Controls.Add(_landingOnly);
        right.Controls.Add(rowLanding);

        // Quick preview mode (fast tree test)
        var rowPreview = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        _quickPreview.Text = "Quick preview (fast tree test)";
        _quickPreview.AutoSize = true;
        _quickPreview.Padding = new Padding(0, 4, 8, 4);
        rowPreview.Controls.Add(_quickPreview);

        rowPreview.Controls.Add(new Label { Text = "Pages/depth:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        _previewPagesPerDepth.Minimum = 1;
        _previewPagesPerDepth.Maximum = 500;
        _previewPagesPerDepth.Value = 25;
        _previewPagesPerDepth.Width = 70;
        rowPreview.Controls.Add(_previewPagesPerDepth);

        rowPreview.Controls.Add(new Label { Text = "Children/page:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        _previewChildrenPerPage.Minimum = 1;
        _previewChildrenPerPage.Maximum = 500;
        _previewChildrenPerPage.Value = 18;
        _previewChildrenPerPage.Width = 70;
        rowPreview.Controls.Add(_previewChildrenPerPage);

        rowPreview.Controls.Add(new Label { Text = "Seconds:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        _previewSeconds.Minimum = 10;
        _previewSeconds.Maximum = 3600;
        _previewSeconds.Value = 90;
        _previewSeconds.Width = 70;
        rowPreview.Controls.Add(_previewSeconds);

        right.Controls.Add(rowPreview);

        var rowOut = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        rowOut.Controls.Add(new Label { Text = "Output folder:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        _out.Width = 360;
        _out.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "WebSnapshotsOutput");
        _browse.Text = "Browse…";
        rowOut.Controls.Add(_out);
        rowOut.Controls.Add(_browse);
        right.Controls.Add(rowOut);

        var importantRow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 4, 24, 0)
        };

        importantRow.Controls.Add(new Label
        {
            Text = "Important:",
            AutoSize = true,
            ForeColor = Color.DarkRed,
            Font = new Font(Font, FontStyle.Bold)
        });

        importantRow.Controls.Add(new Label
        {
            Text =
                " Use the same output folder as previous runs\r\n" +
                "to increment and extend an existing archive.",
            AutoSize = true,
            Margin = new Padding(-78, 0, 0, 0)
        });

        right.Controls.Add(importantRow);

        // Log label row (with Clear log button)
        var rowLogLabel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        rowLogLabel.Controls.Add(new Label { Text = "Log", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        _clearLog.Text = "Clear log";
        rowLogLabel.Controls.Add(_clearLog);
        right.Controls.Add(rowLogLabel);

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Dock = DockStyle.Fill;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.Font = new Font(FontFamily.GenericMonospace, 9f);
        right.Controls.Add(_log);

        var rowRun = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        _run.Text = "Run";
        _pause.Text = "Pause";
        _stop.Text = "Stop";
        _pause.Enabled = false;
        _stop.Enabled = false;
        rowRun.Controls.Add(_run);
        rowRun.Controls.Add(_pause);
        rowRun.Controls.Add(_stop);
        right.Controls.Add(rowRun);

        // Events
        _addPaste.Click += (_, __) => AddFromPaste();
        _remove.Click += (_, __) => RemoveSelected();
        _clear.Click += (_, __) => _list.Items.Clear();

        _browse.Click += (_, __) =>
        {
            using var dlg = new FolderBrowserDialog();
            dlg.SelectedPath = _out.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _out.Text = dlg.SelectedPath;
        };

        // Mutual exclusivity: landing-only vs quick preview
        _landingOnly.CheckedChanged += (_, __) =>
        {
            if (_landingOnly.Checked)
                _quickPreview.Checked = false;
        };

        _quickPreview.CheckedChanged += (_, __) =>
        {
            if (_quickPreview.Checked)
                _landingOnly.Checked = false;

            _previewPagesPerDepth.Enabled = _quickPreview.Checked;
            _previewChildrenPerPage.Enabled = _quickPreview.Checked;
            _previewSeconds.Enabled = _quickPreview.Checked;
        };

        // Init enabled state
        _previewPagesPerDepth.Enabled = false;
        _previewChildrenPerPage.Enabled = false;
        _previewSeconds.Enabled = false;

        _clearLog.Click += (_, __) =>
        {
            _log.Clear();
            _followTail = true;

            while (_logQueue.TryDequeue(out var ignored)) { }
        };

        // Landing only checkbox interaction with depth
        _landingOnly.CheckedChanged += (_, __) =>
        {
            if (_landingOnly.Checked)
            {
                _depth.Enabled = false;
                _depth.Value = 0;
            }
            else
            {
                _depth.Enabled = true;
                if (_depth.Value == 0) _depth.Value = 3;
            }
        };

        // Convenience: Ctrl+Enter in paste box = Add
        _paste.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && e.Control)
            {
                e.SuppressKeyPress = true;
                AddFromPaste();
            }
        };

        _run.Click += async (_, __) => await RunAsync();
        _pause.Click += (_, __) => TogglePause();
        _stop.Click += (_, __) => StopRun();

        // Auto-scroll follow logic: detect user scroll away from bottom
        _log.MouseWheel += (_, __) => UpdateFollowFromScrollPosition();
        _log.KeyDown += (_, __) => UpdateFollowFromScrollPosition();
        _log.MouseDown += (_, __) => UpdateFollowFromScrollPosition();

        FormClosing += (_, __) =>
        {
            try { _cts?.Cancel(); } catch { }
            try { _pauseCtl?.Resume(); } catch { }
            RestoreConsoleCapture();
        };

        // Timer to flush queued log lines in batches (prevents UI overload)
        _logTimer.Interval = 80;
        _logTimer.Tick += (_, __) => FlushLogQueueToUi();
        _logTimer.Start();

        // Capture Console output into GUI (requires UiTextWriter.cs)
        SetupConsoleCapture();
    }

    private void AddFromPaste()
    {
        var raw = _paste.Text ?? "";

        var lines = raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(x => (x ?? "").Trim())
                       .Where(x => !string.IsNullOrWhiteSpace(x))
                       .ToArray();

        if (lines.Length == 0) return;

        AddLines(lines);
        _paste.Clear();
    }

    private void AddLines(IEnumerable<string> lines)
    {
        var existing = new HashSet<string>(_list.Items.Cast<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var x in lines)
        {
            var t = x.Trim();
            if (t.StartsWith("#")) continue;
            if (!existing.Add(t)) continue;
            _list.Items.Add(t);
        }
    }

    private void RemoveSelected()
    {
        var sel = _list.SelectedItems.Cast<object>().ToList();
        foreach (var it in sel)
            _list.Items.Remove(it);
    }

    private async Task RunAsync()
    {
        if (_running) return;

        if (_list.Items.Count == 0)
        {
            MessageBox.Show(this, "Add at least one site.", "WebSnapshots", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var cfg = new SnapshotConfig
        {
            MaxDepth = (int)_depth.Value,
            OutputBaseDir = _out.Text,
            UseDatedOutput = true,
            LandingOnly = _landingOnly.Checked,
            QuickPreview = _quickPreview.Checked,
            PreviewMaxPagesPerDepth = (int)_previewPagesPerDepth.Value,
            PreviewMaxChildrenPerPage = (int)_previewChildrenPerPage.Value,
            PreviewMaxTotalSeconds = (int)_previewSeconds.Value
        };
        cfg.Validate();

        var urls = _list.Items.Cast<string>().ToList();

        _cts = new CancellationTokenSource();
        _pauseCtl = new PauseController();
        _running = true;

        _followTail = true;
        SetUiRunning(true);

        _log.Clear();
        UiLog("[RUN] Starting…");

        if (cfg.LandingOnly)
            UiLog("[MODE] Landing page only - will capture 1 page per site");

        if (cfg.QuickPreview)
            UiLog($"[MODE] Quick preview - depth={cfg.MaxDepth} pages/depth={cfg.PreviewMaxPagesPerDepth} children/page={cfg.PreviewMaxChildrenPerPage} cap={cfg.PreviewMaxTotalSeconds}s");

        try
        {
            var token = _cts.Token;
            var pause = _pauseCtl;

            await Task.Run(async () =>
            {
                await Program.RunAsync(cfg, urls, UiLogThreadSafe, token, pause!);
            }, token);

            UiLog("[RUN] Finished.");

            var baseIndex = Path.Combine(Path.GetFullPath(cfg.OutputBaseDir), "index.htm");

            // Try to open the viewer for the first site directly
            string openPath = baseIndex;
            if (urls.Count > 0)
            {
                try
                {
                    var firstHost = new Uri(Utils.EnsureScheme(urls[0])).Host;
                    var muni = Utils.HostToMunicipality(firstHost);
                    var muniDir = Path.Combine(Path.GetFullPath(cfg.OutputBaseDir), muni);
                    if (Directory.Exists(muniDir))
                    {
                        var latest = Directory.GetDirectories(muniDir)
                            .OrderByDescending(d => d)
                            .FirstOrDefault();
                        if (latest != null)
                        {
                            var candidate = Path.Combine(latest, "index.htm");
                            if (File.Exists(candidate))
                                openPath = candidate;
                        }
                    }
                }
                catch { }
            }

            UiLog("[OPEN] " + openPath);

            var res = MessageBox.Show(this, "Done.\nOpen result now?", "WebSnapshots",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (res == DialogResult.Yes && File.Exists(openPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = openPath,
                    UseShellExecute = true
                });
            }
        }
        catch (OperationCanceledException)
        {
            UiLog("[RUN] Cancelled.");
        }
        catch (Exception ex)
        {
            UiLog("[ERROR] " + ex.Message);
            MessageBox.Show(this, ex.ToString(), "WebSnapshots Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _pauseCtl = null;
            _running = false;

            SetUiRunning(false);
        }
    }

    private void TogglePause()
    {
        if (!_running || _pauseCtl == null) return;

        if (_pauseCtl.IsPaused)
        {
            _pauseCtl.Resume();
            _pause.Text = "Pause";
            UiLog("[RUN] Resumed.");
        }
        else
        {
            _pauseCtl.Pause();
            _pause.Text = "Resume";
            UiLog("[RUN] Paused.");
        }
    }

    private void StopRun()
    {
        if (!_running) return;

        UiLog("[RUN] Stopping…");
        try { _cts?.Cancel(); } catch { }
        try { _pauseCtl?.Resume(); } catch { }
    }

    private void SetUiRunning(bool running)
    {
        _run.Enabled = !running;
        _pause.Enabled = running;
        _stop.Enabled = running;

        _browse.Enabled = !running;
        _addPaste.Enabled = !running;
        _remove.Enabled = !running;
        _clear.Enabled = !running;
        _landingOnly.Enabled = !running;
        _depth.Enabled = !running && !_landingOnly.Checked;

        _pause.Text = "Pause";
    }

    private void UiLog(string s) => _logQueue.Enqueue(s);
    private void UiLogThreadSafe(string s) => _logQueue.Enqueue(s);

    private void FlushLogQueueToUi()
    {
        if (_running == false && _logQueue.IsEmpty) return;

        if (_suppressFollowCheck) return;
        _suppressFollowCheck = true;

        try
        {
            var sb = new StringBuilder();
            while (_logQueue.TryDequeue(out var line))
                sb.AppendLine(line);

            if (sb.Length == 0) return;

            if (_log.TextLength > MAX_LOG_CHARS)
                _log.Text = _log.Text[^MAX_LOG_CHARS..];

            _log.AppendText(sb.ToString());

            if (_followTail)
            {
                _log.SelectionStart = _log.TextLength;
                _log.ScrollToCaret();
            }
        }
        finally
        {
            _suppressFollowCheck = false;
        }
    }

    private void UpdateFollowFromScrollPosition()
    {
        try
        {
            var lastVisibleLine = _log.GetLineFromCharIndex(_log.GetCharIndexFromPosition(new Point(2, _log.Height - 2)));
            var totalLines = _log.Lines.Length;
            _followTail = (totalLines - lastVisibleLine) <= 2;
        }
        catch
        {
            _followTail = true;
        }
    }

    private void SetupConsoleCapture()
    {
        try
        {
            _originalConsoleOut = Console.Out;
            _originalConsoleErr = Console.Error;

            Console.SetOut(new UiTextWriter(s => UiLogThreadSafe(s)));
            Console.SetError(new UiTextWriter(s => UiLogThreadSafe(s)));
        }
        catch
        {
            // ignore
        }
    }

    private void RestoreConsoleCapture()
    {
        try
        {
            if (_originalConsoleOut != null) Console.SetOut(_originalConsoleOut);
            if (_originalConsoleErr != null) Console.SetError(_originalConsoleErr);
        }
        catch
        {
            // ignore
        }
    }
}