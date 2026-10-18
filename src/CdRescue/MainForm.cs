using System.Diagnostics;
using System.Security.Principal;
using CdRescue.Rescue;

namespace CdRescue;

internal sealed class MainForm : Form
{
    private sealed record DriveItem(char Letter, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed record SpeedItem(int KBps, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly ComboBox _drive = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _refresh = new() { Text = "Обновить", AutoSize = true };
    private readonly TextBox _image = new() { Dock = DockStyle.Fill };
    private readonly Button _browseImage = new() { Text = "Обзор…", AutoSize = true };
    private readonly TextBox _mapPath = new() { Dock = DockStyle.Fill };
    private readonly Button _browseMap = new() { Text = "Обзор…", AutoSize = true };
    private readonly NumericUpDown _retries = new() { Minimum = 0, Maximum = 100, Value = 5, Width = 60 };
    private readonly ComboBox _speed = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly Button _start = new() { Text = "Начать", AutoSize = true };
    private readonly Button _test = new() { Text = "Проверить диск", AutoSize = true };
    private readonly Button _stop = new() { Text = "Остановить", AutoSize = true, Enabled = false };
    private readonly Button _report = new() { Text = "Отчёт о повреждениях", AutoSize = true };
    private readonly Label _stage = new() { AutoSize = true, Text = "Выберите привод и файл образа." };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 18 };
    private readonly Label _stats = new() { AutoSize = true };
    private readonly Label _speedInfo = new() { AutoSize = true };
    private readonly ToolTip _tips = new();
    private readonly Label _cellInfo = new() { AutoSize = true, Margin = new Padding(12, 3, 3, 3) };
    private readonly SectorMapView _mapView = new() { Dock = DockStyle.Fill, MinimumSize = new Size(200, 120) };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9.5f), BackColor = SystemColors.Window,
    };

    private CancellationTokenSource? _cts;
    private bool _closeWhenStopped;
    private bool _mapEdited;
    // Скорость считаем по окну ~3 с: за один тик в 0,5 с значения слишком скачут.
    private readonly Queue<(TimeSpan At, long Sectors)> _rateWindow = new();
    private readonly Stopwatch _rateClock = new();

    public MainForm()
    {
        Text = "CdRescue - снятие образа с повреждённого CD";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(820, 640);
        MinimumSize = new Size(640, 520);

        _speed.Items.AddRange([
            new SpeedItem(0, "не менять"),
            new SpeedItem(176, "1x (176 кБ/с)"),
            new SpeedItem(353, "2x (353 кБ/с)"),
            new SpeedItem(706, "4x (706 кБ/с)"),
            new SpeedItem(1411, "8x (1411 кБ/с)"),
            new SpeedItem(2822, "16x (2822 кБ/с)"),
        ]);
        _speed.SelectedIndex = 0;

        BuildLayout();

        _refresh.Click += (_, _) => LoadDrives();
        _mapView.ScaleChanged += (_, _) => ShowCellSize();
        _drive.SelectedIndexChanged += (_, _) => SuggestImagePath();
        _image.TextChanged += (_, _) =>
        {
            if (!_mapEdited)
                _mapPath.Text = _image.Text.Length > 0 ? _image.Text + ".map" : "";
        };
        _mapPath.TextChanged += (_, _) => _mapEdited = _mapPath.Focused;
        _browseImage.Click += (_, _) => BrowseImage();
        _browseMap.Click += (_, _) => BrowseMap();
        _start.Click += async (_, _) => await StartAsync();
        _test.Click += async (_, _) => await TestAsync();
        _report.Click += async (_, _) => await ReportAsync(show: true);
        _stop.Click += (_, _) => Stop();

        LoadDrives();
        if (!IsElevated())
            AppendLog("Запущено без прав администратора. Если привод не откроется, программа предложит перезапуск.");
    }

    private void BuildLayout()
    {
        var settings = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(8) };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddRow(settings, "Привод:", _drive, _refresh);
        AddRow(settings, "Образ:", _image, _browseImage);
        AddRow(settings, "Карта:", _mapPath, _browseMap);

        var options = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        options.Controls.Add(Caption("Проходов повтора:"));
        options.Controls.Add(_retries);
        options.Controls.Add(Caption("Скорость чтения:"));
        options.Controls.Add(_speed);
        settings.Controls.Add(Caption("Параметры:"));
        settings.Controls.Add(options);
        settings.SetColumnSpan(options, 2);

        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        buttons.Controls.Add(_start);
        buttons.Controls.Add(_test);
        buttons.Controls.Add(_stop);
        buttons.Controls.Add(_report);
        settings.Controls.Add(new Label());
        settings.Controls.Add(buttons);
        settings.SetColumnSpan(buttons, 2);

        var status = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(8, 0, 8, 4) };
        status.Controls.Add(_stage);
        status.Controls.Add(_progress);
        status.Controls.Add(_stats);
        status.Controls.Add(_speedInfo);
        status.Controls.Add(Legend());

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
        };
        // До раскладки у SplitContainer размер по умолчанию, и большое значение не принимается.
        Load += (_, _) => split.SplitterDistance = split.Height * 55 / 100;
        split.Panel1.Padding = new Padding(8, 0, 8, 0);
        split.Panel2.Padding = new Padding(8, 0, 8, 8);
        split.Panel1.Controls.Add(_mapView);
        split.Panel2.Controls.Add(_log);

        Controls.Add(split);
        Controls.Add(status);
        Controls.Add(settings);
    }

    private static void AddRow(TableLayoutPanel t, string caption, Control field, Control button)
    {
        t.Controls.Add(Caption(caption));
        t.Controls.Add(field);
        t.Controls.Add(button);
    }

    private static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };

    private FlowLayoutPanel Legend()
    {
        var p = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        foreach (var (color, text, tip) in new[]
        {
            (SectorMapView.GoodColor, "прочитано", "Сектор прочитан и записан в образ."),
            (SectorMapView.SkippedColor, "пропущено", "Быстрый проход перескочил через сбойное место; второй проход дочитает его по секторам."),
            (SectorMapView.BadColor, "плохие", "Сектор не читается; в образе на его месте нули."),
            (SectorMapView.PartialColor, "частично", "Сектор собран из сырых чтений, но контрольная сумма не сошлась - возможны ошибки."),
            (SectorMapView.UntriedColor, "не читалось", "До сектора ещё не дошли."),
        })
        {
            var swatch = new Panel { BackColor = color, Size = new Size(12, 12), Margin = new Padding(3, 5, 3, 3) };
            var label = new Label { Text = text, AutoSize = true, Margin = new Padding(0, 3, 12, 3) };
            _tips.SetToolTip(swatch, tip);
            _tips.SetToolTip(label, tip);
            p.Controls.Add(swatch);
            p.Controls.Add(label);
        }
        p.Controls.Add(_cellInfo);
        return p;
    }

    private void LoadDrives()
    {
        char? selected = (_drive.SelectedItem as DriveItem)?.Letter;
        _drive.Items.Clear();

        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom))
        {
            char letter = d.Name[0];
            string text = $"{letter}:  нет диска или не читается";
            try
            {
                if (d.IsReady)
                    text = $"{letter}:  {d.VolumeLabel}  ({d.TotalSize / 1048576.0:F0} МБ)";
            }
            catch (IOException)
            {
                // Повреждённый диск может не отдать даже метку - читать будем всё равно.
            }
            _drive.Items.Add(new DriveItem(letter, text));
        }

        if (_drive.Items.Count == 0)
        {
            _stage.Text = "Оптические приводы не найдены.";
            return;
        }

        int i = _drive.Items.Cast<DriveItem>().ToList().FindIndex(x => x.Letter == selected);
        _drive.SelectedIndex = Math.Max(0, i);
    }

    private void SuggestImagePath()
    {
        if (_image.Text.Length > 0 || _drive.SelectedItem is not DriveItem item)
            return;

        string name = "disk";
        try
        {
            var d = new DriveInfo(item.Letter + ":");
            if (d.IsReady && d.VolumeLabel.Length > 0)
                name = string.Concat(d.VolumeLabel.Split(Path.GetInvalidFileNameChars()));
        }
        catch (IOException)
        {
        }

        // Не «Документы»: их часто синхронизирует OneDrive, а образ - сотни мегабайт.
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CdRescue");
        _image.Text = Path.Combine(dir, name + ".iso");
    }

    private void BrowseImage()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Файл образа",
            Filter = "Образ диска (*.iso)|*.iso|Все файлы (*.*)|*.*",
            // Существующий образ - не перезапись, а продолжение по карте.
            OverwritePrompt = false,
            FileName = Path.GetFileName(_image.Text),
            InitialDirectory = SafeDirectory(_image.Text),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _image.Text = dlg.FileName;
    }

    private void BrowseMap()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Файл карты секторов",
            Filter = "Карта секторов (*.map)|*.map|Все файлы (*.*)|*.*",
            OverwritePrompt = false,
            FileName = Path.GetFileName(_mapPath.Text),
            InitialDirectory = SafeDirectory(_mapPath.Text),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _mapPath.Text = dlg.FileName;
            _mapEdited = true;
        }
    }

    private static string SafeDirectory(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            return dir is not null && Directory.Exists(dir) ? dir : "";
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    private async Task StartAsync()
    {
        if (_drive.SelectedItem is not DriveItem drive)
        {
            MessageBox.Show(this, "Не выбран привод.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        RescueOptions options;
        try
        {
            string image = Path.GetFullPath(_image.Text.Trim());
            string map = _mapPath.Text.Trim().Length > 0 ? Path.GetFullPath(_mapPath.Text.Trim()) : image + ".map";
            Directory.CreateDirectory(Path.GetDirectoryName(image)!);
            Directory.CreateDirectory(Path.GetDirectoryName(map)!);
            options = new RescueOptions(drive.Letter, image, map, (int)_retries.Value, ((SpeedItem)_speed.SelectedItem!).KBps);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Неверный путь: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var result = await RunAsync(
            (mapReady, progress, log, ct) => RescueSession.Run(options, mapReady, progress, log, ct),
            outcome => outcome switch
            {
                RescueOutcome.Complete => "Готово: образ полный.",
                RescueOutcome.Partial => "Готово: часть секторов не прочитана. Можно продолжить позже, в том числе на другом приводе.",
                _ => "Остановлено. Образ и карта сохранены - нажмите «Начать», чтобы продолжить.",
            });

        // Неполный образ - сразу сказать, задеты ли файлы; подробности по кнопке.
        if (result == RescueOutcome.Partial && !_closeWhenStopped)
            await ReportAsync(show: false);
    }

    /// <summary>Разбирает файловую систему снятого образа и сопоставляет непрочитанные сектора с файлами.</summary>
    private async Task ReportAsync(bool show)
    {
        string image, map;
        try
        {
            image = Path.GetFullPath(_image.Text.Trim());
            map = _mapPath.Text.Trim().Length > 0 ? Path.GetFullPath(_mapPath.Text.Trim()) : image + ".map";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            MessageBox.Show(this, $"Неверный путь: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!File.Exists(image) || !File.Exists(map))
        {
            MessageBox.Show(this, "Отчёт строится по снятому образу и его карте, а их нет:\n"
                + (File.Exists(image) ? "" : image + "\n") + (File.Exists(map) ? "" : map),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _report.Enabled = false;
        try
        {
            var report = await Task.Run(() => DamageAnalyzer.Analyze(image, SectorMap.Open(map)));
            AppendLog("Анализ повреждений: " + report.Verdict);
            if (!show && report.Files.Count + report.Structures.Count > 0)
                AppendLog("Список задетых файлов - кнопка «Отчёт о повреждениях».");
            if (show)
            {
                using var form = new ReportForm(report, image);
                form.ShowDialog(this);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppendLog($"Анализ повреждений не удался: {ex.Message}");
            if (show)
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _report.Enabled = _cts is null;
        }
    }

    private async Task TestAsync()
    {
        if (_drive.SelectedItem is not DriveItem drive)
        {
            MessageBox.Show(this, "Не выбран привод.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int speed = ((SpeedItem)_speed.SelectedItem!).KBps;
        await RunAsync(
            (mapReady, progress, log, ct) => RescueSession.Test(drive.Letter, speed, mapReady, progress, log, ct),
            outcome => outcome switch
            {
                RescueOutcome.Complete => "Проверка: все сектора читаются.",
                RescueOutcome.Partial => "Проверка: есть нечитаемые сектора - они отмечены на карте красным, точные номера в журнале.",
                _ => "Проверка остановлена.",
            });
    }

    private delegate RescueOutcome Job(Action<SectorMap> mapReady, IProgress<RescueProgress> progress,
        Action<string> log, CancellationToken ct);

    /// <returns>Итог работы; null - ошибка.</returns>
    private async Task<RescueOutcome?> RunAsync(Job job, Func<RescueOutcome, string> stageText)
    {
        RescueOutcome? result = null;
        SetRunning(true);
        _cts = new CancellationTokenSource();
        _rateWindow.Clear();
        _rateClock.Restart();
        _speedInfo.Text = "";
        _mapView.Map = null;
        _log.Clear();

        var progress = new Progress<RescueProgress>(ShowProgress);
        var ct = _cts.Token;

        try
        {
            var outcome = await Task.Run(() => job(
                map => BeginInvoke(() => _mapView.Map = map),
                progress,
                line => BeginInvoke(() => AppendLog(line)),
                ct));

            _stage.Text = stageText(outcome);
            result = outcome;
        }
        catch (DriveAccessException ex)
        {
            _stage.Text = "Ошибка доступа к приводу.";
            AppendLog($"Ошибка: {ex.Message} {ex.NativeErrorCode}");
            OfferElevation(ex.Message);
        }
        catch (Exception ex)
        {
            _stage.Text = "Ошибка.";
            AppendLog($"Ошибка: {ex.Message}");
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
            _mapView.Invalidate();
        }

        if (_closeWhenStopped)
            Close();
        return result;
    }

    private void Stop()
    {
        _cts?.Cancel();
        _stop.Enabled = false;
        _stage.Text = "Остановка - дожидаюсь текущего чтения…";
    }

    private void SetRunning(bool running)
    {
        _start.Enabled = !running;
        _test.Enabled = !running;
        _report.Enabled = !running;
        _stop.Enabled = running;
        foreach (var c in new Control[] { _drive, _refresh, _image, _browseImage, _mapPath, _browseMap, _retries, _speed })
            c.Enabled = !running;
    }

    private void ShowProgress(RescueProgress p)
    {
        if (_cts is { IsCancellationRequested: false })
            _stage.Text = p.Stage;

        long done = p.Total - p.Pending;
        _progress.Value = p.Total > 0 ? (int)(1000 * done / p.Total) : 0;

        _rateWindow.Enqueue((_rateClock.Elapsed, p.SectorsRead));
        while (_rateWindow.Count > 7)
            _rateWindow.Dequeue();
        _speedInfo.Text = SpeedText(p.DriveKBps);

        _stats.Text = $"LBA {p.Lba:N0} | прочитано {p.Good:N0} ({100.0 * p.Good / p.Total:F2}%) | плохих {p.Bad:N0}{(p.Partial > 0 ? $" | частично {p.Partial:N0}" : "")} | осталось {p.Pending:N0}";
        _mapView.Invalidate();
    }

    /// <summary>
    /// Фактическая скорость - по прочитанным данным (1x = 75 секторов/с = 153,6 кБ/с полезных данных);
    /// скорость привода - как он сам её сообщает (1x = 176 кБ/с с учётом служебных байтов сектора).
    /// </summary>
    private string SpeedText(int? driveKBps)
    {
        string actual = "-";
        if (_rateWindow.Count >= 2)
        {
            var (t0, s0) = _rateWindow.Peek();
            var (t1, s1) = _rateWindow.Last();
            double seconds = (t1 - t0).TotalSeconds;
            if (seconds > 0)
            {
                double bytesPerSec = (s1 - s0) * Rescuer.SectorSize / seconds;
                actual = $"{bytesPerSec / 1000:N0} кБ/с ({bytesPerSec / 153_600:F1}x)";
            }
        }

        string drive = driveKBps is int k ? $"{k / 176.4:F0}x ({k:N0} кБ/с)" : "привод не сообщает";
        return $"Скорость чтения: фактическая {actual} | выбрана приводом {drive}";
    }

    private void ShowCellSize()
    {
        long n = _mapView.SectorsPerCell;
        _cellInfo.Text = n == 0 ? "" : $"1 клетка = {n:N0} {Plural(n, "сектор", "сектора", "секторов")} ({n * Rescuer.SectorSize / 1024.0:N0} КБ)";
    }

    private static string Plural(long n, string one, string few, string many) =>
        (n % 100) is >= 11 and <= 14 ? many : (n % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many };

    private void AppendLog(string text) =>
        _log.AppendText(text.ReplaceLineEndings(Environment.NewLine) + Environment.NewLine);

    private void OfferElevation(string message)
    {
        if (IsElevated())
        {
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var answer = MessageBox.Show(this,
            $"{message}\n\nПерезапустить программу от имени администратора?",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            _closeWhenStopped = true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Пользователь отказался в окне UAC.
        }
    }

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_cts is not null)
        {
            // Закрываемся только после контрольной точки, иначе потеряем последние секунды работы.
            e.Cancel = true;
            _closeWhenStopped = true;
            Stop();
        }
        base.OnFormClosing(e);
    }
}
