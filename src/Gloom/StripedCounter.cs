using System.Numerics;
using System.Runtime.InteropServices;

namespace Gloom;

/// <summary>
/// A concurrent counter whose increments are spread over cache-line padded stripes, so parallel
/// writers do not serialize on one shared line (a single atomic counter measured a ~10x
/// throughput cliff under parallel writes in the Go library). The total is exact; reading it is
/// not a linearizable snapshot under concurrent writes. Port of counter.go.
/// </summary>
internal sealed class StripedCounter
{
    /// <summary>Caps the counter at 8 KiB of stripes on machines with very many cores.</summary>
    internal const int MAX_STRIPES = 128;

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct PaddedCounter
    {
        [FieldOffset(0)] public long Value;
    }

    private readonly PaddedCounter[] _stripes;
    private readonly ulong           _mask;

    public StripedCounter() : this(Environment.ProcessorCount) { }

    public StripedCounter(int writers)
    {
        int n    = (int)Math.Min(BitOperations.RoundUpToPowerOf2((uint)Math.Max(writers, 1)), MAX_STRIPES);
        _stripes = new PaddedCounter[n];
        _mask    = (ulong)n - 1;
    }

    internal int StripeCount => _stripes.Length;

    /// <summary>Increments the stripe selected by <paramref name="index"/> (callers pass the well-distributed block index).</summary>
    public void Increment(ulong index) => Interlocked.Increment(ref _stripes[(int)(index & _mask)].Value);

    public ulong Load()
    {
        ulong total = 0;
        for (int i = 0; i < _stripes.Length; i++) total += (ulong)Volatile.Read(ref _stripes[i].Value);
        return total;
    }
}
