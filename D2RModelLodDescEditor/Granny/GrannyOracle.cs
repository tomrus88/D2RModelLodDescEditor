using System.Runtime.InteropServices;

namespace Granny.Format;

/// <summary>
/// P/Invoke wrapper for the real granny2_x64.dll (2.11.8.0), used as a
/// ground-truth oracle for the decompressor.
/// </summary>
/// <remarks>
/// This is a low-level codec-only DLL: it has no file loaders, just the
/// decompressors. Note the signature is <b>size then pointer</b> - the
/// opposite of what the name suggests - which is why calling it
/// pointer-first crashes in .data.
/// <code>
/// bool GrannyBitKnitDecompress(uint64 compSize, const void* comp,
///                              uint64 decompSize, void* decomp);
/// </code>
/// Also note this DLL is <b>2.11</b> while D2R ships <b>2.12</b>. Where the
/// two differ in framing, the oracle itself is wrong for D2R data - so a
/// mismatch in a copy/state path is evidence of a version difference, not
/// necessarily a bug in our port.
/// </remarks>
public sealed unsafe class GrannyOracle : IDisposable
{
    /// <summary>The 2.11 codec-only build; D2R ships 2.12, so framing can differ.</summary>
    public const string DefaultDllPath = @"f:\bg3\gr2\granny2_x64_2.11.8.0.dll";

    private const string DefaultDll = DefaultDllPath;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr h);

    [DllImport("kernel32", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr h, string name);

    private readonly IntPtr _module;
    private readonly IntPtr _bitknit;
    private readonly IntPtr _versionString;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VersionStringFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BitKnitFn(ulong compSize, byte* comp, ulong decompSize, byte* decomp);

    public string VersionString { get; }

    public GrannyOracle(string? dllPath = null)
    {
        _module = LoadLibraryW(dllPath ?? DefaultDll);
        if (_module == IntPtr.Zero)
        {
            throw new DllNotFoundException($"could not load {dllPath ?? DefaultDll}");
        }

        _bitknit = GetProcAddress(_module, "GrannyBitKnitDecompress");
        _versionString = GetProcAddress(_module, "GrannyGetVersionString");
        if (_bitknit == IntPtr.Zero)
        {
            throw new EntryPointNotFoundException("GrannyBitKnitDecompress");
        }

        VersionString = _versionString == IntPtr.Zero
            ? "unknown"
            : Marshal.PtrToStringAnsi(
                Marshal.GetDelegateForFunctionPointer<VersionStringFn>(_versionString)()) ?? "unknown";
    }

    /// <summary>Decode one BitKnit2 buffer; null if the DLL refuses.</summary>
    public byte[]? Decompress(ReadOnlySpan<byte> src, int dstSize)
    {
        fixed (byte* pSrc = src)
        {
            var dst = new byte[dstSize];
            fixed (byte* pDst = dst)
            {
                var fn = Marshal.GetDelegateForFunctionPointer<BitKnitFn>(_bitknit);
                int ok = fn((ulong)src.Length, pSrc, (ulong)dstSize, pDst);
                return ok != 0 ? dst : null;
            }
        }
    }

    public void Dispose()
    {
        if (_module != IntPtr.Zero)
        {
            FreeLibrary(_module);
        }
    }
}