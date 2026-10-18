using CdRescue.Rescue;

namespace CdRescue;

/// <summary>Отчёт о повреждениях: сводка, список задетых файлов и структур, сохранение в текст.</summary>
internal sealed class ReportForm : Form
{
    private readonly DamageReport _report;
    private readonly string _savePath;

    /// <param name="source">Откуда отчёт - имя образа или привод; идёт в заголовок окна.</param>
    /// <param name="savePath">Предлагаемый файл для сохранения.</param>
    public ReportForm(DamageReport report, string source, string savePath)
    {
        _report = report;
        _savePath = savePath;

        Text = "Отчёт о повреждениях - " + source;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(820, 560);
        MinimumSize = new Size(560, 400);

        var summary = new TextBox
        {
            Dock = DockStyle.Top, Multiline = true, ReadOnly = true, Height = 190,
            ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window,
            Text = Summary().ReplaceLineEndings(Environment.NewLine),
        };

        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        list.Columns.Add("Что", 380);
        list.Columns.Add("Размер", 90, HorizontalAlignment.Right);
        list.Columns.Add("Не прочитано", 95, HorizontalAlignment.Right);
        list.Columns.Add("Частично", 75, HorizontalAlignment.Right);
        list.Columns.Add("Первая ошибка", 110, HorizontalAlignment.Right);

        foreach (var f in report.Files)
            list.Items.Add(new ListViewItem([
                f.Path, DamageReport.FormatSize(f.Size), f.BadSectors.ToString(), f.PartialSectors.ToString(),
                $"{f.FirstDamageOffset:N0}",
            ]));
        foreach (var s in report.Structures)
            list.Items.Add(new ListViewItem([$"[служебное] {s.What}", "", s.Lbas.Count.ToString(), "", ""])
            {
                ForeColor = Color.FromArgb(160, 60, 0),
                ToolTipText = s.Consequence,
            });
        list.ShowItemToolTips = true;

        // Ширины в пикселях не учитывают масштаб экрана - подгоняем по заголовкам и содержимому.
        Load += (_, _) =>
        {
            for (int i = 1; i < list.Columns.Count; i++)
            {
                list.AutoResizeColumn(i, ColumnHeaderAutoResizeStyle.HeaderSize);
                int header = list.Columns[i].Width;
                list.AutoResizeColumn(i, ColumnHeaderAutoResizeStyle.ColumnContent);
                list.Columns[i].Width = Math.Max(header, list.Columns[i].Width);
            }
            int rest = list.ClientSize.Width - list.Columns.Cast<ColumnHeader>().Skip(1).Sum(c => c.Width);
            list.Columns[0].Width = Math.Max(LogicalToDeviceUnits(200), rest);
        };
        if (list.Items.Count == 0)
            list.Items.Add(new ListViewItem(["Повреждённых файлов нет", "", "", "", ""]) { ForeColor = SystemColors.GrayText });

        var save = new Button { Text = "Сохранить…", AutoSize = true };
        var copy = new Button { Text = "Копировать", AutoSize = true };
        var close = new Button { Text = "Закрыть", AutoSize = true, DialogResult = DialogResult.OK };
        save.Click += (_, _) => Save();
        copy.Click += (_, _) => Clipboard.SetText(_report.ToText());

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4),
        };
        buttons.Controls.AddRange([close, save, copy]);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        body.Controls.Add(list);
        body.Controls.Add(new Label { Dock = DockStyle.Top, Height = 8 });
        body.Controls.Add(summary);

        Controls.Add(body);
        Controls.Add(buttons);
        AcceptButton = close;
    }

    /// <summary>Сводка без списков - списки в таблице ниже.</summary>
    private string Summary()
    {
        string text = _report.ToText();
        int cut = text.IndexOf("\nПовреждённые файлы:", StringComparison.Ordinal);
        int cut2 = text.IndexOf("\nПовреждённые служебные", StringComparison.Ordinal);
        int at = new[] { cut, cut2 }.Where(x => x >= 0).DefaultIfEmpty(-1).Min();
        string head = at >= 0 ? text[..at].TrimEnd() : text;
        var notes = _report.Notes.Select(n => "Примечание: " + n);
        return at >= 0 && _report.Notes.Count > 0 ? head + "\n\n" + string.Join("\n", notes) : head;
    }

    private void Save()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Сохранить отчёт",
            Filter = "Текст (*.txt)|*.txt",
            FileName = Path.GetFileName(_savePath),
            InitialDirectory = Path.GetDirectoryName(_savePath) is { } dir && Directory.Exists(dir) ? dir : "",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            File.WriteAllText(dlg.FileName, _report.ToText().ReplaceLineEndings("\r\n") + "\r\n", new System.Text.UTF8Encoding(true));
    }
}
