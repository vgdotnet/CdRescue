namespace CdRescue.Rescue;

/// <summary>
/// Карта секторов: один байт на сектор, хранится как текст.
/// '?' не читался, '+' прочитан, '-' плохой, '/' пропущен быстрым проходом,
/// '~' восстановлен частично: в образе лучшая догадка по сырым чтениям, EDC не сошлась.
/// </summary>
/// <remarks>
/// Пишет только поток восстановления; интерфейс читает байты параллельно для отрисовки -
/// для однобайтовых значений это безопасно, в худшем случае картинка отстанет на кадр.
/// </remarks>
internal sealed class SectorMap
{
    public const byte Untried = (byte)'?';
    public const byte Good = (byte)'+';
    public const byte Bad = (byte)'-';
    public const byte Skipped = (byte)'/';
    public const byte Partial = (byte)'~';

    private readonly string? _path;
    private readonly byte[] _s;

    private SectorMap(string? path, byte[] s, bool resumed)
    {
        _path = path;
        _s = s;
        Resumed = resumed;
    }

    public long Length => _s.LongLength;

    /// <summary>Карта загружена из файла - работа продолжается с места остановки.</summary>
    public bool Resumed { get; }

    /// <summary>Карта только в памяти, без файла.</summary>
    public bool IsTransient => _path is null;

    public byte this[long i] => _s[i];

    public static SectorMap LoadOrCreate(string path, long sectors)
    {
        if (File.Exists(path))
        {
            var data = File.ReadAllBytes(path);
            if (data.LongLength != sectors)
                throw new InvalidOperationException(
                    $"Карта {path} от другого диска: в ней {data.Length} секторов, на диске {sectors}.");

            return new SectorMap(path, data, resumed: true);
        }

        var s = new byte[sectors];
        Array.Fill(s, Untried);
        return new SectorMap(path, s, resumed: false);
    }

    /// <summary>Карта только в памяти - для проверки диска, на диск не сохраняется.</summary>
    public static SectorMap CreateTransient(long sectors)
    {
        var s = new byte[sectors];
        Array.Fill(s, Untried);
        return new SectorMap(null, s, resumed: false);
    }

    public void Set(long start, int count, byte status) => Array.Fill(_s, status, (int)start, count);

    public int RunLength(long start, byte status, int max)
    {
        int n = 0;
        while (n < max && start + n < _s.LongLength && _s[start + n] == status)
            n++;
        return n;
    }

    public List<long> Find(Func<byte, bool> match)
    {
        var list = new List<long>();
        for (long i = 0; i < _s.LongLength; i++)
            if (match(_s[i]))
                list.Add(i);
        return list;
    }

    public (long Good, long Bad, long Untried, long Skipped, long Partial) Counts()
    {
        long g = 0, b = 0, u = 0, sk = 0, p = 0;
        foreach (byte x in _s)
        {
            switch (x)
            {
                case Good: g++; break;
                case Bad: b++; break;
                case Untried: u++; break;
                case Skipped: sk++; break;
                case Partial: p++; break;
            }
        }
        return (g, b, u, sk, p);
    }

    public IEnumerable<(long Start, long Length)> Ranges(Func<byte, bool> match)
    {
        long i = 0;
        while (i < _s.LongLength)
        {
            if (!match(_s[i]))
            {
                i++;
                continue;
            }

            long start = i;
            while (i < _s.LongLength && match(_s[i]))
                i++;
            yield return (start, i - start);
        }
    }

    public void Save()
    {
        if (_path is null)
            throw new InvalidOperationException("Карта проверки не сохраняется.");

        string tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, _s);
        File.Move(tmp, _path, overwrite: true);
    }
}
