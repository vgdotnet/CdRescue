using System.Text;

namespace CdRescue.Rescue;

internal sealed record DamagedFile(string Path, long Size, int BadSectors, int PartialSectors, long FirstDamageOffset);

internal sealed record DamagedStructure(string What, IReadOnlyList<long> Lbas, string Consequence);

/// <summary>Что из файлов и служебных структур образа задето непрочитанными секторами.</summary>
internal sealed class DamageReport
{
    public string? VolumeLabel { get; init; }
    public bool FromDrive { get; init; }         // построен после проверки диска, образа нет
    public long TotalSectors { get; init; }
    public long Unreadable { get; init; }        // плохие, пропущенные и не читавшиеся
    public long Partial { get; init; }
    public long NotTried { get; init; }          // из Unreadable - ещё не читались вовсе
    public long InFiles { get; init; }
    public long InStructures { get; init; }
    public long InFree { get; init; }
    public long InSystemArea { get; init; }
    public long BeyondVolume { get; init; }
    public long Unknown { get; init; }           // файловая система не разобрана
    public int FilesTotal { get; init; }
    public long FilesBytes { get; init; }
    public required IReadOnlyList<DamagedFile> Files { get; init; }
    public required IReadOnlyList<DamagedStructure> Structures { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    public long Damaged => Unreadable + Partial;

    public string Verdict
    {
        get
        {
            if (Damaged == 0)
                return "Образ полный, повреждений нет.";
            if (Unknown > 0)
                return "Файловую систему разобрать не удалось - какие файлы задеты, неизвестно.";
            if (FilesTotal == 0 && Structures.Count > 0)
                return "Корневые каталоги не прочитаны - список файлов получить не удалось.";

            var sb = new StringBuilder();
            if (Files.Count == 0 && Structures.Count == 0)
                sb.Append(FromDrive
                    ? "Повреждения только вне файлов - данные не пострадали, снятый образ можно будет прожигать."
                    : "Повреждения только вне файлов - данные не пострадали, образ можно прожигать.");
            else if (Files.Count > 0)
                sb.Append($"Повреждено файлов: {Files.Count} из {FilesTotal}. Остальные целы. "
                    + (FromDrive
                        ? "Если не дочитать их при снятии образа, на прожжённой копии они будут читаться без ошибок, но с испорченным содержимым."
                        : "Прожиг образа как есть даст копию, где эти файлы читаются без ошибок, но с испорченным содержимым."));
            if (Structures.Count > 0)
                sb.Append((sb.Length > 0 ? " " : "") + "Повреждены служебные структуры - список файлов может быть неполным.");
            if (NotTried > 0)
                sb.Append($" {NotTried:N0} секторов ещё не читались - отчёт предварительный.");
            return sb.ToString();
        }
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Отчёт о повреждениях" + (VolumeLabel is { Length: > 0 } ? $" - том «{VolumeLabel}»" : ""));
        sb.AppendLine(Verdict);
        sb.AppendLine();
        sb.AppendLine($"Секторов {(FromDrive ? "на диске" : "в образе")}: {TotalSectors:N0}");
        sb.AppendLine($"Не прочитано: {Unreadable:N0}" + (NotTried > 0 ? $" (из них не читались вовсе: {NotTried:N0})" : ""));
        if (Partial > 0)
            sb.AppendLine($"Восстановлено частично: {Partial:N0}");
        if (Damaged > 0)
        {
            sb.AppendLine("Где повреждения:");
            Line(sb, "в файлах", InFiles);
            Line(sb, "в служебных структурах", InStructures);
            Line(sb, "в свободном месте", InFree);
            Line(sb, "в системной области (секторы 0-15, обычно пустые)", InSystemArea);
            Line(sb, "за пределами файловой системы", BeyondVolume);
            Line(sb, "без разбора файловой системы", Unknown);
        }
        sb.AppendLine($"Файлов: {FilesTotal:N0} ({FilesBytes / 1048576.0:N1} МБ), повреждено: {Files.Count:N0}");

        if (Files.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Повреждённые файлы:");
            foreach (var f in Files)
            {
                sb.Append($"  {f.Path} - {FormatSize(f.Size)}, ");
                sb.Append($"не прочитано секторов: {f.BadSectors}");
                if (f.PartialSectors > 0)
                    sb.Append($", частично: {f.PartialSectors}");
                sb.AppendLine($", первая ошибка со смещения {f.FirstDamageOffset:N0}");
            }
        }

        if (Structures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Повреждённые служебные структуры:");
            foreach (var s in Structures)
                sb.AppendLine($"  {s.What}, LBA {string.Join(", ", s.Lbas.Take(10))}{(s.Lbas.Count > 10 ? ", ..." : "")} - {s.Consequence}");
        }

        if (Notes.Count > 0)
        {
            sb.AppendLine();
            foreach (var n in Notes)
                sb.AppendLine("Примечание: " + n);
        }
        return sb.ToString().TrimEnd();
    }

    private static void Line(StringBuilder sb, string what, long n)
    {
        if (n > 0)
            sb.AppendLine($"  {what}: {n:N0}");
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1048576 => $"{bytes / 1024.0:N1} КБ",
        _ => $"{bytes / 1048576.0:N1} МБ",
    };
}

/// <summary>
/// Разбирает ISO 9660 и Joliet в снятом образе и сопоставляет непрочитанные сектора с файлами.
/// Служебным секторам доверяем, только если карта отмечает их прочитанными: на месте плохих
/// в образе нули или догадка, и разбирать их - значит выдумать файлы.
/// </summary>
internal static class DamageAnalyzer
{
    private const int S = Rescuer.SectorSize;
    private const int SystemArea = 16;

    private sealed class Owner
    {
        public required string What { get; init; }
        public string? Consequence { get; init; }
        public IsoFile? File { get; init; }
    }

    private sealed class IsoFile
    {
        public required string Path { get; init; }
        public long Size { get; set; }
        public int Bad;
        public int Partial;
        public long FirstDamage = long.MaxValue;
    }

    private readonly record struct Extent(long Start, long Count, Owner Owner, long FileOffset);

    /// <summary>По снятому образу.</summary>
    public static DamageReport Analyze(string imagePath, SectorMap map)
    {
        using var img = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Analyze(map, lba =>
        {
            if ((lba + 1) * S > img.Length)
                return null;
            var sec = new byte[S];
            img.Position = lba * S;
            img.ReadExactly(sec);
            return sec;
        });
    }

    /// <summary>
    /// По любому источнику секторов - образу или самому приводу после проверки диска.
    /// readSector вызывается только для секторов, которые карта отмечает прочитанными;
    /// null - сектор сейчас не прочитался.
    /// </summary>
    public static DamageReport Analyze(SectorMap map, Func<long, byte[]?> readSector, bool fromDrive = false)
    {
        var extents = new List<Extent>();
        var files = new Dictionary<(long Lba, long Size), IsoFile>();
        var notes = new List<string>();

        var rereadFailed = new List<long>();

        byte[]? Read(long lba)
        {
            if (lba < 0 || lba >= map.Length || map[lba] != SectorMap.Good)
                return null;
            var sec = readSector(lba);
            if (sec is null)
                rereadFailed.Add(lba); // по карте прочитан, а сейчас нет - пограничный сектор
            return sec;
        }

        // Дескрипторы тома: с сектора 16 до терминатора.
        byte[]? pvd = null, joliet = null;
        bool udf = false;
        var descriptors = new Owner { What = "дескрипторы тома", Consequence = "файловая система могла быть не найдена" };
        long lba = SystemArea;
        for (; lba < SystemArea + 64 && lba < map.Length; lba++)
        {
            var sec = Read(lba);
            if (sec is null)
                continue;
            string id = Encoding.ASCII.GetString(sec, 1, 5);
            if (id == "CD001")
            {
                if (sec[0] == 255)
                    break;
                if (sec[0] == 1)
                    pvd ??= sec;
                else if (sec[0] == 2 && sec[88] == 0x25 && sec[89] == 0x2F && sec[90] is 0x40 or 0x43 or 0x45)
                    joliet ??= sec;
            }
            else if (id is "NSR02" or "NSR03")
                udf = true;
            else if (id is not ("BEA01" or "TEA01" or "BOOT2" or "CDW02"))
                break;
        }
        extents.Add(new Extent(SystemArea, lba - SystemArea + 1, descriptors, 0));

        var primary = joliet ?? pvd;
        long volumeSpace = primary is null ? map.Length : Le32(primary, 80);
        string? label = joliet is not null
            ? Encoding.BigEndianUnicode.GetString(joliet, 40, 32).Trim('\0', ' ')
            : pvd is not null ? Encoding.ASCII.GetString(pvd, 40, 32).Trim('\0', ' ') : null;

        if (primary is null)
            notes.Add(udf
                ? "Файловая система ISO 9660 не найдена, на диске только UDF - его разбор пока не поддерживается."
                : "Файловая система ISO 9660 не найдена (или её дескриптор не прочитан).");
        if (udf && primary is not null)
            notes.Add("На диске есть и UDF; отчёт построен по ISO 9660/Joliet - обычно они описывают те же файлы.");
        if (pvd is not null && joliet is not null)
            notes.Add("Каталоги записаны дважды (ISO 9660 и Joliet): если повреждена одна копия, файлы найдены по второй.");

        // Joliet первым - его имена длинные и в Юникоде; дубликаты из ISO 9660 отбрасываются по экстенту.
        foreach (var (desc, isJoliet) in new[] { (joliet, true), (pvd, false) })
        {
            if (desc is null)
                continue;
            string tree = isJoliet ? "Joliet" : "ISO 9660";
            AddPathTables(desc, tree, extents);
            WalkTree(desc, isJoliet, tree, Read, extents, files);
        }

        if (rereadFailed.Count > 0)
            notes.Add($"Служебные сектора {string.Join(", ", rereadFailed.Take(10))}{(rereadFailed.Count > 10 ? ", ..." : "")} "
                + "раньше читались, а при анализе - нет; список файлов может быть неполным.");

        // Кому принадлежит каждый сектор; при пересечении - первому записанному.
        var owner = new int[map.Length];
        Array.Fill(owner, -1);
        for (int i = 0; i < extents.Count; i++)
        {
            var e = extents[i];
            long end = Math.Min(map.Length, e.Start + e.Count);
            for (long k = Math.Max(0, e.Start); k < end; k++)
                if (owner[k] < 0)
                    owner[k] = i;
        }

        long unreadable = 0, partial = 0, notTried = 0, inFiles = 0, inStructures = 0, inFree = 0, inSystem = 0, beyond = 0, unknown = 0;
        var structureLbas = new Dictionary<Owner, List<long>>();

        for (long i = 0; i < map.Length; i++)
        {
            byte st = map[i];
            if (st == SectorMap.Good)
                continue;
            if (st == SectorMap.Partial)
                partial++;
            else
                unreadable++;
            if (st is SectorMap.Untried or SectorMap.Skipped)
                notTried++;

            if (primary is null)
            {
                unknown++;
                continue;
            }

            int o = owner[i];
            if (o < 0)
            {
                if (i < SystemArea)
                    inSystem++;
                else if (i >= volumeSpace)
                    beyond++;
                else
                    inFree++;
                continue;
            }

            var e = extents[o];
            if (e.Owner.File is { } f)
            {
                inFiles++;
                if (st == SectorMap.Partial)
                    f.Partial++;
                else
                    f.Bad++;
                f.FirstDamage = Math.Min(f.FirstDamage, e.FileOffset + (i - e.Start) * S);
            }
            else
            {
                inStructures++;
                if (!structureLbas.TryGetValue(e.Owner, out var list))
                    structureLbas[e.Owner] = list = new List<long>();
                list.Add(i);
            }
        }

        return new DamageReport
        {
            VolumeLabel = label,
            FromDrive = fromDrive,
            TotalSectors = map.Length,
            Unreadable = unreadable,
            Partial = partial,
            NotTried = notTried,
            InFiles = inFiles,
            InStructures = inStructures,
            InFree = inFree,
            InSystemArea = inSystem,
            BeyondVolume = beyond,
            Unknown = unknown,
            FilesTotal = files.Count,
            FilesBytes = files.Values.Sum(f => f.Size),
            Files = files.Values
                .Where(f => f.Bad + f.Partial > 0)
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Select(f => new DamagedFile(f.Path, f.Size, f.Bad, f.Partial, f.FirstDamage))
                .ToList(),
            Structures = structureLbas
                .Select(kv => new DamagedStructure(kv.Key.What, kv.Value, kv.Key.Consequence ?? ""))
                .ToList(),
            Notes = notes,
        };
    }

    private static void AddPathTables(byte[] desc, string tree, List<Extent> extents)
    {
        long size = Le32(desc, 132);
        long count = (size + S - 1) / S;
        var o = new Owner
        {
            What = $"таблица путей ({tree})",
            Consequence = "не страшно - те же сведения есть в каталогах",
        };
        foreach (long loc in new[] { Le32(desc, 140), Le32(desc, 144), Be32(desc, 148), Be32(desc, 152) })
            if (loc > 0 && count > 0)
                extents.Add(new Extent(loc, count, o, 0));
    }

    private static void WalkTree(byte[] desc, bool joliet, string tree, Func<long, byte[]?> read,
        List<Extent> extents, Dictionary<(long, long), IsoFile> files)
    {
        var queue = new Queue<(string Path, long Lba, long Size)>();
        var visited = new HashSet<long>();
        queue.Enqueue(("/", Le32(desc, 156 + 2), Le32(desc, 156 + 10)));
        visited.Add(Le32(desc, 156 + 2));

        while (queue.Count > 0)
        {
            var (path, dirLba, dirSize) = queue.Dequeue();
            long sectors = (dirSize + S - 1) / S;
            extents.Add(new Extent(dirLba, sectors, new Owner
            {
                What = $"каталог {path} ({tree})",
                Consequence = "файлы и папки в нём могли не попасть в отчёт",
            }, 0));

            IsoFile? multi = null;      // файл из нескольких экстентов, который ещё продолжается
            string? skipName = null;    // продолжение файла, уже найденного по другому дереву

            for (long k = 0; k < sectors; k++)
            {
                var sec = read(dirLba + k);
                if (sec is null)
                    continue;

                // Записи каталога не пересекают границу сектора; нулевая длина - конец сектора.
                for (int off = 0; off < S && sec[off] != 0; off += sec[off])
                {
                    int len = sec[off];
                    int nameLen = off + 33 <= S ? sec[off + 32] : 0;
                    if (off + len > S || len < 34 || 33 + nameLen > len)
                        break;
                    if (nameLen == 1 && sec[off + 33] <= 1)
                        continue; // «.» и «..»

                    byte flags = sec[off + 25];
                    long ext = Le32(sec, off + 2);
                    long size = Le32(sec, off + 10);
                    string name = DecodeName(sec, off + 33, nameLen, joliet);
                    bool more = (flags & 0x80) != 0;

                    if ((flags & 0x02) != 0)
                    {
                        if (visited.Add(ext))
                            queue.Enqueue(($"{path}{name}/", ext, size));
                        continue;
                    }

                    string full = path + name;
                    if (skipName == full)
                    {
                        if (!more)
                            skipName = null;
                        continue;
                    }

                    IsoFile file;
                    long fileOffset;
                    if (multi is not null && multi.Path == full)
                    {
                        file = multi;
                        fileOffset = file.Size;
                        file.Size += size;
                    }
                    else if (files.ContainsKey((ext, size)))
                    {
                        skipName = more ? full : null;
                        multi = null;
                        continue;
                    }
                    else
                    {
                        file = new IsoFile { Path = full, Size = size };
                        files[(ext, size)] = file;
                        fileOffset = 0;
                    }

                    if (size > 0)
                        extents.Add(new Extent(ext, (size + S - 1) / S, new Owner { What = full, File = file }, fileOffset));
                    multi = more ? file : null;
                }
            }
        }
    }

    private static string DecodeName(byte[] sec, int at, int len, bool joliet)
    {
        string name = joliet
            ? Encoding.BigEndianUnicode.GetString(sec, at, len & ~1)
            : Encoding.Latin1.GetString(sec, at, len);
        int semicolon = name.IndexOf(';');
        if (semicolon >= 0)
            name = name[..semicolon];
        return joliet ? name : name.TrimEnd('.');
    }

    private static long Le32(byte[] b, int at) => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);

    private static long Be32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
}
