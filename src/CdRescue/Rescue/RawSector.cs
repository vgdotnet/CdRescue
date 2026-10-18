namespace CdRescue.Rescue;

/// <summary>
/// Собирает плохой сектор из нескольких сырых чтений: для каждого байта - взвешенное голосование
/// по большинству (байт без флага C2 весит вдвое больше), затем проверка EDC. Совпала EDC -
/// сектор восстановлен точно; нет - лучшая догадка, которую можно записать вместо нулей.
/// </summary>
internal sealed class RawSector
{
    private const int Size = CdDrive.RawSectorSize;
    private const short MaxWeight = 1000;

    private static readonly uint[] EdcTable = BuildEdcTable();

    private readonly byte[] _value = new byte[Size];
    private readonly short[] _weight = new short[Size];

    public int Samples { get; private set; }

    /// <summary>Сколько байт последнего чтения привод пометил флагом C2 (не смог исправить).</summary>
    public int LastFlagged { get; private set; }

    /// <summary>Из них в заголовке - синхронизация, адрес и режим, первые 16 байт.</summary>
    public int LastFlaggedHeader { get; private set; }

    /// <summary>Сколько байт синхронизации и адреса пришлось заменить известными значениями при последней сборке.</summary>
    public int HeaderBytesFixed { get; private set; }

    /// <summary>Байт режима в прочитанном заголовке не совпал с форматом, на котором сошлась EDC.</summary>
    public bool ModeFixed { get; private set; }

    /// <summary>Добавляет одно чтение: сырой сектор и его блок C2.</summary>
    public void Add(ReadOnlySpan<byte> raw, ReadOnlySpan<byte> c2)
    {
        LastFlagged = LastFlaggedHeader = 0;
        for (int i = 0; i < Size; i++)
        {
            bool flagged = (c2[i >> 3] & (0x80 >> (i & 7))) != 0;
            if (flagged)
            {
                LastFlagged++;
                if (i < 16)
                    LastFlaggedHeader++;
            }
            short w = (short)(flagged ? 1 : 2);

            // Голосование Бойера-Мура: кандидат держится, пока его вес не перебит.
            if (_weight[i] == 0)
            {
                _value[i] = raw[i];
                _weight[i] = w;
            }
            else if (_value[i] == raw[i])
            {
                _weight[i] = (short)Math.Min(_weight[i] + w, MaxWeight);
            }
            else if (_weight[i] >= w)
            {
                _weight[i] -= w;
            }
            else
            {
                _value[i] = raw[i];
                _weight[i] = (short)(w - _weight[i]);
            }
        }
        Samples++;
    }

    /// <summary>
    /// Текущая лучшая версия сектора. Синхронизацию и адрес в заголовке подставляем сами -
    /// они известны заранее, и ошибка в них не должна портить проверку EDC.
    /// </summary>
    /// <returns>true - EDC сошлась, данные точные.</returns>
    public bool TryAssemble(long lba, Span<byte> userData)
    {
        Span<byte> s = stackalloc byte[Size];
        _value.CopyTo(s);

        s[0] = 0;
        s.Slice(1, 10).Fill(0xFF);
        s[11] = 0;
        long a = lba + 150; // адрес MSF отсчитывается от начала зоны, перед LBA 0 - 2 секунды паузы
        s[12] = Bcd((int)(a / 4500));
        s[13] = Bcd((int)(a / 75 % 60));
        s[14] = Bcd((int)(a % 75));

        HeaderBytesFixed = 0;
        for (int i = 0; i < 15; i++)
            if (s[i] != _value[i])
                HeaderBytesFixed++;
        ModeFixed = false;

        // Режим в заголовке тоже мог прочитаться с ошибкой - пробуем оба формата данных.
        ReadOnlySpan<byte> modes = s[15] == 2 ? [2, 1] : [1, 2];
        foreach (byte mode in modes)
        {
            s[15] = mode;
            ModeFixed = mode != _value[15];
            if (mode == 1 && Edc(s[..2064]) == ReadLe(s[2064..]))
            {
                s.Slice(16, Rescuer.SectorSize).CopyTo(userData);
                return true;
            }
            // Mode 2 Form 1 (CD-XA): подзаголовок 8 байт, EDC считается без синхронизации и заголовка.
            if (mode == 2 && Edc(s[16..2072]) == ReadLe(s[2072..]))
            {
                s.Slice(24, Rescuer.SectorSize).CopyTo(userData);
                return true;
            }
        }

        ModeFixed = false;
        s.Slice(_value[15] == 2 ? 24 : 16, Rescuer.SectorSize).CopyTo(userData);
        return false;
    }

    /// <summary>
    /// Для журнала: почему сектор удалось собрать. Флаги C2 - из последнего чтения; если копий
    /// одна, помеченные байты вошли в сектор как есть, и раз EDC сошлась - они были верными.
    /// </summary>
    public string Diagnosis()
    {
        string c2 = LastFlagged == 0
            ? "флагов C2 нет"
            : $"флагов C2 {LastFlagged}" + (LastFlaggedHeader > 0 ? $" (в заголовке {LastFlaggedHeader})" : "");
        string header = HeaderBytesFixed == 0 && !ModeFixed
            ? "заголовок цел"
            : "заголовок исправлен: " + string.Join(", ", new[]
            {
                HeaderBytesFixed > 0 ? $"{HeaderBytesFixed} байт синхронизации/адреса" : null,
                ModeFixed ? "байт режима" : null,
            }.Where(x => x is not null));

        string why = (LastFlagged, HeaderBytesFixed > 0 || ModeFixed) switch
        {
            (_, true) => "повреждён был заголовок, данные целы",
            (0, false) => "чтение прошло чисто - удачная попытка",
            _ when Samples == 1 => "привод пометил байты как сомнительные, но они оказались верными",
            _ => "ошибки исправлены голосованием по нескольким копиям",
        };
        return $"{c2}, {header} - {why}";
    }

    private static byte Bcd(int v) => (byte)(v / 10 << 4 | v % 10);

    private static uint ReadLe(ReadOnlySpan<byte> b) => (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24);

    /// <summary>EDC компакт-диска: CRC-32 с полиномом 0xD8018001 (отражённый), начальное значение 0.</summary>
    private static uint Edc(ReadOnlySpan<byte> data)
    {
        uint edc = 0;
        foreach (byte b in data)
            edc = EdcTable[(edc ^ b) & 0xFF] ^ (edc >> 8);
        return edc;
    }

    private static uint[] BuildEdcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? (c >> 1) ^ 0xD8018001 : c >> 1;
            t[i] = c;
        }
        return t;
    }
}
