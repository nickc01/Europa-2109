using HPCsharp;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Unity.VisualScripting;

public static class CustomAlgorithms
{
    private static class TempArrayHolder<T>
    {
        public static T[] TEMP = null;
        public static T[] TEMP2 = null;
    }

    /*static Func<S, T> CreateGetter<S, T>(FieldInfo field)
    {
        string methodName = field.ReflectedType.FullName + ".get_" + field.Name;
        DynamicMethod setterMethod = new DynamicMethod(methodName, typeof(T), new Type[1] { typeof(S) }, true);
        ILGenerator gen = setterMethod.GetILGenerator();
        if (field.IsStatic)
        {
            gen.Emit(OpCodes.Ldsfld, field);
        }
        else
        {
            gen.Emit(OpCodes.Ldarg_0);
            gen.Emit(OpCodes.Ldfld, field);
        }
        gen.Emit(OpCodes.Ret);
        return (Func<S, T>)setterMethod.CreateDelegate(typeof(Func<S, T>));
    }

    static Action<S, T> CreateSetter<S, T>(FieldInfo field)
    {
        string methodName = field.ReflectedType.FullName + ".set_" + field.Name;
        DynamicMethod setterMethod = new DynamicMethod(methodName, null, new Type[2] { typeof(S), typeof(T) }, true);
        ILGenerator gen = setterMethod.GetILGenerator();
        if (field.IsStatic)
        {
            gen.Emit(OpCodes.Ldarg_1);
            gen.Emit(OpCodes.Stsfld, field);
        }
        else
        {
            gen.Emit(OpCodes.Ldarg_0);
            gen.Emit(OpCodes.Ldarg_1);
            gen.Emit(OpCodes.Stfld, field);
        }
        gen.Emit(OpCodes.Ret);
        return (Action<S, T>)setterMethod.CreateDelegate(typeof(Action<S, T>));
    }*/

    /*public static Span<T> GetSpanFromList<T>(List<T> list)
    {
        Delegate.CreateDelegate()
    }*/


    public static int ThresholdParallelMin { get; set; } = 16384;

    public static int MinSsePar(this int[] arrayToMin)
    {
        if (arrayToMin == null)
        {
            throw new ArgumentNullException("Min cannot be determined for a null array");
        }

        if (arrayToMin.Length == 0)
        {
            throw new ArgumentException("Min cannot be determined for an empty array");
        }

        return arrayToMin.MinSseParInner(0, arrayToMin.Length - 1);
    }

    private static int MinSseInner(this int[] arrayToMin, int l, int r)
    {
        Vector<int> left = default(Vector<int>);
        int num = l + (r - l + 1) / Vector<int>.Count * Vector<int>.Count;
        int i = l;
        if (i < num)
        {
            left = new Vector<int>(arrayToMin, i);
            i += Vector<int>.Count;
        }

        for (; i < num; i += Vector<int>.Count)
        {
            left = Vector.Min(right: new Vector<int>(arrayToMin, i), left: left);
        }

        int num2 = left[0];
        for (int j = 1; j < Vector<int>.Count; j++)
        {
            num2 = Math.Min(num2, left[j]);
        }

        for (; i <= r; i++)
        {
            num2 = Math.Min(num2, arrayToMin[i]);
        }

        return num2;
    }

    private static int MinSseParInner(this int[] arrayToMin, int l, int r)
    {
        if (r - l + 1 <= ThresholdParallelMin || r - l + 1 <= 2)
        {
            return arrayToMin.MinSseInner(l, r - l + 1);
        }

        int i = (r + l) / 2;
        int minLeft = 0;
        int minRight = 0;
        Parallel.Invoke(delegate
        {
            minLeft = arrayToMin.MinSseParInner(l, i);
        }, delegate
        {
            minRight = arrayToMin.MinSseParInner(i + 1, r);
        });
        return Math.Min(minLeft, minRight);
    }

    private static void CopyToArrayParallelInnerDac<T>(this List<T> src, int srcStart, T[] dst, int dstStart, int length, (int minWorkQuanta, int degreeOfParallelism)? parSettings = null)
    {
        if (length <= 0)
        {
            return;
        }

        (int minWorkQuanta, int degreeOfParallelism) = parSettings ?? (65536, Environment.ProcessorCount);
        if (length <= minWorkQuanta || degreeOfParallelism == 1)
        {
            src.CopyTo(srcStart, dst, dstStart, length);
            return;
        }

        int lengthFirstHalf = length / 2;
        int lengthSecondHalf = length - lengthFirstHalf;
        Parallel.Invoke(delegate
        {
            src.CopyToArrayParallelInnerDac(srcStart, dst, dstStart, lengthFirstHalf, new (int, int)?((minWorkQuanta, degreeOfParallelism)));
        }, delegate
        {
            src.CopyToArrayParallelInnerDac(srcStart + lengthFirstHalf, dst, dstStart + lengthFirstHalf, lengthSecondHalf, new (int, int)?((minWorkQuanta, degreeOfParallelism)));
        });
    }

    public static T[] ToArrayPar<T>(this List<T> src, (int minWorkQuanta, int degreeOfParallelism)? parSettings = null)
    {
        (int minWorkQuanta, int degreeOfParallelism) obj = parSettings ?? (65536, Environment.ProcessorCount / SystemAttributes.HyperthreadingNumberOfWays);
        int num = obj.minWorkQuanta;
        int item = obj.degreeOfParallelism;

        if (TempArrayHolder<T>.TEMP == null || TempArrayHolder<T>.TEMP.Length < src.Count)
        {
            TempArrayHolder<T>.TEMP = new T[src.Count];
        }

        T[] array = TempArrayHolder<T>.TEMP;//new T[src.Count];
        if (num * item < src.Count)
        {
            num = src.Count / item;
        }

        src.CopyToArrayParallelInnerDac(0, array, 0, src.Count, new (int, int)?((num, item)));
        return array;
    }

    /*public static void SortMergeInPlaceAdaptivePar<T>(this T[] src, IComparer<T> comparer = null, int parallelThreshold = 24576)
    {
        try
        {
            T[] dst = new T[src.Length];
            if (parallelThreshold * Environment.ProcessorCount < src.Length)
            {
                parallelThreshold = src.Length / Environment.ProcessorCount;
            }

            src.SortMergeInnerPar(0, src.Length - 1, dst, srcToDst: false, comparer, parallelThreshold);
        }
        catch (OutOfMemoryException)
        {
            src.SortMergeInPlaceHybridInnerPar(0, src.Length - 1, comparer, parallelThreshold);
        }
    }

    public static void SortMergeInPlaceAdaptivePar<T>(this T[] src, int startIndex, int length, IComparer<T> comparer = null, int parallelThreshold = 24576)
    {
        try
        {
            T[] dst = new T[src.Length];
            if (parallelThreshold * Environment.ProcessorCount < src.Length)
            {
                parallelThreshold = src.Length / Environment.ProcessorCount;
            }

            src.SortMergeInnerPar(startIndex, startIndex + length - 1, dst, srcToDst: false, comparer, parallelThreshold);
        }
        catch (OutOfMemoryException)
        {
            src.SortMergeInPlaceHybridInnerPar(startIndex, startIndex + length - 1, comparer, parallelThreshold);
        }
    }*/

    public static void SortMergeAdaptivePar<T>(ref List<T> list, int startIndex, int length, IComparer<T> comparer = null, int parallelThreshold = 24576)
    {
        T[] array = ToArrayPar(list);
        if (parallelThreshold * Environment.ProcessorCount < list.Count)
        {
            parallelThreshold = list.Count / Environment.ProcessorCount;
        }

        SortMergeInPlaceAdaptivePar(array, startIndex, length, comparer, parallelThreshold);
        //ParallelAlgorithm.SortMergeInPlaceAdaptivePar
        //HPCsharp.ParallelAlgorithms.Copy.

        //list = new List<T>(array);
        //return array;

        list.Free();
        list = CustomXListPool.ToListPooled(array, length);
    }

    private static void SortMergeInPlaceAdaptivePar<T>(this T[] src, int startIndex, int length, IComparer<T> comparer = null, int parallelThreshold = 24576)
    {
        try
        {
            if (TempArrayHolder<T>.TEMP2 == null || TempArrayHolder<T>.TEMP2.Length < length)
            {
                TempArrayHolder<T>.TEMP2 = new T[length];
            }

            //T[] dst = new T[src.Length];
            //var dst = ArrayPool<T>.New(length);
            T[] dst = TempArrayHolder<T>.TEMP2;
            if (parallelThreshold * Environment.ProcessorCount < src.Length)
            {
                parallelThreshold = src.Length / Environment.ProcessorCount;
            }

            src.SortMergeInnerPar(startIndex, startIndex + length - 1, dst, srcToDst: false, comparer, parallelThreshold);
        }
        catch (OutOfMemoryException)
        {
            src.SortMergeInPlaceHybridInnerPar(startIndex, startIndex + length - 1, comparer, parallelThreshold);
        }
    }

    private static void SortMergeInnerPar<T>(this T[] src, int l, int r, T[] dst, bool srcToDst = true, IComparer<T> comparer = null, int parallelThreshold = 24576, int parallelMergeThreshold = 131072)
    {
        if (r < l)
        {
            return;
        }

        if (r == l)
        {
            if (srcToDst)
            {
                dst[l] = src[l];
            }

            return;
        }

        if (r - l <= HPCsharp.ParallelAlgorithm.SortMergeParallelInsertionThreshold)
        {
            Algorithm.InsertionSort(src, l, r - l + 1, comparer);
            if (srcToDst)
            {
                Array.Copy(src, l, dst, l, r - l + 1);
            }

            return;
        }

        if (r - l <= parallelThreshold)
        {
            Array.Sort(src, l, r - l + 1, comparer);
            if (srcToDst)
            {
                Array.Copy(src, l, dst, l, r - l + 1);
            }

            return;
        }

        int i = r / 2 + l / 2 + (r % 2 + l % 2) / 2;
        Parallel.Invoke(delegate
        {
            src.SortMergeInnerPar(l, i, dst, !srcToDst, comparer, parallelThreshold);
        }, delegate
        {
            src.SortMergeInnerPar(i + 1, r, dst, !srcToDst, comparer, parallelThreshold);
        });
        if (srcToDst)
        {
            MergeInnerFasterPar(src, l, i, i + 1, r, dst, l, comparer, parallelMergeThreshold);
        }
        else
        {
            MergeInnerFasterPar(dst, l, i, i + 1, r, src, l, comparer, parallelMergeThreshold);
        }
    }

    internal static void MergeInnerFasterPar<T>(T[] src, int p1, int r1, int p2, int r2, T[] dst, int p3, IComparer<T> comparer = null, int mergeParallelThreshold = 131072)
    {
        int a = r1 - p1 + 1;
        int b = r2 - p2 + 1;
        if (a < b)
        {
            Algorithm.Swap(ref p1, ref p2);
            Algorithm.Swap(ref r1, ref r2);
            Algorithm.Swap(ref a, ref b);
        }

        if (a == 0)
        {
            return;
        }

        if (a + b <= mergeParallelThreshold)
        {
            Algorithm.MergeFaster(src, p1, a, src, p2, b, dst, p3, comparer);
            return;
        }

        int q1 = p1 / 2 + r1 / 2 + (p1 % 2 + r1 % 2) / 2;
        int q2 = Algorithm.BinarySearch(src[q1], src, p2, r2, comparer);
        int q3 = p3 + (q1 - p1) + (q2 - p2);
        dst[q3] = src[q1];
        Parallel.Invoke(delegate
        {
            MergeInnerFasterPar(src, p1, q1 - 1, p2, q2 - 1, dst, p3, comparer, mergeParallelThreshold);
        }, delegate
        {
            MergeInnerFasterPar(src, q1 + 1, r1, q2, r2, dst, q3 + 1, comparer, mergeParallelThreshold);
        });
    }

    private static void SortMergeInPlaceHybridInnerPar<T>(this T[] src, int startIndex, int endIndex, IComparer<T> comparer = null, int threshold0 = 16384, int threshold1 = 262144, int threshold2 = 262144)
    {
        int num = endIndex - startIndex + 1;
        if (num <= 1)
        {
            return;
        }

        if (num <= threshold0)
        {
            Array.Sort(src, startIndex, num, comparer);
            return;
        }

        int midIndex = endIndex / 2 + startIndex / 2 + (endIndex % 2 + startIndex % 2) / 2;
        Parallel.Invoke(delegate
        {
            src.SortMergeInPlaceHybridInnerPar(startIndex, midIndex, comparer, threshold0, threshold1, threshold2);
        }, delegate
        {
            src.SortMergeInPlaceHybridInnerPar(midIndex + 1, endIndex, comparer, threshold0, threshold1, threshold2);
        });
        MergeDivideAndConquerInPlacePar(src, startIndex, midIndex, endIndex, comparer, threshold1, threshold2);
    }

    private static void MergeDivideAndConquerInPlacePar<T>(T[] arr, int startIndex, int midIndex, int endIndex, IComparer<T> comparer = null, int threshold0 = 16384, int threshold1 = 16384)
    {
        int num = midIndex - startIndex + 1;
        int num2 = endIndex - midIndex;
        if (num >= num2)
        {
            if (num2 <= 0)
            {
                return;
            }

            int q2 = startIndex / 2 + midIndex / 2 + (startIndex % 2 + midIndex % 2) / 2;
            int q4 = Algorithm.BinarySearch(arr[q2], arr, midIndex + 1, endIndex, comparer);
            int q6 = q2 + (q4 - midIndex - 1);
            BlockSwapReversalPar(arr, q2, midIndex, q4 - 1, threshold0);
            if (num < threshold1)
            {
                MergeDivideAndConquerInPlacePar(arr, startIndex, q2 - 1, q6 - 1, comparer);
                MergeDivideAndConquerInPlacePar(arr, q6 + 1, q4 - 1, endIndex, comparer);
                return;
            }

            Parallel.Invoke(delegate
            {
                MergeDivideAndConquerInPlacePar(arr, startIndex, q2 - 1, q6 - 1, comparer);
            }, delegate
            {
                MergeDivideAndConquerInPlacePar(arr, q6 + 1, q4 - 1, endIndex, comparer);
            });
        }
        else
        {
            if (num <= 0)
            {
                return;
            }

            int q1 = (midIndex + 1) / 2 + endIndex / 2 + ((midIndex + 1) % 2 + endIndex % 2) / 2;
            int q3 = Algorithm.BinarySearch(arr[q1], arr, startIndex, midIndex, comparer);
            int q5 = q3 + (q1 - midIndex - 1);
            BlockSwapReversalPar(arr, q3, midIndex, q1, threshold0);
            if (num < threshold1)
            {
                MergeDivideAndConquerInPlacePar(arr, startIndex, q3 - 1, q5 - 1, comparer);
                MergeDivideAndConquerInPlacePar(arr, q5 + 1, q1, endIndex, comparer);
                return;
            }

            Parallel.Invoke(delegate
            {
                MergeDivideAndConquerInPlacePar(arr, startIndex, q3 - 1, q5 - 1, comparer);
            }, delegate
            {
                MergeDivideAndConquerInPlacePar(arr, q5 + 1, q1, endIndex, comparer);
            });
        }
    }

    private static void BlockSwapReversalPar<T>(T[] array, int l, int m, int r, int threshold = 16384)
    {
        if (r - l + 1 < threshold)
        {
            array.Reversal(l, m);
            array.Reversal(m + 1, r);
            array.Reversal(l, r);
            return;
        }

        Parallel.Invoke(delegate
        {
            array.Reversal(l, m);
        }, delegate
        {
            array.Reversal(m + 1, r);
        });
        array.Reversal(l, r);
    }
}

