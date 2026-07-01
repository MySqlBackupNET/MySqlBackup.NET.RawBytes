using System;
using System.Collections.Generic;

namespace MySqlBackup.NET.RawBytes.Wire
{
    /// <summary>
    /// Minimal, dependency-free replacement for <c>ArrayPool&lt;byte&gt;.Shared</c>.
    ///
    /// Rounds each request up to the next power of two and keeps a small free list
    /// per size bucket, so the streaming dump/restore hot paths can rent and return
    /// buffers without generating steady-state garbage — exactly the property the
    /// original ArrayPool gave us, but implemented in-house so the library has zero
    /// NuGet/assembly dependencies beyond the BCL.
    ///
    /// Semantics that callers rely on:
    ///   • Rent(n) returns an array of length &gt;= n (the existing engine code already
    ///     tracks the real payload length separately, so an oversized buffer is fine).
    ///   • Return(array) is safe to call with null, with a zero-length array, or with
    ///     an array this pool never handed out — such calls are simply ignored.
    ///   • Thread-safe: the shared instance may be used from multiple threads.
    /// </summary>
    internal sealed class SimpleBufferPool
    {
        public static readonly SimpleBufferPool Shared = new SimpleBufferPool();

        // Cap on retained arrays per size bucket, to bound idle memory.
        const int MaxArraysPerBucket = 8;

        readonly object _gate = new object();
        readonly Dictionary<int, Stack<byte[]>> _buckets = new Dictionary<int, Stack<byte[]>>();

        public byte[] Rent(int minimumLength)
        {
            if (minimumLength < 0)
                throw new ArgumentOutOfRangeException("minimumLength");

            int size = RoundUpPow2(minimumLength < 16 ? 16 : minimumLength);

            lock (_gate)
            {
                Stack<byte[]> stack;
                if (_buckets.TryGetValue(size, out stack) && stack.Count > 0)
                    return stack.Pop();
            }

            return new byte[size];
        }

        public void Return(byte[] array)
        {
            if (array == null || array.Length == 0)
                return;

            int size = array.Length;

            // Only pool the exact power-of-two sizes we hand out; ignore anything else.
            if ((size & (size - 1)) != 0)
                return;

            lock (_gate)
            {
                Stack<byte[]> stack;
                if (!_buckets.TryGetValue(size, out stack))
                {
                    stack = new Stack<byte[]>();
                    _buckets[size] = stack;
                }

                if (stack.Count < MaxArraysPerBucket)
                    stack.Push(array);
            }
        }

        static int RoundUpPow2(int v)
        {
            // Next power of two >= v (v already guaranteed >= 16 by the caller).
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            return v + 1;
        }
    }
}
