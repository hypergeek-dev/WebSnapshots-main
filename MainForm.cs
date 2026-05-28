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
            RowCount = 7
        };

        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // Settings title
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // Depth
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // Output
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // Note
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // Log label row
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // Log (expands)
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // Buttons row

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

        var rowOut = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        rowOut.Controls.Add(new Label { Text = "Output folder:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        _out.Width = 360;
        _out.Text = Path.Combine(Directory.GetCurrentDirectory(), "output");
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

        _browse.Click += (_, __) => BrowseForOutputFolder();

        _clearLog.Click += (_, __) =>
        {
            _log.Clear();
            _followTail = true;

            while (_logQueue.TryDequeue(out var ignored)) { }
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

    private void BrowseForOutputFolder()
    {
        try
        {
            var selected = ShowLocalFolderPicker(GetExistingFolderForDialog(_out.Text));
            if (!string.IsNullOrWhiteSpace(selected))
                _out.Text = selected;
        }
        catch (Exception ex)
        {
            UiLog("[ERROR] Could not open folder browser: " + ex.Message);
            MessageBox.Show(
                this,
                "Could not open the folder browser.\n\n" + ex.Message,
                "WebSnapshots",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private string? ShowLocalFolderPicker(string initialPath)
    {
        using var form = new Form
        {
            Text = "Select output folder",
            StartPosition = FormStartPosition.CenterParent,
            Width = 720,
            Height = 520,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        form.Controls.Add(root);

        var pathBox = new TextBox { Dock = DockStyle.Fill, Text = initialPath };
        root.Controls.Add(pathBox);

        var navRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        var driveBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        var upButton = new Button { Text = "Up", Width = 70 };
        var refreshButton = new Button { Text = "Refresh", Width = 80 };
        var newFolderButton = new Button { Text = "New folder", Width = 100 };
        navRow.Controls.Add(driveBox);
        navRow.Controls.Add(upButton);
        navRow.Controls.Add(refreshButton);
        navRow.Controls.Add(newFolderButton);
        root.Controls.Add(navRow);

        var folderList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        root.Controls.Add(folderList);

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var selectButton = new Button { Text = "Select", Width = 90, DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        buttonRow.Controls.Add(selectButton);
        buttonRow.Controls.Add(cancelButton);
        root.Controls.Add(buttonRow);

        form.AcceptButton = selectButton;
        form.CancelButton = cancelButton;

        string currentPath = initialPath;

        void LoadDrives()
        {
            driveBox.Items.Clear();
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                driveBox.Items.Add(drive.RootDirectory.FullName);

            var currentRoot = Path.GetPathRoot(currentPath);
            if (!string.IsNullOrWhiteSpace(currentRoot) && driveBox.Items.Contains(currentRoot))
                driveBox.SelectedItem = currentRoot;
            else if (driveBox.Items.Count > 0)
                driveBox.SelectedIndex = 0;
        }

        void LoadFolder(string path)
        {
            try
            {
                path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
                Directory.CreateDirectory(path);

                currentPath = path;
                pathBox.Text = currentPath;
                folderList.Items.Clear();

                foreach (var dir in Directory.EnumerateDirectories(currentPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    folderList.Items.Add(Path.GetFileName(dir));
            }
            catch (Exception ex)
            {
                MessageBox.Show(form, ex.Message, "Cannot open folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        LoadDrives();
        LoadFolder(currentPath);

        driveBox.SelectedIndexChanged += (_, __) =>
        {
            if (driveBox.SelectedItem is string drive)
                LoadFolder(drive);
        };

        pathBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                LoadFolder(pathBox.Text);
            }
        };

        folderList.DoubleClick += (_, __) =>
        {
            if (folderList.SelectedItem is string name)
                LoadFolder(Path.Combine(currentPath, name));
        };

        upButton.Click += (_, __) =>
        {
            var parent = Directory.GetParent(currentPath);
            if (parent != null)
                LoadFolder(parent.FullName);
        };

        refreshButton.Click += (_, __) => LoadFolder(pathBox.Text);

        newFolderButton.Click += (_, __) =>
        {
            var baseName = "New folder";
            var candidate = Path.Combine(currentPath, baseName);
            var index = 2;
            while (Directory.Exists(candidate))
                candidate = Path.Combine(currentPath, $"{baseName} {index++}");

            Directory.CreateDirectory(candidate);
            LoadFolder(candidate);
        };

        selectButton.Click += (_, __) => LoadFolder(pathBox.Text);

        return form.ShowDialog(this) == DialogResult.OK ? currentPath : null;
    }

    private static string GetExistingFolderForDialog(string? path)
    {
        try
        {
            var current = string.IsNullOrWhiteSpace(path)
                ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                : Environment.ExpandEnvironmentVariables(path.Trim());

            if (!Path.IsPathRooted(current))
                current = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), current));

            if (Directory.Exists(current) && !IsCloudBackedPath(current))
                return current;

            var parent = Directory.GetParent(current);
            while (parent != null)
            {
                if (Directory.Exists(parent.FullName) && !IsCloudBackedPath(parent.FullName))
                    return parent.FullName;

                parent = parent.Parent;
            }
        }
        catch
        {
        }

        var localOutput = Path.Combine(Directory.GetCurrentDirectory(), "output");
        return Directory.Exists(localOutput) ? localOutput : Directory.GetCurrentDirectory();
    }

    private static bool IsCloudBackedPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
            var oneDriveCommercial = Environment.GetEnvironmentVariable("OneDriveCommercial");
            var oneDriveConsumer = Environment.GetEnvironmentVariable("OneDriveConsumer");

            return IsSameOrChild(fullPath, oneDrive)
                || IsSameOrChild(fullPath, oneDriveCommercial)
                || IsSameOrChild(fullPath, oneDriveConsumer);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameOrChild(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;

        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
        _depth.Enabled = !running;

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
