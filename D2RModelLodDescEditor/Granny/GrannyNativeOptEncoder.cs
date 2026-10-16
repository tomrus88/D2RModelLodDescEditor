using System.Runtime.InteropServices;

namespace Granny.Format;

/// <summary>
/// Ground-truth BitKnit2 <b>encode_opt</b> oracle: drives the real granny
/// optimizer out of granny2_x64 2.11.8.0 - the only build that ships it - and
/// returns its exact output bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second harness?</b> <see cref="GrannyNativeEncoder"/> reaches the
/// encoder through the <c>GrannyBitKnitCompress</c> export, which hard-codes
/// <c>BitKnit_Level_High</c> (2) and therefore always takes the plain
/// <c>encode</c> path. The optimizer lives behind
/// <c>BitKnit_Compress(..., level, ...)</c>, which is not exported at all - the
/// export table still only carries the three <c>GrannyBitKnit*</c> names. So the
/// entry point is reached by RVA, and the level is passed explicitly.
/// </para>
/// <para>
/// <b>2.12 dropped it.</b> The 2.12.0.14 build that
/// <see cref="GrannyNativeEncoder"/> uses contains no <c>encode_opt</c> (scanned:
/// no 4097 sentinel, no <c>0xE0000000</c> overflow guard in <c>.text</c>), so this
/// harness loads a different DLL entirely - 2.11.8.0, whose <c>encode_opt</c> sits
/// at <c>0x180075d70</c> and is reached from <c>BitKnit_Compress</c> at
/// <c>0x180077a40</c>.
/// </para>
/// <para>
/// <b>Trustworthiness.</b> The same A/B guard as
/// <see cref="GrannyNativeEncoder.CompressVerified"/>: compress twice, the second
/// time with the byte immediately past <c>srcLength</c> changed, and require
/// byte-identical output. Anything that reads memory it was never given fails
/// here rather than poisoning a vector.
/// </para>
/// <para>
/// The RVA is not taken on faith. The first 16 bytes at
/// <c>moduleBase + 0x77A40</c> are checked against a fingerprint recorded from
/// the IDB, and the level's own <c>CompressParameters</c> are read back out of the
/// loaded image and required to carry <c>opt_parse</c> - which fails loudly if the
/// file under <paramref name="dllPath"/> is not the build this was written for.
/// </para>
/// </remarks>
public sealed unsafe class GrannyNativeOptEncoder : IDisposable
{
    /// <summary>The build IDA has open, and the only one with encode_opt.</summary>
    public const string DefaultDllPath = @"f:\bg3\gr2\granny2_x64_2.11.8.0.dll";

    /// <summary>RVA of <c>BitKnit_Compress</c> in 2.11.8.0 (VA 0x180077A40).</summary>
    private const int CompressRva = 0x77A40;

    /// <summary>RVA of the 48-entry <c>CompressParameters</c> table (VA 0x1800950C0).</summary>
    private const int LevelTableRva = 0x950C0;

    /// <summary>
    /// First 16 instructions of <c>BitKnit_Compress</c>:
    /// <c>sub rsp,48h / movsxd rax,[rsp+70h] / cmp eax,2Fh / ja ... / lea r10,[rax+rax*2]</c>.
    /// </summary>
    private static readonly byte[] CompressFingerprint =
    {
        0x48, 0x83, 0xEC, 0x48, 0x48, 0x63, 0x44, 0x24,
        0x70, 0x83, 0xF8, 0x2F, 0x77, 0x11, 0x4C, 0x8D,
    };

    // encode_opt's scratch requirement, worked out from its prologue
    // (f5fb18ae6d81, 0x180075D70):
    //
    //     es            = (work_mem + 63) & ~63
    //     match hash    = es + 421632, 4 * match_init's hash size
    //     DP table      = es + 421632 + 4*hash, 4097 * 12 dwords = 196,656
    //     match cache   = DP + 196656, 4096 * 80 = 327,680
    //
    // Level 3's max_hash_size is 0x1000000, and match_init stops doubling there,
    // so the hash alone is 67,108,864 bytes. 2.12's 34.5 MB buffer would under-run
    // it by half again - hence this class sizes its own.
    private const int StateBytes = 421632;
    private const int DpBytes = 4097 * 12 * 4;
    private const int CacheBytes = 4096 * 80;
    private const uint MaxHash = 0x001000000;
    private const int WorkMemBytes = StateBytes + 4 * (int)MaxHash + DpBytes + CacheBytes + 64;

    /// <summary>BitKnit_Level_VeryHigh - the only level whose opt_parse is set.</summary>
    private const int DefaultLevel = 3;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr h, string name);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr h);

    /// <summary>
    /// <c>BitKnit_Compress(dst, dst_nbytes, src, src_nbytes, level, work_mem)</c>
    /// - returns the number of bytes emitted, or 0 when the encoder refuses.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong CompressFn(IntPtr dst, ulong dstNbytes, IntPtr src, ulong srcNbytes, int level, IntPtr workMem);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetVersionFn();

    private IntPtr _module;
    private readonly CompressFn _compress;
    private readonly byte[] _workMem = new byte[WorkMemBytes];
    private GCHandle _workMemHandle;

    public string VersionString { get; }
    public int Level { get; }

    public GrannyNativeOptEncoder(string? dllPath = null, int level = DefaultLevel)
    {
        string path = dllPath
            ?? Environment.GetEnvironmentVariable("GRANNY_OPT_ORACLE_DLL")
            ?? DefaultDllPath;
        _module = LoadLibraryW(path);
        if (_module == IntPtr.Zero)
        {
            throw new DllNotFoundException(
                $"could not load {path} (win32 {Marshal.GetLastWin32Error()})");
        }

        try
        {
            // No export carries BitKnit_Compress, so the RVA is the only way in -
            // which makes checking the bytes at it non-optional.
            IntPtr pCompress = IntPtr.Add(_module, CompressRva);
            for (int i = 0; i < CompressFingerprint.Length; i++)
            {
                byte got = Marshal.ReadByte(pCompress, i);
                if (got != CompressFingerprint[i])
                {
                    throw new EntryPointNotFoundException(
                        $"{Path.GetFileName(path)} does not have BitKnit_Compress at RVA " +
                        $"0x{CompressRva:X} (byte {i} is 0x{got:X2}, expected 0x{CompressFingerprint[i]:X2}); " +
                        "this is not the 2.11.8.0 build the RVA was recorded from");
                }
            }
            _compress = Marshal.GetDelegateForFunctionPointer<CompressFn>(pCompress);

            if ((uint)level > 0x2F)
            {
                throw new ArgumentOutOfRangeException(nameof(level), level,
                    "BitKnit_Compress indexes a 48-entry table");
            }

            // Read the level's CompressParameters back out of the loaded image and
            // require opt_parse: that is the whole reason this class exists.
            IntPtr entry = IntPtr.Add(IntPtr.Add(_module, LevelTableRva), 12 * level);
            long maxHash = (uint)Marshal.ReadInt32(entry, 4);
            byte optParse = Marshal.ReadByte(entry, 10);
            if (optParse == 0)
            {
                throw new ArgumentException(
                    $"level {level} has opt_parse=0 in {Path.GetFileName(path)} - it would take " +
                    "the plain encode path, which GrannyNativeEncoder already covers",
                    nameof(level));
            }

            // match_init doubles 4096 up to max_hash_size, so 4 * nextPow2 is the
            // worst-case hash - and the buffer was sized for exactly that.
            long hashCap = 4096;
            while (hashCap < maxHash && hashCap < 0x04000000)
            {
                hashCap *= 2;
            }
            if (StateBytes + 4 * hashCap + DpBytes + CacheBytes + 64 > WorkMemBytes)
            {
                throw new ArgumentException(
                    $"level {level} asks for a {hashCap}-entry hash, which needs more " +
                    "work memory than this harness allocates", nameof(level));
            }

            Level = level;

            IntPtr pVersion = GetProcAddress(_module, "GrannyGetVersionString");
            VersionString = pVersion == IntPtr.Zero
                ? "unknown"
                : Marshal.PtrToStringAnsi(
                    Marshal.GetDelegateForFunctionPointer<GetVersionFn>(pVersion)()) ?? "unknown";

            _workMemHandle = GCHandle.Alloc(_workMem, GCHandleType.Pinned);
        }
        catch
        {
            FreeLibrary(_module);
            _module = IntPtr.Zero;
            throw;
        }
    }

    /// <summary>
    /// Compress the first <paramref name="srcLength"/> bytes of <paramref name="src"/>.
    /// Null when granny refuses.
    /// </summary>
    public byte[]? Compress(byte[] src, int srcLength, int? dstCapacity = null)
    {
        ArgumentNullException.ThrowIfNull(src);
        if ((uint)srcLength > (uint)src.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(srcLength));
        }

        // Worst case is the stored-raw path, plus per-block framing; the 0x10000
        // slack absorbs a partial final block so this needs no retry loop.
        int cap = dstCapacity ?? (srcLength + 0x10000);

        var dst = new byte[cap];
        GCHandle dstHandle = GCHandle.Alloc(dst, GCHandleType.Pinned);
        try
        {
            ulong written;
            fixed (byte* pSrc = src)
            {
                written = _compress(
                    dstHandle.AddrOfPinnedObject(), (ulong)cap,
                    (IntPtr)pSrc, (ulong)srcLength,
                    Level,
                    _workMemHandle.AddrOfPinnedObject());
            }

            if (written == 0 || written > (ulong)cap)
            {
                return null;
            }

            return dst.AsSpan(0, checked((int)written)).ToArray();
        }
        finally
        {
            dstHandle.Free();
        }
    }

    /// <summary>Compress <paramref name="src"/> with the real granny optimizer.</summary>
    public byte[]? Compress(ReadOnlySpan<byte> src, int? dstCapacity = null)
        => Compress(src.ToArray(), src.Length, dstCapacity);

    /// <summary>
    /// Compress twice, the second time with the byte immediately after the
    /// declared end changed, and require byte-identical output.
    /// </summary>
    /// <remarks>
    /// See <see cref="GrannyNativeEncoder.CompressVerified"/> for why this is the
    /// guard that earns trust. encode_opt's own match finder is bounded by
    /// <c>stop_len = (src_size - target) &amp; ~7</c>, so a correct run cannot
    /// observe the perturbed byte - and a run that does is discarded here instead
    /// of becoming a golden vector.
    /// </remarks>
    public byte[]? CompressVerified(ReadOnlySpan<byte> src)
    {
        var padded = new byte[src.Length + 64];
        src.CopyTo(padded);

        byte[]? first = Compress(padded, src.Length);
        if (first is null)
        {
            return null;
        }

        padded[src.Length] = 0xA5;
        byte[]? second = Compress(padded, src.Length);

        return second is not null && first.AsSpan().SequenceEqual(second) ? first : null;
    }

    public void Dispose()
    {
        if (_workMemHandle.IsAllocated)
        {
            _workMemHandle.Free();
        }
        if (_module != IntPtr.Zero)
        {
            FreeLibrary(_module);
            _module = IntPtr.Zero;
        }
    }
}
