using System.ComponentModel;
using CdRescue.Rescue;

namespace CdRescue;

/// <summary>Карта секторов клетками: каждая клетка - группа секторов, цвет по худшему из них.</summary>
internal sealed class SectorMapView : Control
{
    private const int Cell = 6;
    private const int Gap = 1;

    public static readonly Color GoodColor = Color.FromArgb(64, 170, 72);
    public static readonly Color BadColor = Color.FromArgb(214, 48, 49);
    public static readonly Color SkippedColor = Color.FromArgb(240, 170, 40);
    public static readonly Color UntriedColor = Color.FromArgb(200, 200, 200);
    public static readonly Color PartialColor = Color.FromArgb(142, 68, 173);

    private readonly SolidBrush _good = new(GoodColor);
    private readonly SolidBrush _bad = new(BadColor);
    private readonly SolidBrush _skipped = new(SkippedColor);
    private readonly SolidBrush _untried = new(UntriedColor);
    private readonly SolidBrush _partial = new(PartialColor);
    private readonly ToolTip _tip = new();
    private SectorMap? _map;
    private int _hoverCell = -1;
    private long _lastPerCell;

    /// <summary>Сменилось число секторов в клетке - при изменении размера или новой карте.</summary>
    public event EventHandler? ScaleChanged;

    /// <summary>Сколько секторов показывает одна клетка; 0 - карты нет.</summary>
    public long SectorsPerCell => _map is null ? 0 : Grid().PerCell;

    public SectorMapView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Color.White;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SectorMap? Map
    {
        get => _map;
        set
        {
            _map = value;
            Invalidate();
        }
    }

    private (int Cols, long PerCell) Grid()
    {
        int cols = Math.Max(1, (Width - Gap) / (Cell + Gap));
        int rows = Math.Max(1, (Height - Gap) / (Cell + Gap));
        long total = _map?.Length ?? 0;
        long perCell = Math.Max(1, (total + (long)cols * rows - 1) / ((long)cols * rows));
        return (cols, perCell);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_lastPerCell != SectorsPerCell)
        {
            _lastPerCell = SectorsPerCell;
            ScaleChanged?.Invoke(this, EventArgs.Empty);
        }
        if (_map is null)
            return;

        var (cols, perCell) = Grid();
        long total = _map.Length;
        int cells = (int)((total + perCell - 1) / perCell);

        for (int c = 0; c < cells; c++)
        {
            long from = c * perCell;
            long to = Math.Min(total, from + perCell);
            int x = Gap + c % cols * (Cell + Gap);
            int y = Gap + c / cols * (Cell + Gap);
            e.Graphics.FillRectangle(BrushFor(from, to), x, y, Cell, Cell);
        }
    }

    private SolidBrush BrushFor(long from, long to)
    {
        bool skipped = false, untried = false, partial = false;
        for (long i = from; i < to; i++)
        {
            switch (_map![i])
            {
                case SectorMap.Bad: return _bad;
                case SectorMap.Skipped: skipped = true; break;
                case SectorMap.Untried: untried = true; break;
                case SectorMap.Partial: partial = true; break;
            }
        }
        return partial ? _partial : skipped ? _skipped : untried ? _untried : _good;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_map is null)
            return;

        var (cols, perCell) = Grid();
        int col = (e.X - Gap) / (Cell + Gap);
        int cell = col < cols ? (e.Y - Gap) / (Cell + Gap) * cols + col : -1;
        long from = cell * perCell;
        if (cell < 0 || from >= _map.Length)
            cell = -1;

        if (cell == _hoverCell)
            return;
        _hoverCell = cell;

        if (cell < 0)
        {
            _tip.Hide(this);
            return;
        }

        long to = Math.Min(_map.Length, from + perCell) - 1;
        _tip.Show($"LBA {from}-{to}", this, e.X + 12, e.Y + 16);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverCell = -1;
        _tip.Hide(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _good.Dispose();
            _bad.Dispose();
            _skipped.Dispose();
            _untried.Dispose();
            _partial.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}
