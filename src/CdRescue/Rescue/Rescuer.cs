// Снятие образа с повреждённого CD с данными.
//
// Читает сектора напрямую через SCSI pass-through (минуя файловую систему),
// ведёт карту секторов как ddrescue, умеет продолжать с места остановки,
// в том числе на другом приводе.
//
// Проходы:
//   1. Быстрый - читает блоками, при ошибке перескакивает вперёд (не мучает царапину).
//   2. Доочистка - возвращается к пропущенным областям, сужает их до отдельных секторов.
//   3. Повторы на минимальной скорости, каждый проход из двух фаз:
//      а) обычное чтение в обход кэша привода; головка перед попыткой отводится
//         то назад, то вперёд, чтобы подходить к сектору с разных сторон;
//      б) сырое чтение (READ CD + C2) нескольких копий сектора, сборка по большинству
//         и проверка EDC. Привод на время фазы переводится в режим «отдавать сектор даже
//         с неисправимой ошибкой» с минимумом своих повторов - повторяем мы сами.
//      Что так и не сошлось по EDC, пишется в образ лучшей догадкой вместо нулей.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CdRescue.Rescue;

internal sealed record RescueOptions(char Drive, string ImagePath, string MapPath, int Retries, int SpeedKBps);

/// <param name="SectorsRead">Сколько секторов успешно прочитано за этот запуск - для расчёта фактической скорости.</param>
/// <param name="DriveKBps">Скорость, выбранная приводом (1x = 176 кБ/с); null - привод не сообщает.</param>
internal readonly record struct RescueProgress(string Stage, long Lba, long Good, long Bad, long Partial, long Pending, long Total,
    long SectorsRead, int? DriveKBps);

internal enum RescueOutcome { Complete, Partial, Cancelled }

internal static class RescueSession
{
    /// <summary>
    /// Проверка диска: те же быстрый проход и доочистка, что при снятии образа, но без записи.
    /// Каждый сектор либо прочитан, либо отмечен плохим после отдельной попытки - карта точная.
    /// </summary>
    /// <param name="reportReady">Отчёт о повреждениях, если проверка дошла до конца и нашла нечитаемые сектора.</param>
    public static RescueOutcome Test(char drive, int speedKBps, Action<SectorMap> mapReady,
        IProgress<RescueProgress> progress, Action<string> log, Action<DamageReport> reportReady, CancellationToken ct)
    {
        using var cd = new CdDrive(drive);
        var (sectors, blockLen) = cd.ReadCapacity();
        if (blockLen != Rescuer.SectorSize)
            throw new InvalidOperationException(
                $"Размер сектора {blockLen}, ожидался 2048. Это точно диск с данными?");

        log($"Привод {drive}: {sectors:N0} секторов ({sectors * Rescuer.SectorSize / 1048576.0:F1} МБ)");

        var map = SectorMap.CreateTransient(sectors);
        mapReady(map);

        if (speedKBps > 0)
            Rescuer.ApplySpeed(cd, speedKBps, log);

        try
        {
            using var rescuer = new Rescuer(cd, imagePath: null, map, progress, log, ct);
            rescuer.Run(retries: 0);
            log(rescuer.Summary());

            if (ct.IsCancellationRequested)
                return RescueOutcome.Cancelled;
            if (map.Counts().Good == sectors)
                return RescueOutcome.Complete;

            // Образа нет - служебные сектора (каталоги, дескрипторы) читаем с привода, их немного.
            var report = DamageAnalyzer.Analyze(map, cd.ReadSector, fromDrive: true);
            log("Анализ повреждений: " + report.Verdict);
            reportReady(report);
            return RescueOutcome.Partial;
        }
        finally
        {
            if (speedKBps > 0)
                cd.RestoreDefaultSpeed();
        }
    }

    /// <summary>Выполняется в фоновом потоке; колбэки вызываются из него же.</summary>
    public static RescueOutcome Run(RescueOptions o, Action<SectorMap> mapReady,
        IProgress<RescueProgress> progress, Action<string> log, CancellationToken ct)
    {
        using var cd = new CdDrive(o.Drive);
        var (sectors, blockLen) = cd.ReadCapacity();
        if (blockLen != Rescuer.SectorSize)
            throw new InvalidOperationException(
                $"Размер сектора {blockLen}, ожидался 2048. Это точно диск с данными?");

        log($"Привод {o.Drive}: {sectors:N0} секторов ({sectors * Rescuer.SectorSize / 1048576.0:F1} МБ)");
        log($"Образ: {o.ImagePath}");
        log($"Карта: {o.MapPath}");

        var map = SectorMap.LoadOrCreate(o.MapPath, sectors);
        if (map.Resumed)
            log("Найдена карта - продолжаю с места остановки.");
        mapReady(map);

        if (o.SpeedKBps > 0)
            Rescuer.ApplySpeed(cd, o.SpeedKBps, log);

        try
        {
            using var rescuer = new Rescuer(cd, o.ImagePath, map, progress, log, ct);
            rescuer.Run(o.Retries);
            log(rescuer.Summary());
        }
        finally
        {
            // Не оставляем привод на 1x после повторов.
            cd.RestoreDefaultSpeed();
        }

        if (ct.IsCancellationRequested)
            return RescueOutcome.Cancelled;
        return map.Counts().Good == sectors ? RescueOutcome.Complete : RescueOutcome.Partial;
    }
}

/// <summary>Логика восстановления: проходы, запись образа, контрольные точки.</summary>
internal sealed unsafe class Rescuer : IDisposable
{
    public const int SectorSize = 2048;
    public const int BlockSectors = 32;        // 64 КБ за одну команду
    private const int MaxSkip = 4096;           // максимальный прыжок после ошибки, секторов
    private const int SlowSpeedKBps = 176;      // 1x для CD, привод округлит до поддерживаемой
    private const int MediumSpeedKBps = 1411;   // 8x - для чётных проходов повтора
    private const int RepositionDistance = 5000;
    private const int RawReadsPerVisit = 3;
    private const int RawBufferSize = CdDrive.RawSectorSize + CdDrive.C2Size;

    private readonly CdDrive _cd;
    private readonly FileStream? _img;   // null - проверка без записи образа
    private readonly SectorMap _map;
    private readonly IProgress<RescueProgress> _progress;
    private readonly Action<string> _log;
    private readonly Dictionary<long, RawSector> _raw = new();
    private bool _rawSupported = true;
    private byte[]? _errorRecoveryOriginal;
    private readonly CancellationToken _ct;
    private readonly byte* _buf;
    private readonly Stopwatch _sinceTick = Stopwatch.StartNew();
    private readonly Stopwatch _sinceSave = Stopwatch.StartNew();
    private readonly Stopwatch _sinceSpeedQuery = new();
    private long _lastLba;
    private int _passes = 3;    // без повторов (проверка диска) проходов два
    private long _sectorsRead;
    private int? _driveKBps;

    public Rescuer(CdDrive cd, string? imagePath, SectorMap map, IProgress<RescueProgress> progress,
        Action<string> log, CancellationToken ct)
    {
        _cd = cd;
        _map = map;
        _progress = progress;
        _log = log;
        _ct = ct;
        if (imagePath is not null)
        {
            _img = new FileStream(imagePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

            long size = map.Length * SectorSize;
            if (_img.Length < size)
                _img.SetLength(size);
        }

        // Буфер выровнен по странице - требование многих драйверов к DMA.
        _buf = (byte*)NativeMemory.AlignedAlloc(BlockSectors * SectorSize, 4096);
    }

    public void Run(int retries)
    {
        _passes = retries > 0 ? 3 : 2;
        try
        {
            FastPass();
            TrimPass();
            RetryPasses(retries);
        }
        finally
        {
            Checkpoint();
            Report(_ct.IsCancellationRequested ? "Остановлено" : "Готово", _lastLba);
        }
    }

    /// <summary>Проход 1: блоками, с экспоненциальным перескоком через повреждённые места.</summary>
    private void FastPass()
    {
        long total = _map.Length;
        long lba = 0;
        int skip = 0;

        while (lba < total && !_ct.IsCancellationRequested)
        {
            if (_map[lba] != SectorMap.Untried)
            {
                lba++;
                continue;
            }

            int n = _map.RunLength(lba, SectorMap.Untried, BlockSectors);
            if (ReadToImage(lba, n))
            {
                _map.Set(lba, n, SectorMap.Good);
                skip = 0;
            }
            else
            {
                _map.Set(lba, n, SectorMap.Skipped);
                skip = skip == 0 ? BlockSectors : Math.Min(skip * 2, MaxSkip);
                int s = _map.RunLength(lba + n, SectorMap.Untried, skip);
                _map.Set(lba + n, s, SectorMap.Skipped);
                n += s;
            }

            lba += n;
            Tick($"1/{_passes} быстрый проход (сбойные блоки пропускаются)", lba);
        }
    }

    /// <summary>Проход 2: пропущенные области - сначала блоком, при ошибке посекторно.</summary>
    private void TrimPass()
    {
        long total = _map.Length;
        long lba = 0;

        while (lba < total && !_ct.IsCancellationRequested)
        {
            if (_map[lba] != SectorMap.Skipped)
            {
                lba++;
                continue;
            }

            int n = _map.RunLength(lba, SectorMap.Skipped, BlockSectors);
            if (ReadToImage(lba, n))
            {
                _map.Set(lba, n, SectorMap.Good);
            }
            else
            {
                for (int i = 0; i < n && !_ct.IsCancellationRequested; i++)
                {
                    _map.Set(lba + i, 1, ReadToImage(lba + i, 1) ? SectorMap.Good : SectorMap.Bad);
                    Tick($"2/{_passes} доочистка: пропущенное читается по секторам", lba + i);
                }
            }

            lba += n;
            Tick($"2/{_passes} доочистка: пропущенное читается по секторам", lba);
        }
    }

    /// <summary>Проход 3: повторы плохих секторов на минимальной скорости.</summary>
    private void RetryPasses(int retries)
    {
        static bool NeedsRetry(byte x) => x == SectorMap.Bad || x == SectorMap.Partial;

        if (retries == 0 || _ct.IsCancellationRequested || _map.Find(NeedsRetry).Count == 0)
            return;

        int currentSpeed = 0;

        try
        {
            for (int pass = 1; pass <= retries && !_ct.IsCancellationRequested; pass++)
            {
                var bad = _map.Find(NeedsRetry);
                if (bad.Count == 0)
                    break;

                // Скорость тоже чередуется: что не читается на минимальной, бывает, читается на средней.
                int speed = pass % 2 == 1 ? SlowSpeedKBps : MediumSpeedKBps;
                if (speed != currentSpeed)
                {
                    ApplySpeed(_cd, speed, _log, $"Повтор {pass}/{retries}: ");
                    currentSpeed = speed;
                    _sinceSpeedQuery.Reset();   // сразу показать, на что привод согласился
                }

                // Обход в обратном направлении иногда читает то, что не читалось в прямом.
                if (pass % 2 == 0)
                    bad.Reverse();

                foreach (long lba in bad)
                {
                    if (_ct.IsCancellationRequested)
                        break;

                    Reposition(lba, fromBelow: pass % 2 == 1);
                    if (ReadToImage(lba, 1, fua: true))
                    {
                        _map.Set(lba, 1, SectorMap.Good);
                        _raw.Remove(lba);
                    }
                    Tick($"3/3 повтор {pass}/{retries}: чтение в обход кэша", lba);
                }

                if (_rawSupported && _img is not null)
                    RawPhase(_map.Find(NeedsRetry), pass, retries);
            }
        }
        finally
        {
            RestoreErrorRecovery();
            WritePartials();
        }
    }

    /// <summary>Задаёт скорость и пишет в журнал, что на самом деле выставил привод.</summary>
    public static void ApplySpeed(CdDrive cd, int kbps, Action<string> log, string prefix = "")
    {
        static string X(int k) => $"{k / 176.4:0.#}x ({k:N0} кБ/с)";

        var (reported, method) = cd.ApplyReadSpeed(kbps);
        log(prefix + (reported, method) switch
        {
            (null, _) => $"скорость {X(kbps)} запрошена, но привод не сообщает текущую скорость - проверить нельзя.",
            (int r, null) => $"скорость {X(kbps)} привод не принял, читает на {X(r)}.",
            (int r, string m) when r > kbps * 11 / 10 => $"скорость {X(kbps)} запрошена ({m}), привод округлил до своего минимума {X(r)}.",
            (int r, string m) => $"скорость {X(r)} ({m}).",
        });
    }

    /// <summary>Фаза сырого чтения: несколько копий каждого сектора, сборка по большинству.</summary>
    private void RawPhase(List<long> bad, int pass, int retries)
    {
        if (bad.Count == 0)
            return;
        if (pass % 2 == 0)
            bad.Reverse();

        SetTransferBlockOnError();

        byte* raw = _buf;
        Span<byte> user = stackalloc byte[SectorSize];

        foreach (long lba in bad)
        {
            if (_ct.IsCancellationRequested || !_rawSupported)
                break;

            if (!_raw.TryGetValue(lba, out var sector))
                _raw[lba] = sector = new RawSector();

            for (int k = 0; k < RawReadsPerVisit && !_ct.IsCancellationRequested; k++)
            {
                Reposition(lba, fromBelow: (pass + k) % 2 == 1);

                // Если привод ничего не передаст, все байты останутся помечены как ошибочные.
                new Span<byte>(raw, CdDrive.RawSectorSize).Clear();
                new Span<byte>(raw + CdDrive.RawSectorSize, CdDrive.C2Size).Fill(0xFF);

                var r = _cd.ReadCdRaw((uint)lba, raw);
                if (r.MediumMissing)
                    throw new InvalidOperationException("Диск извлечён из привода.");
                if (r.IllegalRequest && r.Transferred == 0)
                {
                    _rawSupported = false;
                    _log($"Привод не поддерживает сырое чтение с флагами C2 (sense {r.SenseKey:X}/{r.Asc:X2}/{r.Ascq:X2}) - фаза пропускается.");
                    break;
                }
                if (r.Transferred < RawBufferSize)
                    continue;

                sector.Add(new ReadOnlySpan<byte>(raw, CdDrive.RawSectorSize),
                    new ReadOnlySpan<byte>(raw + CdDrive.RawSectorSize, CdDrive.C2Size));

                if (sector.TryAssemble(lba, user))
                {
                    WriteImage(lba, user);
                    _map.Set(lba, 1, SectorMap.Good);
                    _raw.Remove(lba);
                    _log($"LBA {lba}: восстановлен по {sector.Samples} сырым чтениям, EDC сошлась; {sector.Diagnosis()}.");
                    break;
                }
            }

            Tick($"3/3 повтор {pass}/{retries}: сырое чтение и сборка по большинству", lba);
        }
    }

    /// <summary>
    /// Страница 01h (восстановление ошибок): бит TB - отдавать сектор даже с неисправимой ошибкой,
    /// число собственных повторов привода - минимум. Иначе на плохом секторе данных не получить,
    /// а внутренние повторы привода только тратят время: сравнивать копии будем мы.
    /// </summary>
    private void SetTransferBlockOnError()
    {
        var d = _errorRecoveryOriginal ?? _cd.ModeSense(0x01);
        if (d is null)
            return;

        int page = CdDrive.PageOffset(d);
        if (page + 4 > d.Length || (d[page] & 0x3F) != 0x01)
            return;

        if (_errorRecoveryOriginal is null)
        {
            _errorRecoveryOriginal = d;
            _log($"Восстановление ошибок привода: флаги {d[page + 2]:X2}, повторов {d[page + 3]}; "
                + "на время сырого чтения - TB, 1 повтор.");
        }

        var m = (byte[])d.Clone();
        m[page + 2] = (byte)((m[page + 2] | 0x20) & ~0x01); // TB вкл., DCR выкл. (коррекция нужна)
        m[page + 3] = 1;
        if (!_cd.ModeSelect(m))
            _log("Привод не принял настройки восстановления ошибок - сырое чтение идёт с его настройками.");
    }

    private void RestoreErrorRecovery()
    {
        if (_errorRecoveryOriginal is not null && !_cd.ModeSelect(_errorRecoveryOriginal))
            _log("Не удалось вернуть настройки восстановления ошибок привода - они сбросятся при извлечении диска или перезагрузке.");
    }

    /// <summary>Что не сошлось по EDC, но читалось хоть как-то, - в образ лучшей догадкой вместо нулей.</summary>
    private void WritePartials()
    {
        Span<byte> user = stackalloc byte[SectorSize];
        foreach (var (lba, sector) in _raw)
        {
            if (sector.Samples == 0 || _map[lba] == SectorMap.Good)
                continue;
            sector.TryAssemble(lba, user);
            WriteImage(lba, user);
            _map.Set(lba, 1, SectorMap.Partial);
        }
    }

    /// <summary>Отводит головку подальше, чтобы следующая попытка начиналась с нового позиционирования.</summary>
    /// <param name="fromBelow">true - подход со стороны меньших LBA, false - со стороны больших.</param>
    private void Reposition(long lba, bool fromBelow)
    {
        bool below = fromBelow ? lba >= RepositionDistance : lba + RepositionDistance >= _map.Length;
        long far = below
            ? Math.Max(0, lba - RepositionDistance)
            : Math.Min(_map.Length - 1, lba + RepositionDistance);
        _cd.Read10((uint)far, 1, _buf);
    }

    private bool ReadToImage(long lba, int count, bool fua = false)
    {
        var r = _cd.Read10((uint)lba, (ushort)count, _buf, fua);
        if (r.MediumMissing)
            throw new InvalidOperationException("Диск извлечён из привода.");
        if (!r.Ok)
            return false;

        _sectorsRead += count;
        WriteImage(lba, new ReadOnlySpan<byte>(_buf, count * SectorSize));
        return true;
    }

    private void WriteImage(long lba, ReadOnlySpan<byte> data)
    {
        if (_img is null)
            return;
        _img.Position = lba * SectorSize;
        _img.Write(data);
    }

    private void Tick(string stage, long lba)
    {
        _lastLba = lba;
        if (_sinceSave.ElapsedMilliseconds > 10_000)
        {
            Checkpoint();
            _sinceSave.Restart();
        }

        if (_sinceTick.ElapsedMilliseconds < 500)
            return;
        _sinceTick.Restart();

        Report(stage, lba);
    }

    private void Report(string stage, long lba)
    {
        // Привод меняет скорость сам (сбавляет на плохих местах), поэтому спрашиваем регулярно.
        if (!_sinceSpeedQuery.IsRunning || _sinceSpeedQuery.ElapsedMilliseconds > 2000)
        {
            _driveKBps = _cd.GetCurrentReadSpeed();
            _sinceSpeedQuery.Restart();
        }

        var c = _map.Counts();
        _progress.Report(new RescueProgress(stage, lba, c.Good, c.Bad, c.Partial, c.Untried + c.Skipped, _map.Length,
            _sectorsRead, _driveKBps));
    }

    /// <summary>Сначала данные на диск, потом карта - карта никогда не опережает образ.</summary>
    private void Checkpoint()
    {
        _img?.Flush(flushToDisk: true);
        if (!_map.IsTransient)
            _map.Save();
    }

    public string Summary()
    {
        var c = _map.Counts();
        long total = _map.Length;
        long missing = total - c.Good;
        var sb = new StringBuilder();

        sb.AppendLine($"Прочитано: {c.Good:N0} из {total:N0} секторов ({100.0 * c.Good / total:F3}%)");

        if (missing == 0)
        {
            sb.Append(_img is null ? "Все сектора читаются." : "Образ полный.");
            return sb.ToString();
        }

        sb.AppendLine($"Не прочитано: {missing:N0} секторов ({missing * SectorSize / 1024.0:F0} КБ)"
            + (_img is null ? "." : c.Partial == 0 ? ", в образе на их месте нули." : ":"));
        if (c.Partial > 0)
        {
            sb.AppendLine($"  восстановлено частично: {c.Partial:N0} - в образе лучшая сборка из сырых чтений, возможны ошибки;");
            sb.AppendLine($"  не читаются совсем: {missing - c.Partial:N0} - в образе нули.");
        }
        sb.AppendLine("Диапазоны LBA:");

        int shown = 0;
        foreach (var (start, len) in _map.Ranges(s => s != SectorMap.Good))
        {
            if (shown++ == 30)
            {
                sb.AppendLine(_img is null ? "  ..." : "  ... полный список - в файле карты");
                break;
            }
            bool partial = Enumerable.Range(0, (int)len).Any(k => _map[start + k] == SectorMap.Partial);
            sb.AppendLine($"  {start}-{start + len - 1} ({len}{(partial ? ", есть частичные" : "")})");
        }
        return sb.ToString().TrimEnd();
    }

    public void Dispose()
    {
        _img?.Dispose();
        NativeMemory.AlignedFree(_buf);
    }
}
