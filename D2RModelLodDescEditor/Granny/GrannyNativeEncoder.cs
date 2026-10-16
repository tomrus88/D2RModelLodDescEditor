using System.Runtime.InteropServices;

namespace Granny.Format;

/// <summary>
/// Ground-truth BitKnit2 <b>encoder</b> oracle: runs the real granny encoder
/// inside granny2_x64 2.12.0.14 and returns its exact output bytes.
/// </summary>
/// <remarks>
/// <para>
/// This closes the gap recorded in <c>docs/bitknit2-encoder-notes.md</c>. The
/// existing <see cref="GrannyOracle"/> only proves a stream is <i>valid</i>
/// (granny's own decompressor accepts it); nothing proved our encoder emits
/// the <i>same</i> stream granny does. Here the encoder finally has a
/// ground-truth reference to diff against.
/// </para>
/// <para>
/// <b>Why not <c>GrannyCompressContentsOfMemory</c>?</b> That export takes a
/// file-level descriptor whose type field selects BitKnit / Oodle / stored, and
/// standing one up needs the whole file-writer vtable.
/// <c>GrannyBitKnitCompress</c> is the codec entry point itself and needs only
/// the 44-byte state block reproduced in <see cref="NativeState"/>.
/// </para>
/// <para>
/// <b>Trustworthiness.</b> The captures rejected earlier passed a
/// length-from-return check yet described bytes other than their source. The
/// guard used here is the stronger A/B from
/// <c>BitKnit2EncoderTests.ContaminatedRansCapturesAreNotTrusted</c>: compress
/// once, then again with a different byte planted immediately past
/// <c>srcSize</c>, and require byte-identical output. A run that fails was
/// reading memory it was never given.
/// </para>
/// </remarks>
public sealed unsafe class GrannyNativeEncoder : IDisposable
{
    /// <summary>The exact build IDA has open; the encoder exports were added in 2.12.</summary>
    public const string DefaultDllPath = @"f:\bg3\gr2\granny2_x64_2.12.0.14.dll";

    // sub_180016770's scratch requirement, per the notes:
    //     (short_match ? 946031 : 421695) + 4 * max_hash
    // max_hash is not ours to choose - it comes from the DLL's own
    // CompressParameters at unk_1800915A8, whose first dword pair is
    // { lookahead = 4, max_hash = 0x00800000 }. The larger of the two
    // short_match variants is allocated so the choice cannot under-allocate.
    private const uint MaxHash = 0x00800000;
    private const int WorkMemBytes = 946031 + 4 * (int)MaxHash;

    // Compression type field the file layer would have written for BitKnit.
    // GrannyBitKnitCompress itself never reads it; set for fidelity only.
    private const uint TypeBitKnit = 3;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr h, string name);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr h);

    /// <param name="state">44-byte compression state (see <see cref="NativeState"/>).</param>
    /// <param name="srcSize">Number of valid bytes at <paramref name="src"/>.</param>
    /// <param name="src">Source bytes.</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool CompressFn(IntPtr state, uint srcSize, byte* src);

    /// <param name="state">The same state block passed to <see cref="CompressFn"/>.</param>
    /// <param name="outBase">Receives the base of the emitted stream.</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong CompressEndFn(IntPtr state, IntPtr* outBase);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetVersionFn();

    /// <summary>
    /// granny's file-compression state block, exactly as
    /// <c>GrannyBitKnitCompress</c> reads it (<c>0x180003560</c>).
    /// </summary>
    /// <remarks>
    /// Offsets come from the instruction stream, not from the pseudocode: the
    /// decompiler folds the base-pointer load as a 32-bit value where the code
    /// actually performs a full <c>add rcx, [rbx+24h]</c>.
    /// <code>
    /// +0x00 uint32 type        // file layer's selector; unused by the codec
    /// +0x04 uint32 blockCount  // incremented once per successful compress call
    /// +0x08 uint64 offset      // bytes emitted so far
    /// +0x10 uint64 totalSize   // capacity of the destination buffer
    /// +0x18 uint32 errorFlag   // must be non-zero on entry or the call no-ops
    /// +0x1C IntPtr workMem     // 34,500,463 bytes of scratch
    /// +0x24 IntPtr base        // destination buffer
    /// </code>
    /// <para>
    /// The layout is <b>Explicit</b>, not Sequential, and that is load-bearing.
    /// Sequential would insert padding to give each 8-byte field 8-byte
    /// alignment, landing workMem at +0x20 and base at +0x28. The native code
    /// reads them at +0x1C and +0x24, so it would receive garbage and fault
    /// immediately. Sequential packing cannot express a packed struct whose
    /// natural alignment differs from its field offsets, and this one is
    /// packed: +0x18 uint32 is followed by an unaligned qword.
    /// </para>
    /// </remarks>
    [StructLayout(LayoutKind.Explicit, Size = 44, Pack = 1)]
    private struct NativeState
    {
        [FieldOffset(0x00)] public uint Type;
        [FieldOffset(0x04)] public uint BlockCount;
        [FieldOffset(0x08)] public ulong Offset;
        [FieldOffset(0x10)] public ulong TotalSize;
        [FieldOffset(0x18)] public uint ErrorFlag;
        [FieldOffset(0x1C)] public IntPtr WorkMem;
        [FieldOffset(0x24)] public IntPtr Base;
    }

    private readonly IntPtr _module;
    private readonly CompressFn _compress;
    private readonly CompressEndFn _compressEnd;

    // Allocated once and reused: 32.9 MB per call would dominate the runtime.
    private readonly byte[] _workMem = new byte[WorkMemBytes];
    private GCHandle _workMemHandle;
    private IntPtr _state;

    public string VersionString { get; }

    public GrannyNativeEncoder(string? dllPath = null)
    {
        string path = dllPath
            ?? Environment.GetEnvironmentVariable("GRANNY_ORACLE_DLL")
            ?? DefaultDllPath;
        _module = LoadLibraryW(path);
        if (_module == IntPtr.Zero)
        {
            throw new DllNotFoundException(
                $"could not load {path} (win32 {Marshal.GetLastWin32Error()})");
        }

        IntPtr pCompress = GetProcAddress(_module, "GrannyBitKnitCompress");
        IntPtr pEnd = GetProcAddress(_module, "GrannyBitKnitCompressEnd");
        IntPtr pVersion = GetProcAddress(_module, "GrannyGetVersionString");
        if (pCompress == IntPtr.Zero || pEnd == IntPtr.Zero)
        {
            throw new EntryPointNotFoundException(
                $"{Path.GetFileName(path)} does not export the BitKnit encoder " +
                "(GrannyBitKnitCompress / GrannyBitKnitCompressEnd); 2.12 or newer is required");
        }

        _compress = Marshal.GetDelegateForFunctionPointer<CompressFn>(pCompress);
        _compressEnd = Marshal.GetDelegateForFunctionPointer<CompressEndFn>(pEnd);
        VersionString = pVersion == IntPtr.Zero
            ? "unknown"
            : Marshal.PtrToStringAnsi(
                Marshal.GetDelegateForFunctionPointer<GetVersionFn>(pVersion)()) ?? "unknown";

        _workMemHandle = GCHandle.Alloc(_workMem, GCHandleType.Pinned);
        _state = Marshal.AllocHGlobal(Marshal.SizeOf<NativeState>());
    }

    /// <summary>
    /// Compress the first <paramref name="srcLength"/> bytes of
    /// <paramref name="src"/>. Null when granny refuses.
    /// </summary>
    /// <remarks>
    /// The separate length exists because granny's match finder reads a
    /// lookahead window <i>past</i> the declared end of the stream. Passing a
    /// bare span would let the bytes after it - whatever the GC happened to
    /// leave there - decide the output. Supplying an over-long buffer with an
    /// explicit length makes those bytes ours to choose, which is what lets a
    /// capture be checked for reading memory it was never given.
    /// </remarks>
    public byte[]? Compress(byte[] src, int srcLength, int? dstCapacity = null)
    {
        ArgumentNullException.ThrowIfNull(src);
        if ((uint)srcLength > (uint)src.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(srcLength));
        }

        // Worst case is the store-raw path, i.e. slightly larger than the input,
        // plus per-block framing. The 0x10000 slack absorbs a partial final
        // block so this does not need a retry loop.
        int cap = dstCapacity ?? (srcLength + 0x10000);

        var dst = new byte[cap];
        GCHandle dstHandle = GCHandle.Alloc(dst, GCHandleType.Pinned);
        try
        {
            var state = new NativeState
            {
                Type = TypeBitKnit,
                BlockCount = 0,
                Offset = 0,
                TotalSize = (ulong)cap,
                // Non-zero, or GrannyBitKnitCompress returns false immediately.
                ErrorFlag = 1,
                WorkMem = _workMemHandle.AddrOfPinnedObject(),
                Base = dstHandle.AddrOfPinnedObject(),
            };
            Marshal.StructureToPtr(state, _state, fDeleteOld: false);

            bool ok;
            fixed (byte* pSrc = src)
            {
                ok = _compress(_state, (uint)srcLength, pSrc);
            }
            if (!ok)
            {
                return null;
            }

            // CompressEnd must hand back the same block we initialised. If it
            // does not, the state layout is wrong and nothing it returns can
            // be trusted - fail loudly rather than emit a bogus vector.
            IntPtr basePtr = IntPtr.Zero;
            ulong len = _compressEnd(_state, &basePtr);
            var after = Marshal.PtrToStructure<NativeState>(_state);
            if (basePtr != after.Base)
            {
                throw new InvalidOperationException(
                    "GrannyBitKnitCompressEnd returned a base pointer that does not " +
                    "match the state we supplied - the NativeState layout is wrong");
            }

            return dst.AsSpan(0, checked((int)len)).ToArray();
        }
        finally
        {
            dstHandle.Free();
        }
    }

    /// <summary>
    /// Compress <paramref name="src"/> with the real granny encoder.
    /// Null when granny refuses - notably when the destination is too small.
    /// </summary>
    public byte[]? Compress(ReadOnlySpan<byte> src, int? dstCapacity = null)
        => Compress(src.ToArray(), src.Length, dstCapacity);

    /// <summary>
    /// Compress twice, the second time with the byte immediately after the
    /// declared end changed, and require byte-identical output.
    /// </summary>
    /// <remarks>
    /// This is the guard that earns trust in a capture. A capture that read
    /// memory it was never given - the failure mode behind both previously
    /// rejected vectors - produces <i>different</i> output when the bytes just
    /// past <c>srcLength</c> change. A run that passes reads only the declared
    /// input.
    /// <para>
    /// The tail is held at a constant 0x00 rather than varied: granny's own
    /// lookahead makes the tail legitimately influence the output, so the tail
    /// must be identical across runs for this to be a determinism check at all.
    /// What varies is one byte <i>within</i> that tail, which a reader bounded
    /// by <c>srcLength</c> cannot observe.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The agreed output, or null if granny refused or the two runs disagree.
    /// </returns>
    public byte[]? CompressVerified(ReadOnlySpan<byte> src)
    {
        // 64 bytes covers the level-2 lookahead window with room to spare.
        var padded = new byte[src.Length + 64];
        src.CopyTo(padded);

        byte[]? first = Compress(padded, src.Length);
        if (first is null)
        {
            return null;
        }

        // Perturb a byte the encoder is entitled to read (the lookahead) but
        // that any correct reader bounds out.
        padded[src.Length] = 0xA5;
        byte[]? second = Compress(padded, src.Length);

        return second is not null && first.AsSpan().SequenceEqual(second) ? first : null;
    }

    public void Dispose()
    {
        if (_state != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_state);
            _state = IntPtr.Zero;
        }
        if (_workMemHandle.IsAllocated)
        {
            _workMemHandle.Free();
        }
        if (_module != IntPtr.Zero)
        {
            FreeLibrary(_module);
        }
    }
}
