using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;


public static class ConsistentHashGenerator
{
    private const uint Prime2 = 2246822519U;
    private const uint Prime3 = 3266489917U;
    private const uint Prime4 = 668265263U;
    private const uint Prime5 = 374761393U;

    public static int Combine<T1>(T1 value1)
    {
        uint hc1 = (uint)(value1?.GetHashCode() ?? 0);

        uint hash = MixEmptyState();
        hash += 4;

        hash = QueueRound(hash, hc1);

        hash = MixFinal(hash);
        return (int)hash;
    }

    public static int Combine<T1, T2, T3>(T1 value1, T2 value2, T3 value3)
    {
        uint hc1 = (uint)(value1?.GetHashCode() ?? 0);
        uint hc2 = (uint)(value2?.GetHashCode() ?? 0);
        uint hc3 = (uint)(value3?.GetHashCode() ?? 0);

        uint hash = MixEmptyState();
        hash += 12;

        hash = QueueRound(hash, hc1);
        hash = QueueRound(hash, hc2);
        hash = QueueRound(hash, hc3);

        hash = MixFinal(hash);
        return (int)hash;
    }

    public static int Combine<T1, T2>(T1 value1, T2 value2)
    {
        uint hc1 = (uint)(value1?.GetHashCode() ?? 0);
        uint hc2 = (uint)(value2?.GetHashCode() ?? 0);

        uint hash = MixEmptyState();
        hash += 8;

        hash = QueueRound(hash, hc1);
        hash = QueueRound(hash, hc2);

        hash = MixFinal(hash);
        return (int)hash;
    }

    private static uint MixEmptyState()
    {
        return Prime5;
    }

    private static uint QueueRound(uint hash, uint queuedValue)
    {
        return RotateLeft(hash + queuedValue * Prime3, 17) * Prime4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint RotateLeft(uint value, int offset)
            => (value << offset) | (value >> (32 - offset));

    private static uint MixFinal(uint hash)
    {
        hash ^= hash >> 15;
        hash *= Prime2;
        hash ^= hash >> 13;
        hash *= Prime3;
        hash ^= hash >> 16;
        return hash;
    }
}

