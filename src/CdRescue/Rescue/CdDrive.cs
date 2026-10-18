using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CdRescue.Rescue;

/// <param name="Transferred">Сколько байт привод передал; при ошибке с включённым TB данные тоже бывают.</param>
internal readonly record struct ScsiResult(bool Ok, int SenseKey, int Asc, int Ascq, uint Transferred = 0)
{
    /// <summary>NOT READY / MEDIUM NOT PRESENT.</summary>
    public bool MediumMissing => SenseKey == 2 && Asc == 0x3A;

    /// <summary>ILLEGAL REQUEST - привод не знает команду или параметр.</summary>
    public bool IllegalRequest => SenseKey == 5;
}

/// <summary>Привод не открылся - обычно не хватает прав.</summary>
internal sealed class DriveAccessException(int error, string message) : Win32Exception(error, message);

/// <summary>Доступ к приводу через IOCTL_SCSI_PASS_THROUGH_DIRECT.</summary>
internal sealed unsafe class CdDrive : IDisposable
{
    public const int MaxSpeed = 0xFFFF;

    /// <summary>Сырой сектор: синхронизация, заголовок, данные, EDC/ECC.</summary>
    public const int RawSectorSize = 2352;

    /// <summary>Блок указателей C2: по биту на каждый байт сырого сектора.</summary>
    public const int C2Size = RawSectorSize / 8;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x4D014;
    private const byte SCSI_IOCTL_DATA_OUT = 0;
    private const byte SCSI_IOCTL_DATA_IN = 1;
    private const byte SCSI_IOCTL_DATA_UNSPECIFIED = 2;
    private const byte SenseLen = 32;
    private const uint TimeoutSec = 20;

    private readonly SafeFileHandle _h;
    private uint _lastLba;

    public CdDrive(char letter)
    {
        _h = CreateFileW($@"\\.\{letter}:", GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        if (_h.IsInvalid)
            throw new DriveAccessException(Marshal.GetLastWin32Error(),
                $"Не удалось открыть привод {letter}: (запущено от администратора?)");
    }

    public (long Sectors, int BlockLen) ReadCapacity()
    {
        byte* b = (byte*)NativeMemory.AlignedAlloc(64, 64);
        try
        {
            Span<byte> cdb = stackalloc byte[10];
            cdb[0] = 0x25; // READ CAPACITY(10)

            var r = Exec(cdb, b, 8);
            if (!r.Ok)
                throw new InvalidOperationException(r.MediumMissing
                    ? "В приводе нет диска."
                    : $"Не удалось определить размер диска (sense {r.SenseKey:X}/{r.Asc:X2}/{r.Ascq:X2}).");

            uint lastLba = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
            uint blockLen = (uint)(b[4] << 24 | b[5] << 16 | b[6] << 8 | b[7]);
            _lastLba = lastLba;
            return (lastLba + 1L, (int)blockLen);
        }
        finally
        {
            NativeMemory.AlignedFree(b);
        }
    }

    /// <param name="fua">Force Unit Access: читать с поверхности, а не из кэша привода.</param>
    public ScsiResult Read10(uint lba, ushort count, byte* buf, bool fua = false)
    {
        Span<byte> cdb = stackalloc byte[10];
        cdb[0] = 0x28; // READ(10)
        cdb[1] = (byte)(fua ? 0x08 : 0);
        cdb[2] = (byte)(lba >> 24);
        cdb[3] = (byte)(lba >> 16);
        cdb[4] = (byte)(lba >> 8);
        cdb[5] = (byte)lba;
        cdb[7] = (byte)(count >> 8);
        cdb[8] = (byte)count;
        return Exec(cdb, buf, (uint)count * Rescuer.SectorSize);
    }

    /// <summary>Один сектор в новый массив - для редких чтений вроде служебных структур; null - не прочитался.</summary>
    public byte[]? ReadSector(long lba)
    {
        byte* b = (byte*)NativeMemory.AlignedAlloc(Rescuer.SectorSize, 4096);
        try
        {
            return Read10((uint)lba, 1, b, fua: true).Ok ? new ReadOnlySpan<byte>(b, Rescuer.SectorSize).ToArray() : null;
        }
        finally
        {
            NativeMemory.AlignedFree(b);
        }
    }

    /// <summary>
    /// READ CD одного сектора целиком (2352 байта) и следом блок C2 (294 байта): установленный бит
    /// значит, что соответствующий байт привод исправить не смог. Нужен буфер на RawSectorSize + C2Size.
    /// </summary>
    public ScsiResult ReadCdRaw(uint lba, byte* buf)
    {
        Span<byte> cdb = stackalloc byte[12];
        cdb[0] = 0xBE; // READ CD, тип сектора - любой
        cdb[2] = (byte)(lba >> 24);
        cdb[3] = (byte)(lba >> 16);
        cdb[4] = (byte)(lba >> 8);
        cdb[5] = (byte)lba;
        cdb[8] = 1;    // один сектор
        cdb[9] = 0xFA; // sync | все заголовки | данные | EDC/ECC | C2 (294 байта)
        return Exec(cdb, buf, RawSectorSize + C2Size, allowShort: true);
    }

    /// <summary>
    /// Скорость чтения, выбранная приводом сейчас, кБ/с (1x = 176), по странице возможностей 2Ah.
    /// Поле устарело в MMC-3, но большинство приводов его заполняют; null - привод не сообщил.
    /// </summary>
    public int? GetCurrentReadSpeed()
    {
        var d = ModeSense(0x2A);
        if (d is null)
            return null;

        int page = PageOffset(d);
        if (page + 16 > d.Length || (d[page] & 0x3F) != 0x2A)
            return null;

        int kbps = d[page + 14] << 8 | d[page + 15];
        return kbps > 0 ? kbps : null;
    }

    /// <summary>MODE SENSE(10), текущие значения; null - привод не ответил.</summary>
    public byte[]? ModeSense(byte page)
    {
        const int len = 256;
        byte* b = (byte*)NativeMemory.AlignedAlloc(len, 64);
        try
        {
            Span<byte> cdb = stackalloc byte[10];
            cdb[0] = 0x5A; // MODE SENSE(10)
            cdb[2] = page;
            cdb[7] = len >> 8;
            cdb[8] = len & 0xFF;

            if (!Exec(cdb, b, len, allowShort: true).Ok)
                return null;

            int dataLen = Math.Min((b[0] << 8 | b[1]) + 2, len);
            return dataLen > 8 ? new ReadOnlySpan<byte>(b, dataLen).ToArray() : null;
        }
        finally
        {
            NativeMemory.AlignedFree(b);
        }
    }

    /// <summary>
    /// MODE SELECT(10) с данными, полученными от ModeSense (поля, которые нельзя отправлять обратно,
    /// обнуляются здесь). Без бита SP - значения не сохраняются в приводе и сбрасываются при выключении.
    /// </summary>
    public bool ModeSelect(byte[] modeSenseData)
    {
        var d = (byte[])modeSenseData.Clone();
        d[0] = d[1] = 0;            // длина данных в MODE SELECT зарезервирована
        int page = PageOffset(d);
        if (page < d.Length)
            d[page] &= 0x3F;        // бит PS только для чтения

        byte* b = (byte*)NativeMemory.AlignedAlloc((nuint)d.Length, 64);
        try
        {
            d.CopyTo(new Span<byte>(b, d.Length));
            Span<byte> cdb = stackalloc byte[10];
            cdb[0] = 0x55; // MODE SELECT(10)
            cdb[1] = 0x10; // PF - страницы в формате SCSI-2
            cdb[7] = (byte)(d.Length >> 8);
            cdb[8] = (byte)d.Length;
            return Exec(cdb, b, (uint)d.Length, dataOut: true).Ok;
        }
        finally
        {
            NativeMemory.AlignedFree(b);
        }
    }

    /// <summary>Где в ответе MODE SENSE(10) начинается страница: после заголовка и дескрипторов блоков.</summary>
    public static int PageOffset(byte[] modeData) => 8 + (modeData[6] << 8 | modeData[7]);

    /// <summary>
    /// Задаёт скорость чтения и проверяет, что привод её принял. Сначала SET CD SPEED; если
    /// привод продолжает сообщать прежнюю скорость - SET STREAMING, которую новые приводы
    /// слушаются лучше. Привод вправе округлить вверх до своего минимума (часто 4x).
    /// </summary>
    /// <returns>Скорость, которую привод сообщает после установки, и сработавшая команда (null - ни одна).</returns>
    public (int? Reported, string? Method) ApplyReadSpeed(int kbps)
    {
        SetSpeed(kbps);
        int? now = ReportedSpeedAfterSpinUp();
        if (now is int n && n <= kbps * 11 / 10)
            return (now, "SET CD SPEED");

        int? before = now;
        if (!SetStreaming(kbps, restoreDefaults: false))
            return (now, null);

        now = ReportedSpeedAfterSpinUp();
        // Ниже не опустился - но если хоть снизился, привод принял запрос и округлил до своего минимума.
        if (now is int m && (m <= kbps * 11 / 10 || before is int b && m < b))
            return (now, "SET STREAMING");
        return (now, null);
    }

    /// <summary>Максимальная скорость и настройки привода по умолчанию.</summary>
    public void RestoreDefaultSpeed()
    {
        SetSpeed(MaxSpeed);
        SetStreaming(0, restoreDefaults: true);
    }

    /// <summary>Новая скорость в странице 2Ah появляется, когда привод действительно раскрутит диск.</summary>
    private int? ReportedSpeedAfterSpinUp()
    {
        byte* b = (byte*)NativeMemory.AlignedAlloc(Rescuer.SectorSize, 4096);
        try
        {
            Read10(0, 1, b);
        }
        finally
        {
            NativeMemory.AlignedFree(b);
        }
        return GetCurrentReadSpeed();
    }

    /// <summary>SET STREAMING: дескриптор производительности на весь диск, kbps кБ за 1000 мс.</summary>
    /// <param name="restoreDefaults">Бит RDD - вернуть заводские настройки производительности.</param>
    /// <param name="endLba">null - последний сектор диска; CDSlow передаёт 0xFFFFFFFF.</param>
    public bool SetStreaming(int kbps, bool restoreDefaults, uint? endLba = null)
    {
        const int len = 28;
        byte* b = (byte*)NativeMemory.AlignedAlloc(len, 64);
        try
        {
            var d = new Span<byte>(b, len);
            d.Clear();
            d[0] = (byte)(restoreDefaults ? 0x04 : 0);
            PutBe(d[8..], endLba ?? _lastLba); // конечный LBA, начальный - 0
            PutBe(d[12..], (uint)kbps);        // объём чтения, кБ
            PutBe(d[16..], 1000);              // за время, мс
            PutBe(d[20..], (uint)kbps);        // то же для записи - обязательные поля
            PutBe(d[24..], 1000);

            Span<byte> cdb = stackalloc byte[12];
            cdb[0] = 0xB6; // SET STREAMING
            cdb[10] = len;
            return Exec(cdb, b, len, dataOut: true).Ok;
        }
        finally
        {
            NativeMemory.AlignedFree(b);
        }
    }

    private static void PutBe(Span<byte> d, uint v)
    {
        d[0] = (byte)(v >> 24);
        d[1] = (byte)(v >> 16);
        d[2] = (byte)(v >> 8);
        d[3] = (byte)v;
    }

    /// <summary>
    /// GET PERFORMANCE, текущая производительность чтения: скорость в начале и в конце диска, кБ/с.
    /// У дисков CAV они разные - снаружи диск читается быстрее. null - привод не ответил.
    /// </summary>
    public (int Start, int End)? GetReadPerformance()
    {
        const int len = 8 + 16 * 4;
        byte* b = (byte*)NativeMemory.AlignedAlloc(len, 64);
        try
        {
            Span<byte> cdb = stackalloc byte[12];
            cdb[0] = 0xAC; // GET PERFORMANCE
            cdb[1] = 0x00; // чтение, номинальная производительность
            cdb[9] = 4;    // не больше 4 дескрипторов
            cdb[10] = 0;   // тип - данные производительности

            if (!Exec(cdb, b, len, allowShort: true).Ok)
                return null;

            uint dataLen = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
            if (dataLen < 4 + 16)
                return null;

            int start = b[12] << 24 | b[13] << 16 | b[14] << 8 | b[15];
            int end = b[20] << 24 | b[21] << 16 | b[22] << 8 | b[23];
            return start > 0 || end > 0 ? (start, end) : null;
        }
        finally
        {
            NativeMemory.AlignedFree(b);
        }
    }

    /// <param name="writeKbps">Скорость записи; CDSlow передаёт текущую из страницы 2Ah, а не максимум.</param>
    public ScsiResult SetSpeed(int kbps, int writeKbps = 0xFFFF)
    {
        ushort v = (ushort)Math.Clamp(kbps, 1, 0xFFFF);
        Span<byte> cdb = stackalloc byte[12];
        cdb[0] = 0xBB; // SET CD SPEED
        cdb[2] = (byte)(v >> 8);
        cdb[3] = (byte)v;
        cdb[4] = (byte)(writeKbps >> 8);
        cdb[5] = (byte)writeKbps;
        return Exec(cdb, null, 0);
    }

    private ScsiResult Exec(ReadOnlySpan<byte> cdb, byte* buf, uint len, bool allowShort = false, bool dataOut = false)
    {
        var s = new SptdWithSense();
        s.Sptd.Length = (ushort)sizeof(Sptd);
        s.Sptd.CdbLength = (byte)cdb.Length;
        s.Sptd.SenseInfoLength = SenseLen;
        s.Sptd.DataIn = dataOut ? SCSI_IOCTL_DATA_OUT : len > 0 ? SCSI_IOCTL_DATA_IN : SCSI_IOCTL_DATA_UNSPECIFIED;
        s.Sptd.DataTransferLength = len;
        s.Sptd.TimeOutValue = TimeoutSec;
        s.Sptd.DataBuffer = (IntPtr)buf;
        s.Sptd.SenseInfoOffset = (uint)((byte*)s.Sense - (byte*)&s);
        for (int i = 0; i < cdb.Length; i++)
            s.Sptd.Cdb[i] = cdb[i];

        uint size = (uint)sizeof(SptdWithSense);
        if (!DeviceIoControl(_h, IOCTL_SCSI_PASS_THROUGH_DIRECT, &s, size, &s, size, out _, IntPtr.Zero))
            return new ScsiResult(false, -1, 0, 0);

        uint transferred = s.Sptd.DataTransferLength;
        if (s.Sptd.ScsiStatus != 0)
            return new ScsiResult(false, s.Sense[2] & 0x0F, s.Sense[12], s.Sense[13], transferred);

        // Привод может вернуть «успех» с неполными данными - такое чтение не засчитываем.
        if (!allowShort && transferred < len)
            return new ScsiResult(false, -2, 0, 0, transferred);

        return new ScsiResult(true, 0, 0, 0, transferred);
    }

    public void Dispose() => _h.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct Sptd
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        public fixed byte Cdb[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SptdWithSense
    {
        public Sptd Sptd;
        public uint Filler;
        public fixed byte Sense[SenseLen];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code,
        void* inBuf, uint inSize, void* outBuf, uint outSize, out uint returned, IntPtr overlapped);
}
