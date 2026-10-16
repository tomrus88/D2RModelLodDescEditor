namespace Granny.Format;

/// <summary>
/// One record in the reverse rANS symbol buffer (RansEncSym in the granny PDB):
/// <c>{ ushort start; ushort freq; }</c>, four bytes.
/// </summary>
/// <remarks>
/// <para>
/// <c>freq</c> carries a flag in its top bits. A plain model symbol has
/// <c>freq &amp; 0x8000 == 0</c> and stores the cumulative range. Raw-bit
/// records set bit 15; <c>freq == 0xC000</c> is the continuation word of a
/// bit-run longer than 16 bits, while any other <c>0x8000 | n</c> value means
/// "n raw bits, value in <c>start</c>".
/// </para>
/// <para>
/// The encoder appends records in FORWARD order; the flush walks them in
/// REVERSE, because the decoder consumes the stream backwards. This is why
/// every arithmetic step below runs from the last record to the first.
/// </para>
/// </remarks>
internal readonly struct RansEncSym
{
    public readonly ushort Start;
    public readonly ushort Freq;

    public RansEncSym(ushort start, ushort freq)
    {
        Start = start;
        Freq = freq;
    }
}

/// <summary>Exposes the built model tables for tests and diagnostics.</summary>
public static class DefSumProbe
{
    /// <summary>The four offs_top cumulative tables plus the bot table.</summary>
    public static (ushort[][] Top, ushort[] Bot) Offs()
    {
        var (top, bot) = DefSumModel.BuildOffs();
        return (top.Select(t => t.CumFreq).ToArray(), bot.CumFreq);
    }


    /// <summary>The litlen cumulative table.</summary>
    public static ushort[] Litlen() => DefSumModel.LitlenCumFreq();

    /// <summary>
    /// The cost of coding a symbol with frequency <paramref name="freq"/>, exactly
    /// as <c>defsum_update_caches</c> computes it.
    /// </summary>
    public static int Cost(int freq) => DefSumModel.CostOfFreq(freq);
}

/// <summary>DefSum model: cumulative frequencies, symbol frequencies and a
/// direct-mapped jump table for the rANS decoder.
/// </summary>
/// <remarks>
/// Layout confirmed against D2R.exe (Granny 2.12, sub_14428A2A0), where the
/// symbol decode reads jump at u16 index 516, cumfreq at 1032, symfreq at
/// 1634 and the renorm counter at 2236.
/// <para>
/// The jump table is built with a <em>cursor</em> over the array rather
/// than per-slot search: for i = 1..n, every still-unwritten slot up to
/// <c>(cum[i]-1) &gt;&gt; Shift</c> gets the value i-1. Slots past the final
/// boundary are never written and stay 0 - with the top boundary at
/// (32768-1)&gt;&gt;6 == 511 that leaves slots 512..515 zero. Getting this
/// wrong pins high slots to the last symbol, which makes the decoder start
/// a stream on a bogus match.
/// </para>
/// </remarks>
internal sealed class DefSumModel
{
    public const int JumpSlots = 516;
    public const int RenormLimit = 1024;

    private readonly ushort[] _cumfreq;
    private readonly ushort[] _symfreq;
    private readonly int[] _jump = new int[JumpSlots];
    private int _renormCounter;

    /// <summary>Shift used when BUILDING a jump table: (cum[i]-1) &gt;&gt; shift.</summary>
    public int Shift { get; }

    /// <summary>The cumulative frequency table.</summary>
    public ushort[] CumFreq => _cumfreq;

    public int SymbolCount => _symfreq.Length;

    public DefSumModel(ushort[] cumfreq, int shift)
    {
        _cumfreq = cumfreq;
        _symfreq = new ushort[cumfreq.Length - 1];
        Array.Fill(_symfreq, (ushort)1);
        _renormCounter = RenormLimit;
        Shift = shift;
        RebuildJump();
    }

    /// <summary>Encoder-side symbol emission: record (start, freq), then adapt.</summary>
    /// <remarks>
    /// Mirrors litlen_encode / offset_encode in bg3_Release.exe: the encoder writes
    /// <c>start = cumfreq[sym]</c> and <c>freq = cumfreq[sym+1] - cumfreq[sym]</c>,
    /// then applies the SAME adaptation the decoder will later apply. The two sides
    /// must stay bit-identical or the streams desynchronise, which is why this
    /// shares the model's own adaptation path rather than duplicating it.
    /// <para>
    /// Call this in FORWARD symbol order - that is, during the parse, not from the
    /// backward rANS flush. The decoder decodes symbols front to back, so it
    /// adapts the models front to back; the encoder reverses only the arithmetic
    /// that produces the bitstream, not the adaptation. Resolving the cumulative
    /// range later, while walking the record buffer backwards, is self-consistent
    /// rANS but renorms a different symbol than the decoder and desynchronises on
    /// the first block long enough to reach a renorm countdown.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Fixed-point cost of encoding <paramref name="sym"/> next, in granny's units:
    /// <c>log2(2^15 / freq)</c> scaled by 2^14, which is why the real encoder adds
    /// whole exponents with <c>&lt;&lt; 14</c> for the escape symbols above 287.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read-only: querying the cost of a candidate must not adapt the model, because
    /// granny evaluates every candidate's cost against the same pre-symbol state and
    /// only encodes the one it accepts.
    /// </para>
    /// <para>
    /// <b>The base is 2^15, not 2^16.</b> It was 2^16 for a long time and that
    /// error is worth recording because it is invisible to round-tripping - the
    /// cost only steers candidate selection, so every stream still decoded. It
    /// showed up solely as a narrow miss: on <c>lowent/43</c> the encoder priced a
    /// 4-byte match 1174 units above the threshold that would have accepted it
    /// (0.15%), picking a match granny does not pick. A uniform 1-bit overshoot on
    /// every model term is exactly that size, because the run cost on the left of
    /// the acceptance test carries more terms than <c>bestCost</c> on the right.
    /// Two independent facts pin the base at 32768: <see cref="LitlenCumFreq"/>
    /// totals exactly 32768, and <c>RansEncSym.freq</c> reserves bit 15 as the
    /// raw-bits escape - a total of 65536 would let a hot symbol's frequency set
    /// that bit and be misread as an escape. Correcting it moved the byte-exact
    /// sweep from 664/712 to 701/712.
    /// </para>
    /// <para>
    /// <b>It is a fixed-point table, not a logarithm.</b> <c>costs[]</c> is built by
    /// <c>defsum_update_caches</c> (bg3 <c>0x148541020</c>) with no floating point
    /// at all: the frequency is normalised into <c>[2^15, 2^16)</c>, the top 6 bits
    /// select an entry of a 33-entry log table, and the low 10 bits linearly
    /// interpolate between it and the next.
    /// <code>
    /// v11 = cumfreq[1] - *cumfreq;
    /// _BitScanReverse(&amp;v12, v11);                    // v12 = floor(log2(freq))
    /// v13 = (v11 &lt;&lt; 15 &gt;&gt; v12) &gt;&gt; 10;           // 32..63
    /// cost = ((15 - v12) &lt;&lt; 14)
    ///      - (table[v13-32] + (((norm &amp; 0x3FF) * (table[v13-31] - table[v13-32]) + 512) &gt;&gt; 10));
    /// </code>
    /// Algebraically that is <c>16384 * log2(32768 / freq)</c>, which is why the
    /// base is 2^15 - but substituting <c>Math.Log2</c> is only an approximation of
    /// it. Measured against the table over all 65,535 possible frequencies, plain
    /// truncation is low by 1..4 units (mean +1) on 73% of them, because linear
    /// interpolation inside a 1/32-wide log step is not <c>log2</c>. The sweep
    /// happened to pass at 712/712 either way; reproducing the table removes the
    /// discrepancy entirely rather than relying on it not to flip a decision.
    /// </para>
    /// </remarks>
    internal int CostOf(int sym) => CostOfFreq(_cumfreq[sym + 1] - _cumfreq[sym]);

    /// <summary>
    /// The cost of a symbol whose frequency is <paramref name="freq"/>, in granny's
    /// 2^-14-bit units. Ported verbatim from <c>defsum_update_caches</c>; see the
    /// remarks on <see cref="CostOf"/>.
    /// </summary>
    internal static int CostOfFreq(int freq)
    {
        if (freq <= 0)
        {
            // A symbol whose frequency collapsed to zero cannot be priced; charge it
            // the same as a 1-in-2^15 symbol rather than dividing by zero.
            return 15 << 14;
        }

        int bsr = System.Numerics.BitOperations.Log2((uint)freq);

        // Normalise freq into [2^15, 2^16): top 6 bits pick the table entry, the
        // low 10 the fraction to interpolate across it.
        int norm = (freq << 15) >> bsr;
        int idx = norm >> 10;
        int frac = norm & 0x3FF;
        int t0 = Log2Table[idx - 32];
        int t1 = Log2Table[idx - 31];
        int interp = t0 + ((frac * (t1 - t0) + 512) >> 10);

        return ((15 - bsr) << 14) - interp;
    }

    /// <summary>
    /// <c>word_14A3D4E40</c>: <c>round(log2(1 + k/32) * 16384)</c> for k = 0..32,
    /// read straight out of bg3_Release.exe. Index 0 is 0 and index 32 is 0x4000,
    /// so the table spans exactly one octave and the interpolation needs
    /// <c>[idx-32]</c> and <c>[idx-31]</c> - 33 entries, not 32.
    /// </summary>
    private static readonly int[] Log2Table =
    {
        0, 727, 1433, 2118, 2784, 3432, 4062, 4676, 5274, 5858, 6428, 6984,
        7527, 8059, 8578, 9086, 9584, 10071, 10549, 11017, 11476, 11926, 12368,
        12802, 13228, 13646, 14057, 14461, 14858, 15249, 15634, 16012, 16384,
    };

    internal RansEncSym EncodeSymbol(int sym)
    {
        ushort lo = _cumfreq[sym];
        ushort hi = _cumfreq[sym + 1];

        _symfreq[sym] = (ushort)(_symfreq[sym] + 31);
        if (--_renormCounter == 0)
        {
            Renorm(sym);
        }

        return new RansEncSym(lo, (ushort)(hi - lo));
    }

    /// <summary>Snapshot of the adaptive state, for rolling back a discarded block.</summary>
    /// <remarks>
    /// When a block loses encoder_flush_block's size test and falls back to a raw
    /// store, the decoder never sees those symbols and so never applies their
    /// adaptation. The encoder must therefore undo it exactly - resetting the
    /// models to their initial tables is wrong, because a raw block in the middle
    /// of a stream leaves the models exactly where the previous block left them.
    /// The jump table is derived from <see cref="_cumfreq"/> and is rebuilt on restore.
    /// </remarks>
    internal ModelState Save() => new((ushort[])_symfreq.Clone(),
                                      (ushort[])_cumfreq.Clone(),
                                      _renormCounter);

    /// <summary>Restore a state captured by <see cref="Save"/>.</summary>
    internal void Restore(ModelState s)
    {
        Array.Copy(s.SymFreq, _symfreq, _symfreq.Length);
        Array.Copy(s.CumFreq, _cumfreq, _cumfreq.Length);
        _renormCounter = s.RenormCounter;
        RebuildJump();
    }

    internal readonly record struct ModelState(ushort[] SymFreq, ushort[] CumFreq, int RenormCounter);

    /// <summary>Initial cumulative table for the 300-symbol litlen model.</summary>
    /// <remarks>
    /// The first 264 entries step by 32732/264; the tail 264..300 is linear
    /// from 32468. The model totals exactly 32768 (16-bit range).
    /// </remarks>
    public static ushort[] LitlenCumFreq()
    {
        var cum = new List<ushort>();
        long v = 0;
        while (true)
        {
            cum.Add((ushort)(v / 0x108));
            v += 32732;
            if (v >= 0x83DAE0)
            {
                break;
            }
        }
        int i = cum.Count;
        while (i <= 300)
        {
            cum.Add((ushort)(i + 32468));
            i++;
        }
        return cum.ToArray();
    }

    /// <summary>
    /// The four litlen tables (one per context). Verified identical after
    /// init; they diverge as symbols are decoded.
    /// </summary>
    public static DefSumModel[] BuildLitlen()
    {
        var r = new DefSumModel[4];
        for (int i = 0; i < 4; i++)
        {
            r[i] = new DefSumModel(LitlenCumFreq(), 6);
        }
        return r;
    }

    /// <summary>OffsModel: four 40-symbol top tables plus one 21-symbol bot table.</summary>
    public static (DefSumModel[] Top, DefSumModel Bot) BuildOffs()
    {
        var top = new DefSumModel[4];
        for (int i = 0; i < 4; i++)
        {
            top[i] = new DefSumModel(BuildOffsTop(), 9);
        }
        var bot = new DefSumModel(BuildOffsBot(), 9);
        return (top, bot);
    }

    // 128-bit magic-number division, exactly as the original emits it.
    //
    // These NEED true 128-bit arithmetic: the constants are 64-bit and the
    // product is taken modulo 2^64 and then shifted right by 64 to extract
    // the high word. Doing it in plain `ulong` wraps and silently yields an
    // all-zero table, which makes the offset models decode garbage.
    private static ushort[] BuildOffsTop()
    {
        var cum = new List<ushort>();
        long v = 0;
        while (true)
        {
            var prod = (UInt128)(ulong)v * 0xCCCCCCCCCCCCCCCDUL;
            cum.Add((ushort)((ulong)(prod >> 64) >> 5));
            v += 0x8000;
            if (v > 0x140000)
            {
                break;
            }
        }
        return cum.ToArray();
    }

    private static ushort[] BuildOffsBot()
    {
        var cum = new List<ushort>();
        long v = 0;
        while (true)
        {
            var prod = (UInt128)(ulong)v * 0x8618618618618619UL;
            long q = (long)(ulong)(prod >> 64);
            cum.Add((ushort)((q + ((v - q) >> 1)) >> 4));
            v += 0x8000;
            if (v > 0xA8000)
            {
                break;
            }
        }
        return cum.ToArray();
    }

    /// <summary>
    /// Rebuild the jump table with the cursor walk from
    /// defsum_init_finalize / defsum_adapt_renorm.
    /// </summary>
    /// <remarks>
    /// Slots beyond the last written boundary are left at 0 - see the type
    /// remarks. This is a LOWER bound: the stored value is the first symbol
    /// that could match the slot, and the decoder's linear scan refines it.
    /// </remarks>
    private void RebuildJump()
    {
        Array.Clear(_jump);
        int pos = 0;
        int val = 0;
        for (int i = 1; i < _cumfreq.Length; i++)
        {
            int limit = (_cumfreq[i] - 1) >> Shift;
            while (pos <= limit && pos < JumpSlots)
            {
                _jump[pos++] = val;
            }
            val++;
        }
    }

    /// <summary>Symbol decode + adapt + renorm, ported from the originals.</summary>
    public int Decode(ref uint x, ref uint xalt, ref int read, ReadOnlySpan<byte> data)
    {
        int scaled = (int)(x & 0x7FFF);
        // cum has nsyms+1 entries, so the highest usable symbol is nsyms-1: the
        // decoder needs cum[sym+1]. The original walks unbounded because it
        // __debugbreak()s on a bad state upstream; reaching either end means
        // the coder has already desynchronised, so clamp rather than index
        // past the table (which would corrupt the adaptive model).
        int n = _cumfreq.Length - 2;
        int sym = _jump[scaled >> Shift];
        if (sym > n)
        {
            sym = n;
        }
        if (sym < 0)
        {
            sym = 0;
        }
        if (scaled >= _cumfreq[sym + 1])
        {
            sym++;
        }
        while (sym < n && scaled >= _cumfreq[sym + 1])
        {
            sym++;
        }
        if (sym > n)
        {
            sym = n;
        }

        uint lo = _cumfreq[sym];
        uint width = (uint)(_cumfreq[sym + 1] - lo);
        uint next = (uint)(scaled + ((x >> 15) * width) - lo);

        if (next < 0x10000 && read + 2 <= data.Length)
        {
            // `movzx eax, word [r14] ; lea rcx, [r14+2] ; ... cmovb r14, rcx`
            // - the word is read at the CURRENT cursor and the cursor only
            // advances afterwards. Advancing first silently reads the
            // previous word.
            // `shl edx, 10h ; or edx, eax` - value high, word low.
            next = (next << 16) | (uint)(data[read] | (data[read + 1] << 8));
            read += 2;
        }

        // `add word ptr [...], 0x1f` is a 16-bit add.
        _symfreq[sym] = (ushort)(_symfreq[sym] + 31);
        if (--_renormCounter == 0)
        {
            Renorm(sym);
        }

        // The original swaps the two coder registers after every symbol.
        x = xalt;
        xalt = next;
        return sym;
    }

    /// <summary>
    /// defsum_adapt_renorm: blend the adaptive frequencies back toward the
    /// uniform table and rebuild the jump cache.
    /// </summary>

    private void Renorm(int val)
    {
        _renormCounter = RenormLimit;
        // symfreq is u16[] in the original, so this add WRAPS at 0xFFFF.
        //
        // The bump is PER-MODEL, not a single constant. Measured from d2r.exe
        // (granny 2.12) at the three real adaptation routines:
        //     defsum_adapt_renorm_DefSumModel_300_9_1_1024___  (litlen, 300)  += 725
        //     defsum_adapt_renorm_DefSumModel_40_6_1_1024___   (offs_top, 40) += 985
        //     defsum_adapt_renorm_DefSumModel_21_6_1_1024___   (offs_bot, 21) += 1004
        // and the differences track the symbol-count differences exactly
        // (40-21 = 19 = 1004-985; 300-40 = 260 = 985-725), i.e. the bump is
        // 1025 - nsyms. Using 725 everywhere is exactly 260 short on the
        // 40-symbol models, which desynchronises their cumfreq and was the
        // cause of the decoder dying partway through large assets.
        _symfreq[val] = (ushort)(_symfreq[val] + (1025 - _symfreq.Length));

        uint acc = 0;
        for (int i = 0; i < _symfreq.Length; i++)
        {
            ushort f = _symfreq[i];
            _symfreq[i] = 1;
            acc += f;
            ushort v9 = _cumfreq[i + 1];
            // Arithmetic shift: matches `sar` in the original.
            _cumfreq[i + 1] = (ushort)(v9 + (int)((acc - v9) >> 1));
        }

        RebuildJump();
    }

    /// <summary>Diagnostics: total frequency mass and monotonicity.</summary>
    public (bool Monotonic, int Total) Check()
    {
        bool mono = true;
        for (int i = 0; i + 1 < _cumfreq.Length; i++)
        {
            if (_cumfreq[i] > _cumfreq[i + 1])
            {
                mono = false;
                break;
            }
        }
        return (mono, _cumfreq[^1]);
    }
}