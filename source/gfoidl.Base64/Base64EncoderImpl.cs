﻿using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#if NETCOREAPP
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace gfoidl.Base64
{
    internal abstract class Base64EncoderImpl : IBase64Encoder
    {
#if NETCOREAPP
        protected static readonly Vector128<sbyte> s_sse_encodeShuffleVec;
        protected static readonly Vector128<sbyte> s_sse_decodeShuffleVec;

        protected static readonly Vector256<int>   s_avx_encodePermuteVec;
        protected static readonly Vector256<sbyte> s_avx_encodeShuffleVec;
        protected static readonly Vector256<sbyte> s_avx_decodeShuffleVec;
        protected static readonly Vector256<int>   s_avx_decodePermuteVec;
#endif
        //---------------------------------------------------------------------
#if NETCOREAPP3_0
        protected static readonly bool s_isMac = false;
#endif
        //---------------------------------------------------------------------
        static Base64EncoderImpl()
        {
#if NETCOREAPP3_0
            s_isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
#endif

#if NETCOREAPP
#if NETCOREAPP3_0
            if (Ssse3.IsSupported)
#else
            if (Sse2.IsSupported && Ssse3.IsSupported)
#endif
            {
                s_sse_encodeShuffleVec = Sse2.SetVector128(
                    10, 11, 9, 10,
                     7,  8, 6,  7,
                     4,  5, 3,  4,
                     1,  2, 0,  1
                );

                s_sse_decodeShuffleVec = Sse2.SetVector128(
                    -1, -1, -1, -1,
                    12, 13, 14,  8,
                     9, 10,  4,  5,
                     6,  0,  1,  2
                );
            }

#if NETCOREAPP3_0
            if (Avx2.IsSupported)
#else
            if (Avx.IsSupported && Avx2.IsSupported)
#endif
            {
                s_avx_encodePermuteVec = Avx.SetVector256(6, 5, 4, 3, 2, 1, 0, 0);

                s_avx_encodeShuffleVec = Avx.SetVector256(
                    10, 11,  9, 10,
                     7,  8,  6,  7,
                     4,  5,  3,  4,
                     1,  2,  0,  1,
                    14, 15, 13, 14,
                    11, 12, 10, 11,
                     8,  9,  7,  8,
                     5,  6,  4,  5
                );

                s_avx_decodeShuffleVec = Avx.SetVector256(
                    -1, -1, -1, -1,
                    12, 13, 14,  8,
                     9, 10,  4,  5,
                     6,  0,  1,  2,
                    -1, -1, -1, -1,
                    12, 13, 14,  8,
                     9, 10,  4,  5,
                     6,  0,  1,  2
                );

                s_avx_decodePermuteVec = Avx.SetVector256(-1, -1, 6, 5, 4, 2, 1, 0);
            }
#endif
        }
        //---------------------------------------------------------------------
#if NETSTANDARD2_0
        private const int MaxStackallocBytes = 256;
#endif
        public const int MaximumEncodeLength = int.MaxValue / 4 * 3; // 1610612733
        //---------------------------------------------------------------------
        public OperationStatus Encode(ReadOnlySpan<byte> data, Span<byte> encoded, out int consumed, out int written, bool isFinalBlock = true) => this.EncodeCore(data, encoded, out consumed, out written, isFinalBlock);
        public OperationStatus Encode(ReadOnlySpan<byte> data, Span<char> encoded, out int consumed, out int written, bool isFinalBlock = true) => this.EncodeCore(data, encoded, out consumed, out written, isFinalBlock);

        public OperationStatus Decode(ReadOnlySpan<byte> encoded, Span<byte> data, out int consumed, out int written, bool isFinalBlock = true) => this.DecodeCore(encoded, data, out consumed, out written, isFinalBlock);
        public OperationStatus Decode(ReadOnlySpan<char> encoded, Span<byte> data, out int consumed, out int written, bool isFinalBlock = true) => this.DecodeCore(encoded, data, out consumed, out written, isFinalBlock);
        //---------------------------------------------------------------------
        public abstract int GetEncodedLength(int sourceLength);
        public abstract int GetDecodedLength(ReadOnlySpan<byte> encoded);
        public abstract int GetDecodedLength(ReadOnlySpan<char> encoded);
        //---------------------------------------------------------------------
        public unsafe string Encode(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return string.Empty;

            int encodedLength = this.GetEncodedLength(data.Length);
#if NETCOREAPP
            fixed (byte* ptr = data)
            {
                return string.Create(encodedLength, (Ptr: (IntPtr)ptr, data.Length), (encoded, state) =>
                {
                    var srcBytes           = new Span<byte>(state.Ptr.ToPointer(), state.Length);
                    OperationStatus status = this.EncodeCore(srcBytes, encoded, out int consumed, out int written);

                    Debug.Assert(status         == OperationStatus.Done);
                    Debug.Assert(state.Length   == consumed);
                    Debug.Assert(encoded.Length == written);
                });
            }
#else
            Span<char> encoded = encodedLength <= MaxStackallocBytes / sizeof(char)
                ? stackalloc char[encodedLength]
                : new char[encodedLength];

            OperationStatus status = this.EncodeCore(data, encoded, out int consumed, out int written);
            Debug.Assert(status         == OperationStatus.Done);
            Debug.Assert(data.Length    == consumed);
            Debug.Assert(encoded.Length == written);

            fixed (char* ptr = encoded)
                return new string(ptr, 0, written);
#endif
        }
        //---------------------------------------------------------------------
        public byte[] Decode(ReadOnlySpan<char> encoded)
        {
            if (encoded.IsEmpty) return Array.Empty<byte>();

            int dataLength         = this.GetDecodedLength(encoded);
            byte[] data            = new byte[dataLength];
            OperationStatus status = this.DecodeCore(encoded, data, out int consumed, out int written);

            if (status == OperationStatus.InvalidData)
                ThrowHelper.ThrowForOperationNotDone(status);

            Debug.Assert(status         == OperationStatus.Done);
            Debug.Assert(encoded.Length == consumed);
            Debug.Assert(data.Length    == written);

            return data;
        }
        //---------------------------------------------------------------------
        // For testing
        internal OperationStatus EncodeCore<T>(
            ReadOnlySpan<byte> data,
            Span<T> encoded,
            out int consumed,
            out int written,
            bool isFinalBlock = true)
            where T : struct
        {
            if (typeof(T) == typeof(byte))
            {
                return this.EncodeCore(data, MemoryMarshal.AsBytes(encoded), out consumed, out written, isFinalBlock);
            }
            else if (typeof(T) == typeof(char))
            {
                return this.EncodeCore(data, MemoryMarshal.Cast<T, char>(encoded), out consumed, out written, isFinalBlock);
            }
            else
            {
                throw new NotSupportedException(); // just in case new types are introduced in the future
            }
        }
        //---------------------------------------------------------------------
        // For testing
        internal OperationStatus DecodeCore<T>(
            ReadOnlySpan<T> encoded,
            Span<byte> data,
            out int consumed,
            out int written,
            bool isFinalBlock = true)
            where T : struct
        {
            if (typeof(T) == typeof(byte))
            {
                return this.DecodeCore(MemoryMarshal.AsBytes(encoded), data, out consumed, out written, isFinalBlock);
            }
            else if (typeof(T) == typeof(char))
            {
                return this.DecodeCore(MemoryMarshal.Cast<T, char>(encoded), data, out consumed, out written, isFinalBlock);
            }
            else
            {
                throw new NotSupportedException(); // just in case new types are introduced in the future
            }
        }
        //---------------------------------------------------------------------
        // PERF: can't be generic for inlining (generic virtual)
        protected abstract OperationStatus EncodeCore(
            ReadOnlySpan<byte> data,
            Span<byte> encoded,
            out int consumed,
            out int written,
            bool isFinalBlock = true);
        //---------------------------------------------------------------------
        // PERF: can't be generic for inlining (generic virtual)
        protected abstract OperationStatus EncodeCore(
            ReadOnlySpan<byte> data,
            Span<char> encoded,
            out int consumed,
            out int written,
            bool isFinalBlock = true);
        //---------------------------------------------------------------------
        // PERF: can't be generic for inlining (generic virtual)
        protected abstract OperationStatus DecodeCore(
            ReadOnlySpan<byte> encoded,
            Span<byte> data,
            out int consumed,
            out int written,
            bool isFinalBlock = true);
        //---------------------------------------------------------------------
        // PERF: can't be generic for inlining (generic virtual)
        protected abstract OperationStatus DecodeCore(
            ReadOnlySpan<char> encoded,
            Span<byte> data,
            out int consumed,
            out int written,
            bool isFinalBlock = true);
        //---------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static int GetBase64EncodedLength(int sourceLength)
        {
            if ((uint)sourceLength > MaximumEncodeLength)
                ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.length);

            int numWholeOrPartialInputBlocks = (sourceLength + 2) / 3;
            return numWholeOrPartialInputBlocks * 4;
        }
        //---------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static void EncodeThreeBytes<T>(ref byte threeBytes, ref T encoded, ref byte encodingMap)
        {
            uint i = (uint)threeBytes << 16
                | (uint)Unsafe.Add(ref threeBytes, 1) << 8
                | Unsafe.Add(ref threeBytes, 2);

            uint i0 = Unsafe.Add(ref encodingMap, (IntPtr)(i >> 18));
            uint i1 = Unsafe.Add(ref encodingMap, (IntPtr)((i >> 12) & 0x3F));
            uint i2 = Unsafe.Add(ref encodingMap, (IntPtr)((i >> 6) & 0x3F));
            uint i3 = Unsafe.Add(ref encodingMap, (IntPtr)(i & 0x3F));

            if (typeof(T) == typeof(byte))
            {
                i = i0 | (i1 << 8) | (i2 << 16) | (i3 << 24);
                Unsafe.WriteUnaligned(ref Unsafe.As<T, byte>(ref encoded), i);
            }
            else if (typeof(T) == typeof(char))
            {
                ref char enc = ref Unsafe.As<T, char>(ref encoded);
                Unsafe.Add(ref enc, 0) = (char)i0;
                Unsafe.Add(ref enc, 1) = (char)i1;
                Unsafe.Add(ref enc, 2) = (char)i2;
                Unsafe.Add(ref enc, 3) = (char)i3;
            }
            else
            {
                throw new NotSupportedException();  // just in case new types are introduced in the future
            }
        }
        //---------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static int DecodeFour<T>(ref T encoded, ref sbyte decodingMap)
        {
            uint t0, t1, t2, t3;

            if (typeof(T) == typeof(byte))
            {
                ref byte tmp = ref Unsafe.As<T, byte>(ref encoded);
                t0 = Unsafe.Add(ref tmp, 0);
                t1 = Unsafe.Add(ref tmp, 1);
                t2 = Unsafe.Add(ref tmp, 2);
                t3 = Unsafe.Add(ref tmp, 3);
            }
            else if (typeof(T) == typeof(char))
            {
                ref char tmp = ref Unsafe.As<T, char>(ref encoded);
                t0 = Unsafe.Add(ref tmp, 0);
                t1 = Unsafe.Add(ref tmp, 1);
                t2 = Unsafe.Add(ref tmp, 2);
                t3 = Unsafe.Add(ref tmp, 3);
            }
            else
            {
                throw new NotSupportedException();  // just in case new types are introduced in the future
            }

            int i0 = Unsafe.Add(ref decodingMap, (IntPtr)t0);
            int i1 = Unsafe.Add(ref decodingMap, (IntPtr)t1);
            int i2 = Unsafe.Add(ref decodingMap, (IntPtr)t2);
            int i3 = Unsafe.Add(ref decodingMap, (IntPtr)t3);

            i0 <<= 18;
            i1 <<= 12;
            i2 <<= 6;

            i0 |= i3;
            i1 |= i2;

            i0 |= i1;
            return i0;
        }
        //---------------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static void WriteThreeLowOrderBytes(ref byte destination, uint destIndex, int value)
        {
            Unsafe.Add(ref destination, (IntPtr)(destIndex + 0)) = (byte)(value >> 16);
            Unsafe.Add(ref destination, (IntPtr)(destIndex + 1)) = (byte)(value >> 8);
            Unsafe.Add(ref destination, (IntPtr)(destIndex + 2)) = (byte)value;
        }
    }
}
