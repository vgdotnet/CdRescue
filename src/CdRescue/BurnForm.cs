using System.Runtime.InteropServices;
using CdRescue.Burn;
using CdRescue.Rescue;

namespace CdRescue;

/// <summary>Запись образа на чистый или перезаписываемый диск (со стиранием) и сверка.</summary>
internal sealed class BurnForm : Form
{
    private sealed record SpeedItem(int SectorsPerSecond, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly string _isoPath;
    private readonly string? _warning;
    private readonly ComboBox _recorder = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _refresh = new() { Text = "Обновить", AutoSize = true };
    private readonly Label _media = new() { AutoSize = true, Text = "…" };
    private readonly ComboBox _speed = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly ComboBox _erase = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Label _eraseCaption = new() { Text = "Стирание:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };
    private readonly CheckBox _verify = new() { Text = "Сверить диск с образом после записи", AutoSize = true, Checked = true };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 18 };
    private readonly Label _stage = new() { AutoSize = true };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9.5f), BackColor = SystemColors.Window,
    };
    private readonly Button _burn = new() { Text = "Записать", AutoSize = true, Enabled = false };
    private readonly Button _cancel = new() { Text = "Отменить", AutoSize = true, Enabled = false };
    private readonly Button _close = new() { Text = "Закрыть", AutoSize = true, DialogResult = DialogResult.Cancel };

    private MediaInfo? _info;
    private CancellationTokenSource? _cts;

    // Этап и время последнего события: если служба молчит, окно всё равно показывает, что работа идёт.
    private readonly System.Windows.Forms.Timer _ticker = new() { Interval = 1000 };
    private string _phase = "";
    private DateTime _phaseStart;
    private DateTime _lastEvent;

    /// <param name="warning">Предупреждение о неполном образе - показывается перед записью.</param>
    public BurnForm(string isoPath, string? warning)
    {
        _isoPath = isoPath;
        _warning = warning;

        Text = "Запись на диск - " + Path.GetFileName(isoPath);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(720, 480);
        MinimumSize = new Size(560, 420);

        long sectors = new FileInfo(isoPath).Length / Rescuer.SectorSize;
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(8) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // Ячейки задаём явно: строка стирания показывается не всегда, а скрытые элементы
        // при автораскладке сдвигают соседей.
        int row = 0;
        void Row(Control caption, Control field, Control? extra = null)
        {
            grid.Controls.Add(caption, 0, row);
            grid.Controls.Add(field, 1, row);
            if (extra is not null)
                grid.Controls.Add(extra, 2, row);
            else
                grid.SetColumnSpan(field, 2);
            row++;
        }

        Row(Caption("Образ:"), new Label
        {
            AutoSize = true, Text = $"{isoPath}  ({sectors * Rescuer.SectorSize / 1048576.0:N1} МБ)", Margin = new Padding(3, 7, 3, 3),
        });
        Row(Caption("Привод:"), _recorder, _refresh);

        _media.Margin = new Padding(3, 7, 3, 3);
        Row(Caption("Диск:"), _media);

        var speedRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        speedRow.Controls.Add(_speed);
        speedRow.Controls.Add(new Label
        {
            AutoSize = true, Margin = new Padding(8, 7, 3, 3), ForeColor = SystemColors.GrayText,
            Text = "медленнее - надёжнее",
        });
        Row(Caption("Скорость:"), speedRow);

        _erase.Items.AddRange([
            "быстрое - удаляется оглавление, около минуты",
            "полное - перезаписывается вся поверхность, десятки минут",
        ]);
        _erase.SelectedIndex = 0;
        Row(_eraseCaption, _erase);
        ShowErase(false);

        Row(new Label(), _verify);

        if (warning is not null)
        {
            Row(new Label(), new Label
            {
                AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(660), 0), Margin = new Padding(3, 8, 3, 3),
                ForeColor = Color.FromArgb(170, 60, 0), Text = warning,
            });
        }

        var status = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(8, 0, 8, 4) };
        status.Controls.Add(_stage);
        status.Controls.Add(_progress);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
        buttons.Controls.AddRange([_close, _cancel, _burn]);

        var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0) };
        logPanel.Controls.Add(_log);

        Controls.Add(logPanel);
        Controls.Add(status);
        Controls.Add(grid);
        Controls.Add(buttons);
        CancelButton = _close;

        _refresh.Click += async (_, _) => await LoadRecordersAsync();
        _recorder.SelectedIndexChanged += async (_, _) => await InspectAsync();
        _burn.Click += async (_, _) => await BurnAsync();
        _cancel.Click += (_, _) =>
        {
            _cts?.Cancel();
            _cancel.Enabled = false;
            _stage.Text = "Отмена…";
        };
        Shown += async (_, _) => await LoadRecordersAsync();
        _ticker.Tick += (_, _) =>
        {
            if (_phase.Length > 0 && DateTime.Now - _lastEvent > TimeSpan.FromSeconds(3))
                _stage.Text = $"{_phase} - идёт {DateTime.Now - _phaseStart:mm\\:ss}, привод не сообщает прогресс";
        };
    }

    private static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };

    private static string Cancelled(MediaInfo info) =>
        "Запись отменена: на диске только часть образа, пользоваться им нельзя."
        + (info.NeedsErase || info.Overwrite || info.Type.Contains("RW") ? " Диск можно стереть и записать заново." : "");

    private long ImageSectors => new FileInfo(_isoPath).Length / Rescuer.SectorSize;

    private void ShowErase(bool show) => _eraseCaption.Visible = _erase.Visible = show;

    /// <summary>Что сейчас записано на диске - чтобы не стереть не тот диск.</summary>
    private static string DiscContent(char? letter)
    {
        try
        {
            if (letter is char l && new DriveInfo(l + ":") is { IsReady: true } d)
                return $" «{d.VolumeLabel}», {d.TotalSize / 1048576.0:N0} МБ";
        }
        catch (IOException)
        {
        }
        return "";
    }

    private async Task LoadRecordersAsync()
    {
        _recorder.Items.Clear();
        _media.Text = "Поиск пишущих приводов…";
        try
        {
            var list = await Task.Run(Imapi.Recorders);
            foreach (var r in list)
                _recorder.Items.Add(r);
            if (list.Count == 0)
                _media.Text = "Пишущих приводов не найдено (или служба записи Windows недоступна).";
            else
                _recorder.SelectedIndex = 0;
        }
        catch (COMException ex)
        {
            _media.Text = "Служба записи дисков Windows недоступна: " + ex.Message;
        }
    }

    private async Task InspectAsync()
    {
        _burn.Enabled = false;
        _info = null;
        if (_recorder.SelectedItem is not RecorderInfo rec)
            return;

        _media.Text = "Проверяю диск…";
        try
        {
            _info = await Task.Run(() => Imapi.Inspect(rec.Id));
        }
        catch (COMException ex)
        {
            _media.Text = "Не удалось опросить привод: " + ex.Message;
            return;
        }

        var m = _info;
        long need = ImageSectors;
        string size = m.FreeSectors > 0 ? $"{m.FreeSectors * Rescuer.SectorSize / 1048576.0:N0} МБ" : "станет известна после стирания";
        string content = m.DestroysData ? DiscContent(rec.Letter) : "";
        _media.Text = m.Problem.Length > 0 ? (m.Type.Length > 0 ? m.Type + ": " : "") + m.Problem
            : m.NeedsErase ? $"{m.Type} с данными{content} - перед записью будет стёрт; ёмкость {size}"
            : m.Overwrite ? $"{m.Type} с данными{content} - запись пойдёт поверх них; ёмкость {size}"
            : $"{m.Type}, пустой, свободно {size}";
        _media.ForeColor = m.DestroysData ? Color.FromArgb(170, 60, 0) : SystemColors.ControlText;
        ShowErase(m.NeedsErase);
        if (m.CanBurn && m.FreeSectors > 0 && m.FreeSectors < need)
            _media.Text += $" - образ не поместится (нужно {need * Rescuer.SectorSize / 1048576.0:N0} МБ)";

        _speed.Items.Clear();
        _speed.Items.Add(new SpeedItem(0, "максимальная"));
        foreach (int s in m.WriteSpeeds)
            _speed.Items.Add(new SpeedItem(s, $"{(double)s / m.SectorsPer1x:0.#}x ({s * Rescuer.SectorSize / 1000:N0} кБ/с)"));
        // По умолчанию - вторая сверху: на максимуме чаще бывают ошибки записи.
        _speed.SelectedIndex = _speed.Items.Count > 2 ? 2 : 0;

        _burn.Enabled = m.CanBurn && (m.FreeSectors == 0 || m.FreeSectors >= need);
    }

    private async Task BurnAsync()
    {
        if (_recorder.SelectedItem is not RecorderInfo rec || _info is null)
            return;

        var info = _info;
        bool full = _erase.SelectedIndex == 1;
        string question = $"Записать образ на {info.Type} в приводе {rec.Letters}?\n\nДиск будет закрыт - дописать на него потом нельзя.";
        if (info.DestroysData)
            question = $"Все данные на диске{DiscContent(rec.Letter)} будут удалены без возможности восстановления"
                + (info.NeedsErase ? $" ({(full ? "полное" : "быстрое")} стирание, прервать его нельзя)." : ".")
                + "\n\n" + question;
        if (_warning is not null)
            question = _warning + "\n\n" + question;
        if (MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo,
                _warning is null && !info.DestroysData ? MessageBoxIcon.Question : MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        int speed = ((SpeedItem)_speed.SelectedItem!).SectorsPerSecond;
        Log($"Запись: {_isoPath} -> {rec.Letters} {rec.Name}, скорость {_speed.SelectedItem}");

        try
        {
            if (info.NeedsErase)
            {
                // Стирание не отменяется - кнопку отмены включим к началу записи.
                _cancel.Enabled = false;
                Phase(full ? "Полное стирание" : "Быстрое стирание");
                await Task.Run(() => Imapi.Erase(rec.Id, full, (elapsed, total) => BeginInvoke(() =>
                {
                    _lastEvent = DateTime.Now;
                    _progress.Value = total > 0 ? Math.Clamp(1000 * elapsed / total, 0, 1000) : 0;
                    _stage.Text = $"{(full ? "Полное" : "Быстрое")} стирание: {elapsed} из ~{total} с";
                })));
                Log("Диск стёрт.");

                // До стирания закрытый RW ёмкость не сообщает - проверяем сейчас, пока ничего не записано.
                var after = await Task.Run(() => Imapi.Inspect(rec.Id));
                if (after.FreeSectors > 0 && after.FreeSectors < ImageSectors)
                    throw new InvalidOperationException(
                        $"Образ не помещается: на диске {after.FreeSectors * Rescuer.SectorSize / 1048576.0:N0} МБ, "
                        + $"нужно {ImageSectors * Rescuer.SectorSize / 1048576.0:N0} МБ. Диск стёрт, запись не начиналась.");
                if (!after.CanBurn)
                    throw new InvalidOperationException($"После стирания диск не готов к записи: {after.Problem}");
                _cancel.Enabled = true;
            }

            Phase("Запись");
            string lastAction = "";
            var (events, sinkError) = await Task.Run(() => Imapi.Burn(rec.Id, _isoPath, speed, p => BeginInvoke(() =>
            {
                _lastEvent = DateTime.Now;
                if (p.Action != lastAction)
                {
                    Log(p.Action);
                    lastAction = p.Action;
                }
                _stage.Text = p.Total > 0 && p.Written > 0
                    ? $"{p.Action}: {p.Written * Rescuer.SectorSize / 1048576.0:N0} из {p.Total * Rescuer.SectorSize / 1048576.0:N0} МБ, осталось ~{p.RemainingSeconds} с"
                    : p.Action;
                _progress.Value = p.Total > 0 ? (int)Math.Clamp(1000 * p.Written / p.Total, 0, 1000) : 0;
            }), ct));
            Log($"Событий прогресса от службы записи: {events}" + (sinkError is null ? "" : $", ошибка их разбора: {sinkError}"));

            if (ct.IsCancellationRequested)
            {
                _stage.Text = Cancelled(info);
                Log(_stage.Text);
                return;
            }
            Log("Запись завершена.");

            if (_verify.Checked && rec.Letter is char letter)
            {
                Phase("Сверка с образом");
                var result = await Task.Run(() => BurnVerifier.Verify(letter, _isoPath, (done, total) => BeginInvoke(() =>
                {
                    _lastEvent = DateTime.Now;
                    _progress.Value = (int)(1000 * done / Math.Max(1, total));
                    _stage.Text = $"Сверка с образом: {done * Rescuer.SectorSize / 1048576.0:N0} из {total * Rescuer.SectorSize / 1048576.0:N0} МБ";
                }), ct));

                _stage.Text = ct.IsCancellationRequested ? "Сверка прервана; запись завершена."
                    : result.Ok ? "Готово: диск записан и совпадает с образом."
                    : $"Диск записан, но отличается от образа: не читаются {result.Unreadable:N0}, отличаются {result.Mismatched:N0} секторов (первый - LBA {result.FirstBad}).";
                Log(_stage.Text);
            }
            else
            {
                _stage.Text = "Готово: диск записан.";
            }
        }
        catch (COMException) when (ct.IsCancellationRequested)
        {
            // Отменили мы - служба отвечает «request was cancelled»; это не ошибка записи.
            _stage.Text = Cancelled(info);
            Log(_stage.Text);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or DriveAccessException)
        {
            _stage.Text = "Ошибка записи.";
            Log("Ошибка: " + ex.Message);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            await InspectAsync();
        }
    }

    private void Phase(string name)
    {
        _phase = name;
        _phaseStart = _lastEvent = DateTime.Now;
        _stage.Text = name;
        Log(name);
    }

    private void SetBusy(bool busy)
    {
        if (busy)
            _ticker.Start();
        else
        {
            _ticker.Stop();
            _phase = "";
        }
        _burn.Enabled = !busy;
        _cancel.Enabled = busy;
        _close.Enabled = !busy;
        _recorder.Enabled = _refresh.Enabled = _speed.Enabled = _erase.Enabled = _verify.Enabled = !busy;
        ControlBox = !busy;
    }

    private void Log(string text) => _log.AppendText(text + Environment.NewLine);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Прерванная запись портит болванку - закрыть окно можно только после неё.
        if (_cts is not null)
            e.Cancel = true;
        base.OnFormClosing(e);
    }
}
