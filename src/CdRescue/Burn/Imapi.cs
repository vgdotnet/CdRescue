using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace CdRescue.Burn;

internal sealed record RecorderInfo(string Id, string Name, string Letters)
{
    public override string ToString() => $"{Letters}  {Name}";

    public char? Letter => Letters.Length > 0 ? Letters[0] : null;
}

/// <param name="FreeSectors">Сколько поместится: на чистом диске - свободное место, на RW - вся ёмкость после стирания.</param>
/// <param name="WriteSpeeds">Поддерживаемые скорости записи для вставленного диска, секторов в секунду.</param>
/// <param name="NeedsErase">CD-RW или DVD-RW с данными: перед записью их нужно стереть.</param>
/// <param name="Overwrite">DVD+RW, BD-RE, DVD-RAM с данными: запись идёт поверх, стирать не нужно.</param>
internal sealed record MediaInfo(string Type, bool Supported, bool Blank, bool Appendable, long FreeSectors,
    int SectorsPer1x, IReadOnlyList<int> WriteSpeeds, string Problem, bool NeedsErase = false, bool Overwrite = false)
{
    public bool CanBurn => Supported && Problem.Length == 0;

    /// <summary>Запись уничтожит то, что сейчас на диске.</summary>
    public bool DestroysData => NeedsErase || Overwrite;
}

internal readonly record struct BurnProgress(string Action, long Written, long Total, int RemainingSeconds);

/// <summary>
/// Запись образа штатным API Windows (IMAPI2): оно само ведёт буфер, защиту от опустошения
/// и закрытие сессии. Вызывается через позднее связывание - интероп-сборки для IMAPI2
/// в .NET SDK нет, а события записи приходят через точку подключения COM.
/// </summary>
internal static class Imapi
{
    // IMAPI_FORMAT2_DATA_MEDIA_STATE
    private const int StateOverwriteOnly = 0x1;
    private const int StateBlank = 0x2;
    private const int StateAppendable = 0x4;
    private const int StateDamaged = 0x400;
    private const int StateEraseRequired = 0x800;
    private const int StateWriteProtected = 0x2000;
    private const int StateFinalized = 0x4000;
    private const int StateUnsupported = 0x8000;

    private static readonly string[] MediaTypes =
    [
        "неизвестный диск", "CD-ROM", "CD-R", "CD-RW", "DVD-ROM", "DVD-RAM", "DVD+R", "DVD+RW", "DVD+R DL",
        "DVD-R", "DVD-RW", "DVD-R DL", "диск", "DVD+RW DL", "HD DVD-ROM", "HD DVD-R", "HD DVD-RAM",
        "BD-ROM", "BD-R", "BD-RE",
    ];

    private static readonly string[] Actions =
    [
        "проверка диска", "форматирование", "подготовка привода", "калибровка лазера", "запись данных",
        "закрытие диска", "готово", "проверка записи",
    ];

    public static List<RecorderInfo> Recorders()
    {
        var list = new List<RecorderInfo>();
        dynamic master = Create("IMAPI2.MsftDiscMaster2");
        try
        {
            if (!(bool)master.IsSupportedEnvironment)
                return list;

            foreach (string id in master)
            {
                dynamic rec = Create("IMAPI2.MsftDiscRecorder2");
                try
                {
                    rec.InitializeDiscRecorder(id);
                    var letters = ((object[])rec.VolumePathNames).Cast<string>().Select(p => p.TrimEnd('\\'));
                    string name = $"{((string)rec.VendorId).Trim()} {((string)rec.ProductId).Trim()}".Trim();
                    list.Add(new RecorderInfo(id, name, string.Join(" ", letters)));
                }
                finally
                {
                    Release(rec);
                }
            }
        }
        finally
        {
            Release(master);
        }
        return list;
    }

    public static MediaInfo Inspect(string recorderId)
    {
        dynamic rec = Create("IMAPI2.MsftDiscRecorder2");
        dynamic fmt = Create("IMAPI2.MsftDiscFormat2Data");
        try
        {
            rec.InitializeDiscRecorder(recorderId);
            if (!(bool)fmt.IsRecorderSupported(rec))
                return new MediaInfo("", false, false, false, 0, 0, [], "Привод не умеет записывать диски с данными.");

            fmt.Recorder = rec;
            int type;
            try
            {
                type = (int)fmt.CurrentPhysicalMediaType;
            }
            catch (COMException)
            {
                return new MediaInfo("", false, false, false, 0, 0, [], "В приводе нет диска.");
            }

            string typeName = type >= 0 && type < MediaTypes.Length ? MediaTypes[type] : $"тип {type}";
            int per1x = type switch
            {
                >= 1 and <= 3 => 75,            // CD: 75 секторов в секунду
                >= 17 and <= 19 => 2195,        // BD
                _ => 676,                       // DVD
            };

            // Закрытый CD-RW/DVD-RW модуль записи данных не принимает, пока диск не стёрт, -
            // а модуль стирания его поддерживает. Ёмкость такой диск до стирания не сообщает (0).
            if (!(bool)fmt.IsCurrentMediaSupported(rec) && type is 3 or 10 && CanErase(rec))
                return new MediaInfo(typeName, true, false, false, 0, per1x, [], "", NeedsErase: true);

            if (!(bool)fmt.IsCurrentMediaSupported(rec))
                return new MediaInfo(typeName, false, false, false, 0, per1x, [], type is 1 or 4 or 17
                    ? "диск только для чтения - вставьте чистую болванку"
                    : "на такой диск этот привод не пишет");

            int state = (int)fmt.CurrentMediaStatus;
            bool blank = (state & StateBlank) != 0;
            bool erasable = type is 3 or 10;                                 // CD-RW, DVD-RW
            bool overwrite = !blank && (state & StateOverwriteOnly) != 0;    // DVD+RW, BD-RE, DVD-RAM
            bool needsErase = erasable && (!blank || (state & StateEraseRequired) != 0);
            string problem = (state & StateUnsupported) != 0 ? "диск не поддерживается"
                : (state & StateDamaged) != 0 && !erasable ? "диск повреждён"
                : (state & StateWriteProtected) != 0 ? "диск защищён от записи"
                : blank || needsErase || overwrite ? ""
                : "диск не пустой - нужна чистая болванка или перезаписываемый диск (RW)";

            // На RW после стирания доступна вся ёмкость, а не остаток после старых сессий.
            long free = blank && !needsErase ? (long)(int)fmt.FreeSectorsOnMedia
                : needsErase || overwrite ? (long)(int)fmt.TotalSectorsOnMedia
                : 0;
            var speeds = new List<int>();
            try
            {
                foreach (object s in (object[])fmt.SupportedWriteSpeeds)
                    speeds.Add(Convert.ToInt32(s));
            }
            catch (COMException)
            {
                // Скорости не сообщаются - останется «максимальная».
            }

            return new MediaInfo(typeName, true, blank, (state & StateAppendable) != 0, free, per1x,
                speeds.Distinct().OrderByDescending(x => x).ToList(), problem, needsErase, overwrite);
        }
        finally
        {
            Release(fmt);
            Release(rec);
        }
    }

    /// <summary>
    /// Пишет ISO и закрывает диск. Чистый диск или DVD+RW/BD-RE (поверх старых данных);
    /// CD-RW и DVD-RW перед этим нужно стереть. Блокирует поток до конца записи.
    /// </summary>
    /// <param name="sectorsPerSecond">0 - максимальная скорость.</param>
    public static void Burn(string recorderId, string isoPath, int sectorsPerSecond,
        Action<BurnProgress> progress, CancellationToken ct)
    {
        dynamic rec = Create("IMAPI2.MsftDiscRecorder2");
        dynamic fmt = Create("IMAPI2.MsftDiscFormat2Data");
        IStream? stream = null;
        IConnectionPoint? cp = null;
        int cookie = 0;
        try
        {
            rec.InitializeDiscRecorder(recorderId);
            fmt.Recorder = rec;
            fmt.ClientName = "CdRescue";
            fmt.ForceMediaToBeClosed = true;
            fmt.SetWriteSpeed(sectorsPerSecond > 0 ? sectorsPerSecond : -1, false);

            SHCreateStreamOnFileEx(isoPath, StgmRead | StgmShareDenyWrite, 0, false, IntPtr.Zero, out stream);

            var sink = new ProgressSink(progress);
            var container = (IConnectionPointContainer)(object)fmt;
            var iid = typeof(DDiscFormat2DataEvents).GUID;
            container.FindConnectionPoint(ref iid, out var point);
            if (point is null)
                throw new InvalidOperationException("Служба записи не отдаёт события прогресса.");
            point.Advise(sink, out cookie);
            cp = point;

            // Отмена из другого потока - тот же MTA, где создан объект записи.
            using var reg = ct.Register(() => Task.Run(() =>
            {
                try
                {
                    fmt.CancelWrite();
                }
                catch (COMException)
                {
                    // Запись уже закончилась.
                }
            }).Wait());

            fmt.Write(stream);
        }
        finally
        {
            if (cp is not null && cookie != 0)
                cp.Unadvise(cookie);
            if (stream is not null)
                Marshal.ReleaseComObject(stream);
            Release(fmt);
            Release(rec);
        }
    }

    private static bool CanErase(dynamic rec)
    {
        dynamic erase = Create("IMAPI2.MsftDiscFormat2Erase");
        try
        {
            return (bool)erase.IsRecorderSupported(rec) && (bool)erase.IsCurrentMediaSupported(rec);
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            Release(erase);
        }
    }

    /// <summary>
    /// Стирает CD-RW или DVD-RW. Быстрое стирание удаляет оглавление (около минуты), полное -
    /// перезаписывает всю поверхность (десятки минут). Прервать стирание нельзя.
    /// </summary>
    /// <param name="progress">Прошло секунд и сколько всего ожидается.</param>
    public static void Erase(string recorderId, bool full, Action<int, int> progress)
    {
        dynamic rec = Create("IMAPI2.MsftDiscRecorder2");
        dynamic erase = Create("IMAPI2.MsftDiscFormat2Erase");
        IConnectionPoint? cp = null;
        int cookie = 0;
        try
        {
            rec.InitializeDiscRecorder(recorderId);
            erase.Recorder = rec;
            erase.ClientName = "CdRescue";
            erase.FullErase = full;

            var container = (IConnectionPointContainer)(object)erase;
            var iid = typeof(DDiscFormat2EraseEvents).GUID;
            container.FindConnectionPoint(ref iid, out var point);
            if (point is not null)
            {
                point.Advise(new EraseSink(progress), out cookie);
                cp = point;
            }

            erase.EraseMedia();
        }
        finally
        {
            if (cp is not null && cookie != 0)
                cp.Unadvise(cookie);
            Release(erase);
            Release(rec);
        }
    }

    private static dynamic Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!;

    private static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o))
            Marshal.ReleaseComObject(o);
    }

    private const uint StgmRead = 0x0;
    private const uint StgmShareDenyWrite = 0x20;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create,
        IntPtr template, out IStream stream);

    /// <summary>События записи IMAPI2 - dispinterface, вызов приходит через IDispatch::Invoke.</summary>
    [ComImport, Guid("2735413C-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    internal interface DDiscFormat2DataEvents
    {
        [DispId(0x200)]
        void Update([MarshalAs(UnmanagedType.IDispatch)] object sender, [MarshalAs(UnmanagedType.IDispatch)] object args);
    }

    /// <summary>События стирания: прошло секунд и ожидаемая длительность.</summary>
    [ComImport, Guid("2735413A-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    internal interface DDiscFormat2EraseEvents
    {
        [DispId(0x200)]
        void Update([MarshalAs(UnmanagedType.IDispatch)] object sender, int elapsedSeconds, int estimatedTotalSeconds);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class EraseSink(Action<int, int> report) : DDiscFormat2EraseEvents
    {
        public void Update(object sender, int elapsedSeconds, int estimatedTotalSeconds) =>
            report(elapsedSeconds, estimatedTotalSeconds);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ProgressSink(Action<BurnProgress> report) : DDiscFormat2DataEvents
    {
        public void Update(object sender, object args)
        {
            try
            {
                dynamic a = args;
                int action = (int)a.CurrentAction;
                long start = (int)a.StartLba;
                long count = (int)a.SectorCount;
                long written = Math.Max(0, (int)a.LastWrittenLba - start);
                report(new BurnProgress(
                    action >= 0 && action < Actions.Length ? Actions[action] : $"этап {action}",
                    Math.Min(written, count), count, (int)a.RemainingTime));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // Прогресс - не повод прерывать запись.
            }
            finally
            {
                Marshal.ReleaseComObject(args);
            }
        }
    }
}
