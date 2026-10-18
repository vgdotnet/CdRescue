using System.Runtime.InteropServices;
using CdRescue.Rescue;

namespace CdRescue.Burn;

internal readonly record struct VerifyResult(long Sectors, long Mismatched, long Unreadable, long FirstBad)
{
    public bool Ok => Mismatched == 0 && Unreadable == 0;
}

/// <summary>Сверяет записанный диск с образом посекторно, читая мимо кэша привода.</summary>
internal static unsafe class BurnVerifier
{
    private const int S = Rescuer.SectorSize;
    private const int Block = Rescuer.BlockSectors;

    public static VerifyResult Verify(char letter, string isoPath, Action<long, long> progress, CancellationToken ct)
    {
        using var cd = new CdDrive(letter);
        using var iso = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        long total = iso.Length / S;

        var (sectors, _) = cd.ReadCapacity();
        if (sectors < total)
            throw new InvalidOperationException(
                $"На диске {sectors:N0} секторов, а в образе {total:N0} - записано не всё.");

        byte* disc = (byte*)NativeMemory.AlignedAlloc(Block * S, 4096);
        var file = new byte[Block * S];
        long mismatched = 0, unreadable = 0, firstBad = -1;
        try
        {
            for (long lba = 0; lba < total && !ct.IsCancellationRequested; lba += Block)
            {
                int n = (int)Math.Min(Block, total - lba);
                iso.Position = lba * S;
                iso.ReadExactly(file, 0, n * S);

                if (cd.Read10((uint)lba, (ushort)n, disc, fua: true).Ok)
                {
                    for (int k = 0; k < n; k++)
                        if (!new ReadOnlySpan<byte>(disc + k * S, S).SequenceEqual(file.AsSpan(k * S, S)))
                            Bad(ref mismatched, lba + k);
                }
                else
                {
                    // Блок не прочитался - уточняем по секторам.
                    for (int k = 0; k < n; k++)
                    {
                        if (!cd.Read10((uint)(lba + k), 1, disc, fua: true).Ok)
                            Bad(ref unreadable, lba + k);
                        else if (!new ReadOnlySpan<byte>(disc, S).SequenceEqual(file.AsSpan(k * S, S)))
                            Bad(ref mismatched, lba + k);
                    }
                }

                if (lba % (Block * 64) == 0)
                    progress(lba + n, total);
            }
            progress(total, total);
        }
        finally
        {
            NativeMemory.AlignedFree(disc);
        }

        return new VerifyResult(total, mismatched, unreadable, firstBad);

        void Bad(ref long counter, long lba)
        {
            counter++;
            if (firstBad < 0)
                firstBad = lba;
        }
    }
}
