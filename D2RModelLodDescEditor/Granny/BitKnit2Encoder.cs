namespace Granny.Format;

/// <summary>
/// BitKnit2 compressor - a port of granny's <c>BitKnit_Compress</c> path.
/// </summary>
/// <remarks>
/// <para>
/// Verified against the <c>bg3_Release.exe</c> granny PDB (full symbols):
/// <c>BitKnit_Compress</c> selects <c>encode</c> or <c>encode_opt</c> from the
/// level's <c>CompressParameters.opt_parse</c> flag. Both drive the same symbol
/// layer, implemented here directly: <c>litlen_encode</c> / <c>offset_encode</c>
/// append records, and <c>ransenc_attempt_flush</c> folds them into bytes
/// <em>backwards</em>.
/// </para>
/// <para>
/// The symbol layer, the candidate cost model, block framing and the fallback
/// rules below are exact, and this reproduces granny's own bytes byte-for-byte
/// on the validated vectors in the test suite - including a compressed rANS
/// vector, not just the raw-block path. See
/// <c>docs/bitknit2-encoder-notes.md</c> for the recovered cost model.
/// <para>
/// The candidate SET now matches granny's too: all eight <c>rep.offs[]</c>
/// entries are priced as single move-to-front codes (by MTF rank, not raw
/// distance), the rep cache keeps granny's two-deep <c>offs[6]/[7]</c> tail, and
/// the match finder drives the packed 8-slot rows, the second-hash probe and the
/// 1024-entry short-match chain - all live at level 2.
/// </para>
/// <para>
/// Two entry points, because <c>BitKnit_Compress</c> has two parsers:
/// <see cref="Compress"/> is <c>encode</c> (levels 0..2, greedy runs under an
/// adaptive distance penalty) and <see cref="CompressOpt"/> is
/// <c>encode_opt</c> (level 3, a Viterbi DP with no penalty). They share the
/// symbol layer, the block framing and the match finder, and disagree on
/// everything above that. Both are byte-identical to granny: 712/712 synthetic
/// cases and 19,843/19,843 real corpus sections for <c>encode</c>, 712/712 for
/// <c>encode_opt</c> against the 2.11.8.0 build, which is the only one that
/// still ships it.
/// </para>
/// </remarks>
public sealed class BitKnit2Encoder
{
    private const ushort Tag = 0x75B1;
    private const int BlockSize = 0x10000;
    private const int LongMatchThreshold = 0x120;

    // granny's per-position match ceiling: `encode` clamps its stop_len to
    // 0x2018 (`if (v32 > 0x2018) v32 = 8216`), so no candidate it considers can
    // ever exceed this. It coincides with the litlen escape ceiling.
    private const int MaxStopLength = 0x2018;

    // The rep-offset cache depth (granny: `RepOffsSet.offs[8]`).
    private const int RepSlots = 8;

    /// <summary>
    /// granny's distance penalty, in 2^-14-bit units per byte of extra advance:
    /// 0x20000 = 131072 = 8 whole bits per byte.
    /// </summary>
    /// <remarks>
    /// This is only the SEED. In the binary the penalty is recomputed after every
    /// parse decision from a smoothed bits-per-byte estimate; see
    /// <c>AdvancePenalty</c> and the <c>estBits</c>/<c>penalty</c> locals in
    /// <see cref="EncodeBlock"/>, which transcribe encode_dec.txt's
    /// <c>v118 += ((v102 &lt;&lt; 8) / v91 + 1 - v118 + 4) &gt;&gt; 3</c>.
    /// </remarks>
    private const long DistancePenalty = 0x20000;

    /// <summary>
    /// granny's smoothed cost-per-byte estimate (`v118` / `var_4040`), seeded to
    /// 0x2000000 at the top of `encode` - which is exactly the value that makes
    /// <c>(seed + 128) >> 8</c> land on <see cref="DistancePenalty"/>.
    /// </summary>
    private const long InitialEstimate = 0x2000000;

    // The live penalty (v17 / v109) and its estimate (v118). Seeded once per
    // stream and advanced after EVERY parse decision.
    //
    // Neither is part of the model/rep snapshot: a stored block does not roll
    // them back, it SKIPS the update (LABEL_126 does `v17 = v109; goto
    // $parse_revert;`). Rolling them back instead would diverge as soon as a
    // stream mixed stored and compressed blocks.
    private long _penalty = DistancePenalty;
    private long _estimate = InitialEstimate;

    // D2R hardcodes compression level 2, whose CompressParameters are
    // lookahead=4, max_hash_size=8388608, second_hash=1, short_match=1.
    //
    // `lookahead` is not only a match ceiling in this codec: encode uses it as
    // the per-position literal RUN budget
    // (`v20 = lookahead; if (v8 - v18 <= lookahead) v20 = v8 - v18;`).
    private const int Lookahead = 4;

    private DefSumModel[] _litlen = System.Array.Empty<DefSumModel>();
    private DefSumModel[] _offsTop = System.Array.Empty<DefSumModel>();
    private DefSumModel _offsBot = null!;

    // granny's RepOffsSet: an 8-entry recent-distance cache plus offs0. Every
    // match emitted MUST rotate it exactly as encode does, because the decoder
    // runs the same rotation and a literal is coded as a delta against offs0 -
    // any divergence desynchronises the stream immediately.
    private readonly uint[] _rep = new uint[RepSlots];
    private uint _rep0;

    // granny's packed hash table (match_init / match_insert_until): `rows` slots of
// 8 uint entries each. Entry layout is (tag << 26) | position, so the low 26 bits
// address a source position and the upper bits are the hash tag.
private uint[] _mfHash = System.Array.Empty<uint>();
    private uint _rowMask;

    // Lowest position not yet indexed into the table.
    private int _fillPos;

    private bool _secondHash;

    // match_find's short-match chain, live at level 2 (short_match == 1). A 2-byte
    // hash indexes a 2048-entry table of recent positions chained through
    // short_prev, so it finds length-2/3 matches the packed table's 26-bit tag
    // test rejects.
    //
    // The 2048/1024 asymmetry is not a typo: the bucket index is masked with
    // 0x7FF (11 bits -> 2048 buckets) while the chain LINK is masked with 0x3FF
    // (10 bits -> 1024 slots). Sizing short_hash at 1024 makes the bucket index
    // run off the end of the array.
    private readonly uint[] _shortHash = new uint[2048];
    private readonly uint[] _shortPrev = new uint[1024];
    private bool _shortMatch;

    /// <summary>Candidates produced by the last FindMatch call (granny: up to 8).</summary>
    private int MatchCount;

    // match_find's per-context result cache - encode's `v127`/`v128`: four slots
    // keyed by `pos & 3`, each holding the last position searched under that
    // context, its match count, and its 8 candidates.
    //
    // This is load-bearing, not a speed optimisation. A position is re-searched
    // only when its context next sees a DIFFERENT position, so a position that
    // shows up again in a later run reuses the candidates found the first time,
    // against the hash table as it stood then. Recomputing instead is observably
    // wrong: indexing position P evicts the row that P's own match was found in,
    // so the second search comes up empty and a candidate granny picks vanishes.
    // `lowent/48` diverges exactly there - position 34 is searched in the run at
    // 32 and again in the run at 34, and only the first search sees (3, 4).
    private readonly int[] _mfCachePos = new[] { -1, -1, -1, -1 };
    private readonly int[] _mfCacheCount = new int[4];
    private readonly Match[] _mfCacheMatches = new Match[4 * 8];

    /// <summary>A match candidate, mirroring granny's <c>Match</c>.</summary>
    private struct Match
    {
        public int Length;
        public int Dist;
    }

    // The encoder never emits a move-to-front distance code (every distance
    // goes out as an explicit two-level value), so it carries no MTF state.
    // The decoder's MTF_INIT is recorded in BitKnit2's constructor.

    /// <summary>
    /// One rANS record: the resolved symbol's start state and frequency, ready
    /// for <see cref="Flush"/> to walk backwards over.
    /// </summary>
    internal readonly struct EncRec
    {
        public readonly ushort Start;
        public readonly ushort Flag;

        public EncRec(ushort start, ushort flag)
        {
            Start = start;
            Flag = flag;
        }

        public static EncRec Resolved(RansEncSym sym) => new(sym.Start, sym.Freq);
    }

    private readonly List<EncRec> _recs = new();
    private readonly List<byte> _out = new();

    // granny hands match_find a fixed 8-entry candidate array per position.
    private readonly Match[] _matches = new Match[8];

    public BitKnit2Encoder()
    {
        Reset();
    }

    private void Reset()
    {
        ResetModels();
        _recs.Clear();
        _out.Clear();

        // The match cache is per-stream (encode initialises v127 to -1 on entry).
        Array.Fill(_mfCachePos, -1);
        Array.Clear(_mfCacheCount);

        // encode seeds the penalty and its estimate once per stream, alongside
        // the models (`v118 = 0x2000000; v17 = 0x20000; v109 = 0x20000`).
        _penalty = DistancePenalty;
        _estimate = InitialEstimate;
    }

    private void ResetModels()
    {
        _litlen = DefSumModel.BuildLitlen();
        (_offsTop, _offsBot) = DefSumModel.BuildOffs();

        // decoder_init writes the 64-bit constant 0x100000001 across two
        // adjacent u32[8] slots, so every rep offset starts at 1.
        Array.Fill(_rep, 1u);
        _rep0 = 1;
    }

    /// <summary>
    /// Capture the model state at a block boundary so a discarded parse can be
    /// rolled back. See <see cref="Compress"/> for why the rep cache must be
    /// rolled back together with the models.
    /// </summary>
    private (DefSumModel.ModelState[] Litlen, DefSumModel.ModelState[] OffsTop, DefSumModel.ModelState OffsBot) SaveModels()
    {
        var lit = new DefSumModel.ModelState[_litlen.Length];
        for (int i = 0; i < lit.Length; i++)
        {
            lit[i] = _litlen[i].Save();
        }

        var top = new DefSumModel.ModelState[_offsTop.Length];
        for (int i = 0; i < top.Length; i++)
        {
            top[i] = _offsTop[i].Save();
        }

        return (lit, top, _offsBot.Save());
    }

    private void RestoreModels(
        (DefSumModel.ModelState[] Litlen, DefSumModel.ModelState[] OffsTop, DefSumModel.ModelState OffsBot) snap)
    {
        for (int i = 0; i < snap.Litlen.Length; i++)
        {
            _litlen[i].Restore(snap.Litlen[i]);
        }

        for (int i = 0; i < snap.OffsTop.Length; i++)
        {
            _offsTop[i].Restore(snap.OffsTop[i]);
        }

        _offsBot.Restore(snap.OffsBot);
    }
    // ===== encode_opt (BitKnit_Level_VeryHigh) ==================================

    /// <summary>encode_opt's DP table: 4097 entries of 12 dwords.</summary>
    private const int OptEntries = 4097;
    private const int OptStride = 12;
    private readonly uint[] _dp = new uint[OptEntries * OptStride];

    /// <summary>
    /// encode_opt's match cache: 4096 slots keyed <c>pos &amp; 0xFFF</c>, each
    /// holding the eight candidates found there, the count, and the position the
    /// slot belongs to.
    /// </summary>
    private readonly Match[] _optMatches = new Match[4096 * 8];
    private readonly int[] _optCount = new int[4096];
    private readonly int[] _optCacheTarget = new int[4096];

    /// <summary>
    /// encode_opt's window is a fixed 4096 bytes - it never reads
    /// <c>CompressParameters.lookahead</c>, which is 0 at level 3 anyway.
    /// </summary>
    private const int OptWindow = 0x1000;

    /// <summary>Level 3 is the only entry whose <c>bubble_back</c> flag is set.</summary>
    private const bool OptBubbleBack = true;

    /// <summary>Level 3's <c>max_hash_size</c>; drives match_init's table size.</summary>
    private const int OptMaxHash = 0x01000000;

    /// <summary>
    /// The <c>encode_opt</c> parse: a Viterbi DP over a 4096-byte window instead
    /// of <c>encode</c>'s greedy runs, with no adaptive penalty at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Transcribed from the 2.11.8.0 decompilation (<c>0x180075D70</c>); see
    /// <c>encode_opt_2118_dec.txt</c> for the annotated pseudocode this mirrors.
    /// The symbol layer, block framing and fallback rules are shared with
    /// <see cref="Compress"/> - only the PARSE differs.
    /// </para>
    /// <para>
    /// Each DP entry is 12 dwords: cost, prev index, offset code
    /// (-1 = literal run), the eight rep offsets and offs0. Entries are filled
    /// lazily: a run starts with entry 0 live and entry 1 at infinity, and every
    /// candidate that can reach further extends the frontier, initialising what it
    /// passes over. The cheapest way in is priced four literal-run lengths back,
    /// the eight rep slots, every length of every hash candidate, and - with
    /// bubble_back - the same match re-anchored at an earlier start.
    /// </para>
    /// <para>
    /// When the run stops, the frontier entry gets a 4097 sentinel cost and the
    /// prev links are inverted into forward links <em>through that same cost
    /// field</em>, so the walk reads <c>DP[node].cost</c> as "next". The walk emits
    /// literal runs (gap = node - prev, code -1) and matches, then LABEL_152 emits
    /// any long match that bypassed the DP - which is found FIRST, before the chain.
    /// </para>
    /// </remarks>
    public byte[] CompressOpt(ReadOnlySpan<byte> src)
    {
        Reset();
        _secondHash = true;
        _shortMatch = true;
        InitMatcher(src.Length, OptMaxHash);
        Array.Fill(_optCacheTarget, -1);

        _out.Add((byte)(Tag & 0xFF));
        _out.Add((byte)(Tag >> 8));

        if (src.Length == 0)
        {
            return _out.ToArray();
        }

        // encode_opt pushes src[0] out as a raw literal before the parse starts
        // (`v35[1] = -32760`, i.e. PutBits(src[0], 8)), guarded on sym_cur != sym_end
        // - a buffer this encoder does not have a limit for.
        PutBits(src[0], 8);

        // blkStart is es->prev_block_end: the block being built, and the base of
        // the page-crossing test. cur is the absolute cursor.
        int blkStart = 0;
        int cur = 1;
        var snap = SaveModels();
        var repSnap = new uint[RepSlots];
        Array.Copy(_rep, repSnap, RepSlots);
        uint rep0Snap = _rep0;

        if (src.Length > 1)
        {
            // Declared out here so the goto targets below never jump over a
            // declaration - the control flow is a direct transcription of the
            // original's gotos.
            int runStart = 0, window = 0, target = 0, bCur = 0, ctx = 0, slot = 0;
            int stopLen = 0, matchCount = 0, litrun = 0, bSrc = 0;
            uint frontier = 0, hiWater = 0, i40 = 0, pendingLen = 0, pendingCode = 0;
            uint gate = 0, bestRepLen = 0;
            int lastBubbled = -1, bubbledEnd = 0;
            bool stored = false;

            do
            {
                runStart = cur;
                window = Math.Min(src.Length - runStart, OptWindow);

                // --- entry 0 is the run start, seeded with the live rep state ---
                _dp[0] = 0;
                _dp[1] = 0;
                _dp[2] = 0xFFFFFFFF;
                for (int k = 0; k < RepSlots; k++)
                {
                    _dp[3 + k] = _rep[k];
                }
                _dp[11] = _rep0;
                _dp[12] = 0xFFFFFFFF;   // entry 1 pre-seeded at infinity

                frontier = 1;
                hiWater = 1;
                i40 = 0;
                pendingLen = 0;
                pendingCode = 0;
                lastBubbled = -1;
                bubbledEnd = 0;

            fillTop:
                target = runStart + (int)i40;
                bCur = (int)i40 * OptStride;
                ctx = target & 3;

                // --- literal runs of 1..4 back, priced with the SOURCE entry's
                //     offs0 and each literal's own pos&3 context ---
                litrun = 1;
                bSrc = bCur - OptStride;
                while (true)
                {
                    if (i40 < (uint)litrun)
                    {
                        break;
                    }

                    uint acc = _dp[bSrc];
                    int srcOffs = (int)_dp[bSrc + 11];
                    for (int j = 1; j <= litrun; j++)
                    {
                        int p = target - j;
                        byte back = p >= srcOffs ? src[p - srcOffs] : (byte)0;
                        acc += (uint)_litlen[p & 3].CostOf((byte)(src[p] - back));
                    }

                    if (acc < _dp[bCur])
                    {
                        _dp[bCur] = acc;
                        _dp[bCur + 2] = 0xFFFFFFFF;
                        _dp[bCur + 1] = i40 - (uint)litrun;
                    }

                    litrun++;
                    bSrc -= OptStride;
                    if (litrun > 4)
                    {
                        break;
                    }
                }

                MaterializeRep(bCur);

                if (i40 == frontier)
                {
                    goto chain;
                }
                if (_dp[bCur] >= 0xE0000000)
                {
                    goto label129;
                }

                stopLen = (src.Length - target) & ~7;
                if (stopLen > MaxStopLength)
                {
                    stopLen = MaxStopLength;
                }
                else if (stopLen < 2)
                {
                    // LABEL_124: no search here, and the frontier is NOT synced
                    // from the high-water mark on this path.
                    goto nextPos;
                }

                slot = target & 0xFFF;
                if (_optCacheTarget[slot] != target)
                {
                    _optCacheTarget[slot] = target;
                    matchCount = FindMatch(src, target, src.Length, _optMatches, slot * 8).Item1;
                    _optCount[slot] = matchCount;
                }
                else
                {
                    matchCount = _optCount[slot];
                }

                // --- the eight rep candidates ---
                gate = 1;
                bestRepLen = 1;
                for (int r = 0; r < RepSlots; r++)
                {
                    uint off = _dp[bCur + 3 + r];
                    if (off == 0 || off > (uint)target)
                    {
                        continue;
                    }

                    int rlen = CommonPrefix(src, target, target - (int)off, stopLen);
                    if ((uint)rlen <= gate)
                    {
                        continue;
                    }

                    // v146 = the best rep length so far; the gate is what the hash
                    // candidates are later measured against, and it never moves.
                    bestRepLen = (uint)rlen;
                    if (rlen >= 0x100)
                    {
                        // A long match bypasses the DP entirely.
                        pendingCode = (uint)r;
                        pendingLen = (uint)rlen;
                        goto label129;
                    }
                    if (i40 + (uint)rlen > (uint)window)
                    {
                        goto label129;
                    }

                    ExtendFrontier(i40 + (uint)rlen, ref hiWater);

                    uint cand = _dp[bCur]
                              + (uint)LitLenCost(ctx, rlen)
                              + (uint)_offsTop[ctx].CostOf(r);
                    int tb = (int)(i40 + (uint)rlen) * OptStride;
                    if (cand < _dp[tb])
                    {
                        _dp[tb] = cand;
                        _dp[tb + 1] = i40;
                        _dp[tb + 2] = (uint)r;
                    }

                    gate = bestRepLen;
                }

                // --- bubble bookkeeping runs before the hash loop, not inside it ---
                if (i40 == (uint)bubbledEnd)
                {
                    lastBubbled = -1;
                }

                if (matchCount == 0)
                {
                    frontier = hiWater;
                    goto nextPos;
                }

                int hashIdx = 0;
                while (true)
                {
                    Match hm = _optMatches[slot * 8 + hashIdx];
                    uint offs = (uint)hm.Dist;
                    uint len = (uint)hm.Length;
                    uint hcode = offs + 7;

                    if (len <= gate)
                    {
                        goto hashNext;
                    }
                    if (len >= 0x100)
                    {
                        pendingCode = hcode;
                        pendingLen = len;
                        goto label129;
                    }
                    if (i40 + len > (uint)window)
                    {
                        goto label129;
                    }

                    uint endPos = i40 + len;
                    ExtendFrontier(endPos, ref hiWater);

                    uint baseCost = _dp[bCur] + (uint)OffsCost(ctx, (int)offs);
                    int l0 = len <= 2 ? (int)len : (int)len - 1;
                    for (int l = l0; l <= (int)len; l++)
                    {
                        int tb = (int)(i40 + (uint)l) * OptStride;
                        uint lc = (uint)LitLenCost(ctx, l);
                        if (lc + baseCost < _dp[tb])
                        {
                            _dp[tb] = lc + baseCost;
                            _dp[tb + 1] = i40;
                            _dp[tb + 2] = hcode;
                        }
                    }

                    // ---- bubble_back: re-price the match anchored earlier ----
                    if (OptBubbleBack && offs != (uint)lastBubbled)
                    {
                        uint reach = (uint)(target - (int)offs);
                        if (reach > i40)
                        {
                            reach = i40;
                        }

                        uint back = 0;
                        int q = target - 1;
                        while (back < reach)
                        {
                            if (src[q] != src[q - (int)offs])
                            {
                                break;
                            }
                            back++;
                            q--;
                        }

                        if (back != 0)
                        {
                            if (back > i40)
                            {
                                back = i40;
                            }
                            if (endPos > (uint)bubbledEnd)
                            {
                                lastBubbled = (int)offs;
                                bubbledEnd = (int)endPos;
                            }

                            uint bEnd = back + len;
                            uint bStart = i40 - back;
                            if (bEnd > (uint)stopLen)
                            {
                                bEnd = (uint)stopLen;
                            }

                            // ctx is the ABSOLUTE position of the new start: the
                            // original casts each term to a byte first, which is
                            // equivalent because 256 is a multiple of 4.
                            int bctx = (runStart + (int)bStart) & 3;
                            uint bBase = _dp[(int)bStart * OptStride]
                                       + (uint)OffsCost(bctx, (int)offs);
                            uint l1 = back + 1 > bEnd - 1 ? back + 1 : bEnd - 1;
                            if (l1 <= bEnd)
                            {
                                for (uint bl = l1; bl <= bEnd; bl++)
                                {
                                    int tb = (int)(bStart + bl) * OptStride;
                                    uint lc = (uint)LitLenCost(bctx, (int)bl);
                                    if (lc + bBase < _dp[tb])
                                    {
                                        _dp[tb] = lc + bBase;
                                        _dp[tb + 1] = bStart;
                                        _dp[tb + 2] = hcode;
                                    }
                                }
                            }
                        }
                    }

                hashNext:
                    hashIdx++;
                    if (hashIdx >= matchCount)
                    {
                        frontier = hiWater;
                        break;
                    }
                }

            nextPos:
                if (++i40 > frontier)
                {
                    goto chain;
                }
                goto fillTop;

            label129:
                // A long match, an over-long window reach, or cost overflow all end
                // the run at the CURRENT index - not at the frontier.
                frontier = i40;

            chain:
                // Sentinel, then invert every prev link into a forward link through
                // the cost field: entry[prev(x)].cost = x, down to entry[0].cost =
                // the first node. The frontier keeps 4097, which is the terminator.
                _dp[(int)frontier * OptStride] = 4097;
                if (frontier != 0)
                {
                    uint y = frontier;
                    uint p;
                    do
                    {
                        p = _dp[(int)y * OptStride + 1];
                        _dp[(int)p * OptStride] = y;
                        y = p;
                    }
                    while (p != 0);
                }

                uint node = _dp[0];
                if (node <= frontier)
                {
                    uint offs0 = _rep0;
                    while (true)
                    {
                        int nb = (int)node * OptStride;
                        uint gcode = _dp[nb + 2];
                        uint gap = node - _dp[nb + 1];

                        if (gcode == 0xFFFFFFFF)
                        {
                            // Literal run: emitted one byte at a time, re-testing the
                            // page boundary after EACH so a run can be split mid-way.
                            for (uint li = 0; li < gap; li++)
                            {
                                byte lb = cur >= (int)offs0 ? src[cur - (int)offs0] : (byte)0;
                                LitLenEncode(cur & 3, (byte)(src[cur] - lb));
                                cur++;
                                if ((cur ^ blkStart) >= BlockSize)
                                {
                                    EndBlock(src, ref cur, ref blkStart, ref snap,
                                             ref repSnap, ref rep0Snap, out stored);
                                    if (stored)
                                    {
                                        goto label161;
                                    }
                                    offs0 = _rep0;
                                }
                            }
                        }
                        else
                        {
                            LitLenEncode(cur & 3, (int)gap + 254);
                            OffsetEncode(cur & 3, (int)gcode);
                            UpdateRep((int)gcode);
                            cur += (int)gap;
                            if ((cur ^ blkStart) >= BlockSize)
                            {
                                EndBlock(src, ref cur, ref blkStart, ref snap,
                                         ref repSnap, ref rep0Snap, out stored);
                                if (stored)
                                {
                                    goto label161;
                                }
                            }
                            offs0 = _rep0;
                        }

                        node = _dp[nb];   // the cost field now holds "next"
                        if (node > frontier)
                        {
                            break;
                        }
                    }
                }

                // LABEL_152: a long match that bypassed the DP is emitted FIRST,
                // ahead of nothing - the chain above is already done. Note the flush
                // here does NOT check prev_block_stored.
                if (pendingLen != 0)
                {
                    LitLenEncode(cur & 3, (int)pendingLen + 254);
                    OffsetEncode(cur & 3, (int)pendingCode);
                    UpdateRep((int)pendingCode);
                    cur += (int)pendingLen;
                    if ((cur ^ blkStart) >= BlockSize)
                    {
                        EndBlock(src, ref cur, ref blkStart, ref snap,
                                 ref repSnap, ref rep0Snap, out stored);
                    }
                }

            label161:
                ;   // a stored block abandons the chain and restarts the parse
            }
            while (cur < src.Length);
        }

        EndBlock(src, ref cur, ref blkStart, ref snap, ref repSnap, ref rep0Snap, out _);
        return _out.ToArray();
    }

    /// <summary>
    /// Raise the DP frontier, initialising every entry it passes over to infinity.
    /// </summary>
    /// <remarks>
    /// Entries are written only after this runs, and every write is a
    /// strictly-better-than-infinity test - so an entry the frontier has not
    /// reached yet must read as infinity, not as the array's zero default. Skipping
    /// this silently turns every un-reached entry into a free path.
    /// </remarks>
    private void ExtendFrontier(uint to, ref uint hiWater)
    {
        if (hiWater >= to)
        {
            return;
        }

        for (uint k = hiWater + 1; k <= to; k++)
        {
            _dp[k * OptStride] = 0xFFFFFFFF;
        }
        hiWater = to;
    }

    /// <summary>
    /// Build an entry's rep state from the entry it points at, applying
    /// <see cref="UpdateRep"/>'s rotation to a copy.
    /// </summary>
    /// <remarks>
    /// Three arms, all verified against the raw stores rather than the
    /// decompiler's rendering: a literal copies its predecessor verbatim, a code
    /// of 8 or more sets offs[6]/offs0 to <c>code - 7</c> and pushes the old offs[6]
    /// into offs[7], and a code below 8 is the ordinary move-to-front rotation
    /// (offs[1..code] shift up, offs[0] takes the selected slot, the tail is left
    /// alone). The source is always the previous entry, never the one being built.
    /// </remarks>
    private void MaterializeRep(int b)
    {
        uint code = _dp[b + 2];
        int pb = (int)_dp[b + 1] * OptStride;

        if (code == 0xFFFFFFFF)
        {
            for (int k = 0; k < RepSlots; k++)
            {
                _dp[b + 3 + k] = _dp[pb + 3 + k];
            }
            _dp[b + 11] = _dp[pb + 11];
            return;
        }

        if (code >= RepSlots)
        {
            _dp[b + 9] = code - 7;
            _dp[b + 11] = code - 7;
            for (int k = 0; k < 6; k++)
            {
                _dp[b + 3 + k] = _dp[pb + 3 + k];
            }
            _dp[b + 10] = _dp[pb + 9];
            return;
        }

        uint v = _dp[pb + 3 + (int)code];
        _dp[b + 3] = v;
        _dp[b + 11] = v;
        for (int i = (int)code; i > 0; i--)
        {
            _dp[b + 3 + i] = _dp[pb + 3 + i - 1];
        }
        for (int i = (int)code + 1; i < RepSlots; i++)
        {
            _dp[b + 3 + i] = _dp[pb + 3 + i];
        }
    }

    public byte[] Compress(ReadOnlySpan<byte> src)
    {
        Reset();
        _out.Add((byte)(Tag & 0xFF));
        _out.Add((byte)(Tag >> 8));

        // The hash chain spans the whole stream, not one block: a match in block
        // N routinely reaches back into block N-1. Clearing these per block
        // leaves every block after the first unable to find anything, which
        // costs ratio and - because the encoder's window no longer matches what
        // it emits - can desynchronise the stream outright.
        //
        // D2R hardcodes compression level 2, whose CompressParameters are
        // lookahead=4, max_hash_size=8388608, second_hash=1, short_match=1.
        //
        // Both second_hash AND short_match are live at level 2, so the match
        // finder drives the second-hash probe and the 1024-entry short-match
        // chain. Treating short_match as off was a real candidate-set gap.
        _secondHash = true;
        _shortMatch = true;
        InitMatcher(src.Length, 8388608);

        if (src.Length == 0)
        {
            return _out.ToArray();
        }

        // A block-start literal is emitted by the decoder only while the cursor
        // is still at dest_beg (BitKnit2.BlockHeader: `if (dest_cur == dest_beg
        // && dest_cur != dest_end)`). dest_beg is the start of the whole section,
        // so that holds for the FIRST block and never again - every later block
        // begins with an ordinary model symbol. Emitting putbits(byte, 8) per
        // block shifts the record stream by one from the second block onward and
        // corrupts it immediately.
        PutBits(src[0], 8);

        // `pos` is granny's `prev_block_end`. It is both the start of the block
        // being built and the base of the page-crossing test below, and it MOVES
        // every time a block ends - including in the middle of a literal run. That
        // is why the parse and the block loop are one method here rather than a
        // per-block helper: a helper could not split a run across the boundary.
        int pos = 0;
        int p = 1;

        // The parse adapts the models as it resolves each symbol, and it rotates
        // the rep-offset cache as it emits matches. If a block then loses Flush's
        // size test and is stored raw, the decoder decodes none of those symbols,
        // so it applies neither the adaptation nor the rotation - and BOTH must go
        // back to exactly what they were when that block began.
        //
        // The rep rollback is easy to overlook and immediately fatal when missed: a
        // literal is coded as a delta against offs0, so a stale offs0 left behind by
        // a discarded parse makes the very next block's first literal decode to the
        // wrong byte. granny snapshots both together (`snapshot_rep`, `snapshot_model`
        // in encoder_flush_block) and restores both on the stored path.
        var snap = SaveModels();
        var repSnap = new uint[RepSlots];
        Array.Copy(_rep, repSnap, RepSlots);
        uint rep0Snap = _rep0;

        while (true)
        {
            // granny's model context for this position. Verified: `p & 3`
            // round-trips on every real section; a single shared context made all
            // 400 fail and pushed the size ratio from 1.096 to 1.361, which is
            // decisive - the encoder must agree with the DECODER's context
            // selection, and position rotation is the variant that agrees.
            int ctx = p & 3;

            // granny does not choose between a match and a single literal. Each
            // outer step opens a RUN of `lookahead` positions (4 at level 2, fewer
            // near the end) and, inside that run, repeats:
            //
            //   1. run match_find plus the whole candidate selection AT THE CURRENT
            //      cursor, keeping the winner in CURSOR-RELATIVE offsets;
            //   2. price the literal under that cursor into the running total
            //      WITHOUT emitting it and WITHOUT touching the models;
            //
            // until the run is spent, then emit either the run of literals or the
            // best match preceded by the literals before it (LABEL_104). A match
            // found a few bytes into the run is therefore reachable, with the
            // intervening literals emitted ahead of it - which the previous port
            // could not express at all, because it only ever considered a match at
            // the current cursor.
            //
            // encode_dec.txt: `v20 = lookahead; if (v8 - v18 <= lookahead) v20 =
            // v8 - v18;` is the budget, and LABEL_102 prices a literal with no
            // litlen_encode call at all.
            int runBudget = src.Length - p < Lookahead ? src.Length - p : Lookahead;

            // Per-position seeds: v101 / v22 / v100 / v26 / v103 / v104. bestStart
            // and bestEnd are CURSOR-RELATIVE (v110 is `-&v13[v18]`, the negated
            // cursor at run start), which is what makes the acceptance subtraction
            // below measure the candidate's extra advance even when the two
            // candidates were priced at different offsets into the run.
            long runCost = 0;            // v101, the literal running total
            long bestCost = 0x40000000; // v22, the 2^30 sentinel
            int bestStart = -1;          // v100/v21
            int bestEnd = 0;             // v26/v106
            int bestCode = 0;            // v103/v24
            int bestLen = 0;             // v104/v25

            for (int d = 0; d < runBudget; d++)
            {
                int c = p + d;
                ctx = c & 3;

                // Room for a match at `c`. granny bounds a candidate length by
                // stop_len alone - (src_size - pos) & ~7, capped at 0x2018 - and
                // NOT by any block edge, because its flush test runs after the
                // cursor has advanced: a match is free to carry the cursor across
                // a 64 KiB page boundary, and the block simply ends wherever it
                // landed.
                //
                // The `& ~7` is not cosmetic: the decoder copies matches 8 bytes
                // at a time and rejects any whose (length + 7) & ~7 runs past
                // dest_end.
                int maxSafe = (src.Length - c) & ~7;
                int limit = maxSafe > MaxStopLength ? MaxStopLength : maxSafe;

                if (limit != 0)
                {
                    // stop_len is source-relative in granny, not block-relative:
                    // the block boundary is only discovered afterwards, from
                    // where the cursor came to rest.
                    FindMatchesCached(src, c);

                    // ---- (1) the eight rep candidates --------------------------
                    // granny unrolls rep.offs[i] as separate gated blocks, each
                    // recomputing the length with an 8-byte xor/bsf compare rather
                    // than trusting match_find. The cost is a single MTF code i -
                    // no bot, no raw bits - which is why a long rep match can
                    // undercut a short explicit one.
                    //
                    // The GATE (edi) tracks the best length PRICED so far, not the
                    // best length ACCEPTED: it is raised BEFORE the acceptance
                    // compare, so a candidate that is priced and then refused
                    // still raises it for the candidates after it. Seeded to 1 per
                    // position, and it applies to the rep blocks only.
                    int gateLen = 1;

                    for (int i = 0; i < RepSlots; i++)
                    {
                        uint rd = _rep[i];
                        if (rd == 0 || rd > (uint)c)
                        {
                            continue;
                        }

                        int rLen = CommonPrefix(src, c, c - (int)rd, limit);
                        if (rLen <= gateLen)
                        {
                            continue;
                        }

                        long repCost = LitLenCost(ctx, rLen) + _offsTop[ctx].CostOf(i);

                        // Raise the gate BEFORE the accept test, as granny does.
                        gateLen = rLen;

                        long candTotal = runCost + repCost;
                        long threshold = bestCost + _penalty * (long)((d + rLen) - bestEnd);
                        ParseTrace?.Add($"  d={d} c={c} rep[{i}] len={rLen} run={runCost} cand={candTotal} " +
                                        $"best={bestCost} bestEnd={bestEnd} thr={threshold} " +
                                        (candTotal < threshold ? "ACCEPT" : "reject"));
                        if (candTotal >= threshold)
                        {
                            continue;
                        }

                        bestCost = candTotal;
                        bestStart = d;
                        bestEnd = d + rLen;
                        bestCode = i;     // 0..7: an MTF code, not a distance
                        bestLen = rLen;
                    }

                    // ---- (2) the hash candidates ------------------------------
                    // NO gate and NO length recompute in this loop. granny runs ONE
                    // separate loop over the match_find array (encode
                    // 0x148542E40) that takes Match.Length and Match.Dist straight
                    // from the struct and prices them without consulting the gate.
                    for (int mi = 0; mi < MatchCount; mi++)
                    {
                        int useLen = _matches[mi].Length > limit ? limit : _matches[mi].Length;
                        if (useLen < MinEmittedMatch)
                        {
                            continue;
                        }

                        long candTotal = runCost + LitLenCost(ctx, useLen) + OffsCost(ctx, _matches[mi].Dist);
                        long threshold = bestCost + _penalty * (long)((d + useLen) - bestEnd);
                        ParseTrace?.Add($"  d={d} c={c} hash len={useLen} dist={_matches[mi].Dist} run={runCost} " +
                                        $"cand={candTotal} best={bestCost} bestEnd={bestEnd} thr={threshold} " +
                                        (candTotal < threshold ? "ACCEPT" : "reject"));
                        if (candTotal >= threshold)
                        {
                            continue;
                        }

                        bestCost = candTotal;
                        bestStart = d;
                        bestEnd = d + useLen;
                        bestCode = _matches[mi].Dist + 7;
                        bestLen = useLen;
                    }
                }

                // ---- LABEL_102: price the literal at `c`, costing only --------
                // No litlen_encode here, so the models stay untouched and the
                // literal priced now is resolved against the same model state at
                // emit time below. A literal's symbol is its DIFFERENCE against
                // the byte at rep0 back, which is exactly what the decoder adds.
                int back = c >= (int)_rep0 ? src[c - (int)_rep0] : 0;
                runCost += LitCost(ctx, (byte)(src[c] - back));
            }

            // ---- LABEL_104: the literal run, or literals + the best match ----
            // `v92 = v22 + v17 * (v20 - v26)`: the run's cost is compared against
            // the best match's cost plus the penalty for the bytes the match does
            // NOT consume, so the match wins only if it beats the whole run.
            long runThreshold = bestCost + _penalty * (long)(runBudget - bestEnd);
            bool takeMatch = runCost >= runThreshold;
            int litCount = takeMatch ? bestStart : runBudget;
            ParseTrace?.Add($"run p={p} budget={runBudget} bestStart={bestStart} bestEnd={bestEnd} " +
                            $"bestLen={bestLen} bestCode={bestCode} bestCost={bestCost} " +
                            $"runCost={runCost} thr={runThreshold} pen={_penalty} " +
                            $"=> {(takeMatch ? $"MATCH len={bestLen} after {litCount} lits" : $"{litCount} lits")}");
            int lit = 0;
            bool stored = false;

            // granny emits the literal prefix ONE BYTE AT A TIME and re-tests the
            // page boundary after EACH one (`if ((v117->prev_block_end ^ v18) >=
            // 0x10000) encoder_flush_block(...)`, encode_dec.txt:552). A run that
            // straddles the edge is therefore split across blocks mid-run, and the
            // remainder continues in the next block with the decision ALREADY made
            // - it is never re-costed. Testing only once per step lets the cursor
            // overshoot the edge, producing a single block that the native decoder
            // rejects outright (a 65537-byte stream decoded to 65537 bytes whose
            // last byte was 0, where granny emits two blocks and matches exactly).
            //
            // When the block that straddles the edge is stored raw, granny abandons
            // the run and returns to the top of the parse loop
            // (encode_dec.txt:556-560 `goto LABEL_126` -> `$parse_revert`), so the
            // remaining literals are re-decided rather than carried over.
            // `cur` is the ABSOLUTE cursor and must be separate from `p`: EndBlock
            // can move it BACKWARD on the raw path, because granny clamps the
            // stored length to the page edge it was aiming at and the parse then
            // restarts from that clamp. Advancing `p` inside the loop as well as
            // indexing by `lit` counted the step twice, so the second literal read
            // one byte past the end of the buffer.
            int cur = p;
            while (lit < litCount)
            {
                int lb = cur >= (int)_rep0 ? src[cur - (int)_rep0] : 0;
                LitLenEncode(cur & 3, (byte)(src[cur] - lb));
                lit++;
                cur++;

                if ((cur ^ pos) >= BlockSize)
                {
                    EndBlock(src, ref cur, ref pos, ref snap, ref repSnap, ref rep0Snap, out stored);
                    if (stored)
                    {
                        break;
                    }
                }
            }

            p = cur;

            if (!stored && takeMatch)
            {
                // The match starts at the CURRENT cursor - p has already moved
                // past the emitted literal prefix - so it is encoded under `p & 3`.
                LitLenEncode(p & 3, bestLen + 254);
                OffsetEncode(p & 3, bestCode);
                UpdateRep(bestCode);

                p += bestLen;

                if ((p ^ pos) >= BlockSize)
                {
                    EndBlock(src, ref p, ref pos, ref snap, ref repSnap, ref rep0Snap, out stored);
                }
            }

            // End of the parse step: granny updates the adaptive penalty here,
            // UNLESS a block was stored raw this step - LABEL_126 does `v17 =
            // v109; goto $parse_revert;` and skips the update entirely.
            if (!stored)
            {
                AdvancePenalty(takeMatch ? bestCost : runCost,
                               takeMatch ? bestEnd : runBudget);
            }

            if (p >= src.Length)
            {
                // granny's tail: `if (v18 >= src_size) { encoder_flush_block(v117,
                // v99, src_size, &m, &rep); return v117->out_written; }`.
                EndBlock(src, ref p, ref pos, ref snap, ref repSnap, ref rep0Snap, out stored);
                return _out.ToArray();
            }
        }
    }

    /// <summary>
    /// End the block being built at <paramref name="p"/> and start the next one.
    /// </summary>
    /// <remarks>
    /// granny's flush test is <c>(prev_block_end ^ cursor) &gt;= 0x10000</c>, so a
    /// block that starts mid-page ends on the NEXT page edge rather than 64 KiB
    /// after itself. The returned cursor is the next block's start, which the raw
    /// fallback can move BACKWARD: granny clamps the stored raw length to the page
    /// edge it was aiming at, so a parse that overshot re-starts from that clamp.
    /// </remarks>
    /// <param name="stored">Set when the block was stored raw.</param>
    private void EndBlock(
        ReadOnlySpan<byte> src,
        ref int p,
        ref int pos,
        ref (DefSumModel.ModelState[] Litlen, DefSumModel.ModelState[] OffsTop, DefSumModel.ModelState OffsBot) snap,
        ref uint[] repSnap,
        ref uint rep0Snap,
        out bool stored)
    {
        stored = false;

        // NOTE: _recs is NOT cleared here. Flush is what folds the block's records
        // into output bytes (and clears them itself, on both the compressed and
        // the stored-raw path). Clearing them first would hand Flush an empty block
        // and every small block would fall back to raw.
        //
        // An empty block emits nothing at all, which is granny's own guard in
        // encoder_flush_block: `if (a3 == v5) return v5;` (sub_180016550 - it
        // skips the whole body when the cursor equals the block start). Without
        // this, a flush that already ran at exactly src_size is followed by the
        // tail flush, which appends a second 2-byte marker: every input that ends
        // on a page edge came out exactly 2 bytes longer than granny's.
        if (Math.Min(p, src.Length) - pos <= 0)
        {
            return;
        }

        int uncomp = Math.Min(p, src.Length) - pos;
        if (!Flush(uncomp, snap, repSnap, rep0Snap))
        {
            // The clamp is to the first page edge strictly AFTER the block start. Writing
            // it as `(pos + BlockSize) & ~0xFFFF` looks equivalent but is not: it
            // evaluates to 0 for pos == 0 and to 65536 for pos == 65537, both of
            // which sit at or BEHIND the block start, so a raw store there writes
            // nothing and hands EndBlock a cursor that moves backward. Block starts
            // are no longer page-aligned - a match can carry a block past an edge
            // and the next one begins wherever it landed - so the two must be
            // kept distinct.
            int pageEnd = ((pos >> 16) + 1) << 16;
            int rawEnd = Math.Min(p, pageEnd);
            if (rawEnd <= pos)
            {
                rawEnd = p;
            }

            StoreRaw(src, pos, rawEnd);
            p = rawEnd;
            stored = true;
        }

        pos = p;

        // The next block starts from whatever state the decoder now holds.
        snap = SaveModels();
        repSnap = new uint[RepSlots];
        Array.Copy(_rep, repSnap, RepSlots);
        rep0Snap = _rep0;
    }
    /// <summary>
    /// Greedy hash-chain match search.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one deliberate divergence from granny, which uses the same
    /// idea but with level-dependent chain depth, a second hash and
    /// short-match candidates. It changes the compression ratio, never the
    /// decompressed output.
    /// </para>
    /// <para>
    /// The chain is the classic two-array form: <c>head[h]</c> is the newest
    /// position hashing to h, and <c>prev[i]</c> links a position to the
    /// previous one with the same hash. Deriving the predecessor from the
    /// position - rather than storing it in the hash slot - is what keeps the
    /// walk from revisiting the position just inserted.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Serve the candidates for <paramref name="pos"/> through granny's
    /// per-context match cache, computing them only when that context last saw a
    /// different position (encode's `if (v127[v30] != v29)`).
    /// </summary>
    /// <remarks>
    /// The cached arm deliberately does NOT call <see cref="InsertUntil"/>, which
    /// is why it goes through <see cref="FindMatch"/> as a whole rather than
    /// skipping only the search: native wraps the insert and the find in the same
    /// guard, so a cache hit advances neither the table nor the candidates.
    /// </remarks>
    private void FindMatchesCached(ReadOnlySpan<byte> src, int pos)
    {
        int ctx = pos & 3;
        if (_mfCachePos[ctx] == pos)
        {
            MatchCount = _mfCacheCount[ctx];
            Array.Copy(_mfCacheMatches, ctx * 8, _matches, 0, 8);
            return;
        }

        FindMatch(src, pos, src.Length, _matches);
        _mfCachePos[ctx] = pos;
        _mfCacheCount[ctx] = MatchCount;
        Array.Copy(_matches, 0, _mfCacheMatches, ctx * 8, 8);
    }

    private (int Length, int Dist) FindMatch(ReadOnlySpan<byte> src, int pos, int end,
                                             Match[] matches, int at = 0)
    {
        // A faithful port of granny's match_find (0x148544A00) reading the packed
        // hash table that InsertUntil maintains.
        InsertUntil(src, pos);

        int count = 0;
        if (_fillPos < pos)
        {
            return (0, 0);
        }

        // hash = (0x9E3779B1 * u32) ^ ROL(0x9E3779B1 * u32, 19)
        uint w = ReadU32(src, pos);
        uint prod = unchecked(0x9E3779B1u * w);
        uint h = prod ^ System.Numerics.BitOperations.RotateLeft(prod, 19);

        uint mix = 0;
        if (_secondHash)
        {
            // The mix reads the u16 at src+pos+4, NOT src+pos+2.
            //
            // granny's match_find (0x180016340) computes
            //     (982451653 * h - 617768103 * (u16)v5[2])  where
            //     v5 = (WORD*)(src + pos), so v5[2] is src+pos+4;
            // match_insert (0x180016220) reads `*(u16*)(result + a2 + 4)`,
            // the same offset. InsertUntil below already uses +4.
            //
            // We used to read +2 here, so FindMatch looked up rows under a hash
            // that InsertUntil had never inserted under: the second-hash pass and
            // the XOR-rehash pass both scanned effectively empty rows, candidates
            // went missing, matches came out too short and every real section
            // compressed larger than granny's.
            uint w2 = ReadU16(src, pos + 4);
            uint v = unchecked(982451653u * h - 617768103u * w2);
            uint h2 = v ^ System.Numerics.BitOperations.RotateLeft(v, 19);
            mix = h ^ h2;
            h = h2;
        }

        // granny's stop_len for match_find: (src_size - pos) & ~7, capped at 0x2018.
        // The 8-byte alignment is required because the decoder copies matches a
        // qword at a time.
        int stopLen = (src.Length - pos) & ~7;
        if (stopLen > MaxStopLength)
        {
            stopLen = MaxStopLength;
        }
        uint rowMask = _rowMask;

        for (int pass = 0; ; pass++)
        {
            for (int slot = 0; slot < 8; slot++)
            {
                uint entry = _mfHash[(h & rowMask) + (uint)slot];

                // The low 26 bits are the stored position; a candidate is only
                // considered when the tag bits (the rest) agree.
                if ((h ^ entry) > 0x3FFFFFF)
                {
                    continue;
                }

                int cand = (int)(entry & 0x3FFFFFF);
                if (cand >= pos)
                {
                    continue;
                }

                int offs = pos - cand;
                if (offs > MaxMatchOffset)
                {
                    continue;
                }

                int len = CommonPrefix(src, pos, cand, stopLen);
                if (len < MinMatchLength)
                {
                    continue;
                }

                matches[at + count].Dist = offs;
                matches[at + count].Length = len;
                count++;
                if (count == 8)
                {
                    break;
                }
            }

            if (count == 8)
            {
                break;
            }

            // granny re-hashes with `hash ^= mix` and rescans the same rows, which
            // surfaces candidates inserted under the other hash.
            if (pass == 0 && _secondHash)
            {
                uint next = h ^ mix;
                if (next == h)
                {
                    break;
                }
                h = next;
                continue;
            }
            break;
        }

        // granny's short-match chain. The packed table's probe requires a 26-bit
        // tag to agree, which for a 2- or 3-byte match is rarely true across
        // distinct positions - so those lengths come from this separate 2-byte
        // hash instead. It walks BACKWARD from the most recent position and stops
        // as soon as the distance stops increasing, which bounds it to the last
        // 1024 bytes.
        if (_shortMatch)
        {
            uint idx = _shortHash[ShortHash(src, pos)];
            int best = 2;
            long dist = pos - idx;

            // The chain must yield a position STRICTLY BEFORE `pos`. `InsertUntil`
            // has already inserted `pos` itself, so the first slot of the chain is
            // `pos` and `dist` is 0 on the first probe; a zero distance makes
            // `CommonPrefix(src, pos, pos, stopLen)` compare the buffer against
            // itself and report the full stop_len, i.e. a maximal-length match at
            // distance 0. The candidate is then coded as MTF slot 7 (dist + 7),
            // which is a valid-looking but wrong match that silently corrupts the
            // stream instead of failing. Uninitialised chain slots can also run
            // past `pos`, giving a negative distance, so both ends are guarded.
            while (dist > 0 && dist <= 0x3FF)
            {
                int len = CommonPrefix(src, pos, pos - (int)dist, stopLen);
                if (len >= best)
                {
                    if (count == 8)
                    {
                        break;
                    }

                    matches[at + count].Dist = (int)dist;
                    matches[at + count].Length = len;
                    count++;
                    best = len;
                }

                long prev = idx & 0x3FF;
                idx = _shortPrev[prev];
                long next = pos - idx;
                if (next <= dist)
                {
                    break;
                }
                dist = next;
            }
        }

        MatchCount = count;
        return (count, 0);
    }

    /// <summary>granny's acceptance floor and offset ceiling.</summary>
    private const int MinMatchLength = 2;
    private const int MaxMatchOffset = 0x3FFFFE0;

    /// <summary>
    /// Shortest match the encoder will actually EMIT.
    ///
    /// Set to 3 deliberately. The format CAN represent length 2 - the decoder
    /// treats any litlen symbol &gt;= MatchThreshold (0x100 = 256) as a match and
    /// derives length = sym - 254 - so 3 is a choice, not a limit.
    ///
    /// Measured: dropping this to 2 was tried and REVERTED. It is not neutral.
    /// On one section it looked like a large win (matches 8,354 -&gt; 14,647,
    /// stream coverage 26.8% -&gt; 32.0%, against granny's 26,447 matches and
    /// 41.6% coverage), which is what motivated it: granny does emit many short
    /// matches and we clearly under-use them. But across 400 real sections the
    /// size ratio got WORSE, 1.0928 -&gt; 1.1120, and the test suite slowed from
    /// ~15s to over 2.5 minutes.
    ///
    /// The lesson: granny is not simply "more permissive". It reaches for short
    /// matches only when its cost test says they pay, and emitting them
    /// unconditionally spends bits. Our cost comparison evidently still differs
    /// from granny's, and unconditional short matches amplify that error. The
    /// real remaining difference is the SHORT-MATCH COST TEST, not the length
    /// threshold - so fix the cost model before touching this constant again.
    ///
    /// RE-TESTED AFTER THE COST FIX: the cost base was wrong by a whole bit
    /// (<c>log2(65536/freq)</c> where the model totals 32768 - see
    /// <c>DefSumModel.CostOf</c>), which is precisely the "cost comparison differs
    /// from granny's" this paragraph predicted. With the base corrected
    /// (sweep 664/712 -&gt; 701/712) the floor was dropped back to 2, because native's
    /// hash-candidate loop has no length test of any kind: it takes
    /// <c>Match.Length</c> straight from the struct and prices it
    /// (encode_dec.txt:472-519). Filtering at 3 there dropped length-2 candidates
    /// granny does price - e.g. <c>mixed/200</c> at position 42, where granny emits
    /// a 2-byte match at distance 42 that this filter discarded outright.
    /// </summary>
    private const int MinEmittedMatch = 2;


    /// <summary>
    /// Opt-in trace of the candidate search, mirroring <c>BitKnit2.DecisionLog</c>
    /// on the encode side. When set, each run records every candidate that got
    /// past the length gate (with its cost, threshold and verdict) followed by the
    /// LABEL_104 decision.
    /// </summary>
    /// <remarks>
    /// The decode-side log says <i>where</i> two streams diverge; this says <i>why</i>
    /// - a rejected candidate prints its cost against the threshold it failed, which
    /// is the only way to tell a candidate-set gap from a scoring gap. Off by default.
    /// </remarks>
    public List<string>? ParseTrace { get; set; }

    /// <summary>
    /// Diagnostic: run the matcher up to <paramref name="pos"/> and return the
    /// candidates <c>FindMatch</c> offers there, as <c>(length, distance)</c>.
    /// </summary>
    /// <remarks>
    /// The decode-side counterpart is <c>BitKnit2.DecisionLog</c>. Together they
    /// localise an encoder mismatch precisely: decode granny's stored bytes to get
    /// the decision granny actually made at a position, then ask this what our
    /// matcher offered there. If granny's match is absent, the candidate SET is
    /// wrong and no cost rule could have picked it; if present, selection is at
    /// fault. Read-only with respect to <paramref name="src"/>; does not disturb a
    /// subsequent <see cref="Compress"/> call, which re-initialises the matcher.
    /// </remarks>
    public (int len, int dist)[] PeekCandidates(ReadOnlySpan<byte> src, int pos)
    {
        _secondHash = true;
        _shortMatch = true;
        InitMatcher(src.Length, 8388608);
        InsertUntil(src, pos);
        FindMatch(src, pos, src.Length, _matches);

        var r = new (int, int)[MatchCount];
        for (int i = 0; i < MatchCount; i++)
        {
            r[i] = (_matches[i].Length, _matches[i].Dist);
        }
        return r;
    }

    /// <summary>
    /// Bounds-checked little-endian u32 read. granny reads 6 bytes at each hash
    /// position and may therefore look up to 5 bytes past src_len; the input is
    /// padded so these reads always land in the buffer.
    /// </summary>
    private static uint ReadU32(ReadOnlySpan<byte> src, int at)
    {
        if (at + 4 <= src.Length)
        {
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(at));
        }

        uint v = 0;
        for (int i = 0; i < 4; i++)
        {
            uint b = at + i < src.Length ? src[at + i] : (byte)0;
            v |= b << (8 * i);
        }
        return v;
    }

    /// <summary>Bounds-checked little-endian u16 read. See <see cref="ReadU32"/>.</summary>
    private static ushort ReadU16(ReadOnlySpan<byte> src, int at)
    {
        if (at + 2 <= src.Length)
        {
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(at));
        }

        ushort v = 0;
        for (int i = 0; i < 2; i++)
        {
            ushort b = at + i < src.Length ? src[at + i] : (byte)0;
            v |= (ushort)(b << (8 * i));
        }
        return v;
    }

    /// <summary>
    /// Common-prefix length, matching granny's 8-byte compare plus BitScanForward
    /// on the first differing qword.
    /// </summary>
    private static int CommonPrefix(ReadOnlySpan<byte> src, int a, int b, int maxLen)
    {
        int n = 0;
        while (n + 8 <= maxLen)
        {
            ulong x = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(a + n));
            ulong y = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(b + n));
            if (x != y)
            {
                return n + (System.Numerics.BitOperations.TrailingZeroCount(x ^ y) >> 3);
            }
            n += 8;
        }
        while (n < maxLen && src[a + n] == src[b + n])
        {
            n++;
        }
        return n;
    }

    /// <summary>match_init: size and clear the packed table and the short chain.</summary>
    /// <remarks>
    /// The table size is <c>min(data_size, 0x3FFFFE0)</c> rounded UP to a power of
    /// two, starting from 4096 and stopping at <c>max_hash_size</c>. It is
    /// therefore input-size dependent - the previous fixed <c>1 &lt;&lt; 16</c> gave
    /// a different table for every input than granny does, which changes which
    /// candidates collide and therefore the whole downstream parse.
    /// <para>
    /// <c>row_mask = (i - 1) &amp; 0xFFFFFFF8</c> keeps the table 8-aligned, which is
    /// what lets each row be scanned as a contiguous 8-entry window.
    /// </para>
    /// </remarks>
    private void InitMatcher(int dataSize, int maxHashSize)
    {
        int cap = 0x4000000;
        if (dataSize < 0x3FFFFE0)
        {
            cap = dataSize;
        }

        int i = 4096;
        for (; i < cap; i *= 2)
        {
            if (i >= maxHashSize)
            {
                break;
            }
        }

        _mfHash = new uint[i];
        _rowMask = (uint)((i - 1) & 0xFFFFFFF8);
        _fillPos = 0;

        if (_shortMatch)
        {
            Array.Clear(_shortHash);
            Array.Clear(_shortPrev);
        }
    }

    /// <summary>
    /// match_insert_until: index positions below targetPos. Each insert shifts its
    /// row down one slot and writes the new entry at slot 0, keeping an 8-deep
    /// chain per row.
    /// </summary>
    private void InsertUntil(ReadOnlySpan<byte> src, int targetPos)
    {
        int fill = _fillPos;
        if (fill >= targetPos)
        {
            return;
        }

        while (fill < targetPos)
        {
            // granny reads 6 bytes at each position (u32 at +0, u16 at +4), so it
            // reads past src_len near the end of the buffer. The caller guarantees
            // the same slack here: Compress works on a copy padded with a sentinel
            // that cannot match (see Padded), which reproduces granny's behaviour
            // without reading out of bounds.
            uint w = ReadU32(src, fill);
            uint prod = unchecked(0x9E3779B1u * w);
            uint h = prod ^ System.Numerics.BitOperations.RotateLeft(prod, 19);
            Push(_mfHash, h & _rowMask, fill, h);

            if (_secondHash)
            {
                uint w2 = ReadU16(src, fill + 4);
                uint v = unchecked(982451653u * h - 617768103u * w2);
                uint h2 = v ^ System.Numerics.BitOperations.RotateLeft(v, 19);
                Push(_mfHash, h2 & _rowMask, fill, h2);
            }

            if (_shortMatch)
            {
                // 2-byte hash over src+fill (NOT src+fill+4), chained through a
                // 1024-entry table. Note this reads at +0, unlike the second hash
                // above which reads at +4.
                uint h3 = ShortHash(src, fill);
                _shortPrev[fill & 0x3FF] = _shortHash[h3];
                _shortHash[h3] = (uint)fill;
            }

            fill++;
        }
        _fillPos = fill;
    }

    /// <summary>
    /// The short-match chain's bucket: a 2-byte multiplicative hash mixed with a
    /// 4-byte one, masked to the 1024-entry chain.
    /// </summary>
    private static uint ShortHash(ReadOnlySpan<byte> src, int at)
    {
        uint w2 = ReadU16(src, at);
        uint w4 = ReadU32(src, at);
        uint lo = unchecked(27011u * w2) & 0xFFFF;
        uint hi = unchecked(0x853C49E9u * w4) >> 13;
        return (lo ^ (hi & 0xFFFF)) & 0x7FF;
    }

    /// <summary>
/// Insert one entry at slot 0 of an 8-deep row, shifting the rest up one slot.
/// </summary>
/// <remarks>
/// granny does this with two unaligned 16-byte moves (sub_180016220,
/// 0x180016262-0x180016288): <c>xmm0</c> &lt;- entries 0..3,
/// <c>xmm1</c> &lt;- entries 3..6, then stores them one slot later and writes the
/// new entry at slot 0. Entry 3 is written twice with the same value, so the net
/// effect is that entries <b>0..6</b> move up one slot - seven entries - and the
/// previous slot-7 entry is left alone. That is exactly the loop below.
/// </remarks>
/// <remarks>
/// Measured: "fixing" this to shift all EIGHT entries (the reading we first
/// took from the SSE pair) is WRONG. It changed the candidate set and dropped
/// stream coverage 26.8% -&gt; 20.0% while worsening the corpus ratio
/// 1.0928 -&gt; 1.1305. The overlapping store is not an off-by-one; it is how the
/// single pass shifts the row. Do not "fix" this again.
/// </remarks>
private static void Push(uint[] table, uint row, int pos, uint hash)
    {
        for (int k = 6; k >= 0; k--)
        {
            table[row + (uint)(k + 1)] = table[row + (uint)k];
        }
        table[row] = (uint)(pos & 0x3FFFFFF) | (hash & 0xFC000000);
    }

    /// <summary>Record one symbol against a model, applying its adaptation now.</summary>
    private void Emit(DefSumModel m, int sym) => _recs.Add(EncRec.Resolved(m.EncodeSymbol(sym)));

    /// <summary>
    /// litlen_encode: emit a litlen symbol, splitting into an escape symbol plus
    /// raw bits once past the 300-symbol direct range.
    /// </summary>
    private void LitLenEncode(int ctx, int val)
    {
        if (val >= LongMatchThreshold)
        {
            int v10 = val - 286;
            int nbits = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v10);
            Emit(_litlen[ctx], nbits + 287);
            PutBits((uint)v10, nbits);
        }
        else
        {
            Emit(_litlen[ctx], val);
        }
    }

    /// <summary>
    /// Rotate the rep-offset cache after emitting a match, exactly as granny does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="code"/> is the offset CODE just emitted: 0..7 selects a rep
    /// slot directly, >= 8 is an explicit distance (<c>code - 7</c>).
    /// </para>
    /// <para>
    /// For <c>code &gt;= 8</c> granny does NOT do a plain LZMA move-to-front. It
    /// writes the new distance into <b>both</b> <c>offs[0]</c> and <c>offs[6]</c>
    /// and pushes the old <c>offs[6]</c> into <c>offs[7]</c>:
    /// </para>
    /// <code>
    /// uint old6 = rep.offs[6];
    /// rep.offs0 = rep.offs[6] = (uint)(code - 7);
    /// rep.offs[7] = old6;
    /// </code>
    /// <para>
    /// This two-deep tail is real, not a decompiler artifact, and it is load
    /// bearing: slot 6 keeps tracking the most recent large distance so a later
    /// code-6 lookup stays correct. Reproducing only the <c>offs0</c> half leaves
    /// slot 6 stale and silently reorders which distances the next candidates see.
    /// </para>
    /// <para>
    /// For <c>code &lt; 8</c> it is the ordinary LZMA rotation: the selected slot
    /// moves to the front and everything from it down to slot 1 shifts up by one.
    /// </para>
    /// </remarks>
    private void UpdateRep(int code)
    {
        if (code >= RepSlots)
        {
            uint d = (uint)(code - 7);
            uint old6 = _rep[6];
            _rep0 = d;
            _rep[6] = d;
            _rep[7] = old6;
            return;
        }

        uint v = _rep[code];
        _rep0 = v;
        for (int i = code; i > 0; i--)
        {
            _rep[i] = _rep[i - 1];
        }
        _rep[0] = v;
    }

    /// <summary>
    /// offset_encode: the two-level distance code. A code below 8 selects a
    /// move-to-front rep slot; anything higher is an explicit distance + 7.
    /// </summary>
    private void OffsetEncode(int ctx, int codeVal)
    {
        if (codeVal >= RepSlots)
        {
            int d = codeVal - 8;
            Emit(_offsTop[ctx], (d & 0x1F) + 8);
            int v18 = (d >> 5) + 1;
            int bsym = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v18);
            Emit(_offsBot, bsym);
            PutBits((uint)v18, bsym);
        }
        else
        {
            Emit(_offsTop[ctx], codeVal);
        }
    }

    /// <summary>
    /// Price of encoding a match of <paramref name="len"/> bytes at
    /// <paramref name="offs"/>, in granny's cost units (2^-14 bits).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors <see cref="LitLenEncode"/> and <see cref="OffsetEncode"/> symbol for
    /// symbol, so the price charged here is the price actually paid. Where those
    /// methods emit an escape symbol plus raw bits, the escape symbol is the model
    /// cost and the raw bits are <c>nbits</c> whole bits - which is precisely the
    /// <c>&lt;&lt; 14</c> term granny adds for lengths above 288:
    /// <c>litlen cost = model[bsr(len-32) + 287] + (bsr(len-32) &lt;&lt; 14)</c>.
    /// </para>
    /// <para>
    /// Read-only: it queries the models without adapting them, because every
    /// candidate at a position is priced against the same pre-symbol state.
    /// </para>
    /// </remarks>
    /// Price of coding a match length, in granny's 2^-14-bit cost units.</summary>
    /// <remarks>
    /// Mirrors <see cref="LitLenEncode"/> symbol for symbol, so the price charged
    /// is the price actually paid. Where that method emits an escape symbol plus
    /// raw bits, the escape symbol is the model cost and the raw bits are
    /// <c>nbits</c> whole bits - which is precisely the <c>&lt;&lt; 14</c> term
    /// granny adds for lengths above 288.
    /// <para>
    /// Read-only: it queries the models without adapting them, because every
    /// candidate at a position is priced against the same pre-symbol state.
    /// </para>
    /// </remarks>
    private long LitLenCost(int ctx, int len)
    {
        int val = len + 254;
        if (val >= LongMatchThreshold)
        {
            int nbits = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)(val - 286));
            return _litlen[ctx].CostOf(nbits + 287) + ((long)nbits << 14);
        }
        return _litlen[ctx].CostOf(val);
    }

    /// <summary>
    /// Price of coding one literal, in granny's 2^-14-bit cost units.
    /// </summary>
    /// <remarks>
    /// This is LABEL_102's costing-only read, which never calls
    /// <c>litlen_encode</c>:
    /// <code>
    /// v23 = v33-&gt;model.costs[(unsigned __int8)(*v28 - v27[v123])] + v101;
    /// </code>
    /// A literal's symbol is the byte itself - its delta against the byte
    /// <c>rep.offs0</c> back - and always lands in 0..255, below the 288 escape
    /// threshold. So unlike <see cref="LitLenCost"/> there is no <c>+254</c> and
    /// no raw-bits term: one model code, full stop.
    /// <para>
    /// Read-only, like the other cost helpers: the models are only adapted by
    /// the emitting calls, and every candidate inside one run is priced against
    /// the same pre-symbol state.
    /// </para>
    /// </remarks>
    private long LitCost(int ctx, byte lit) => _litlen[ctx].CostOf(lit);

    /// <summary>
    /// Price of coding a LITERAL byte, in granny's 2^-14-bit cost units.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="LitLenCost"/>. That helper takes a match LENGTH
    /// and shifts it into the symbol range with <c>+ 254</c>; a literal's symbol
    /// IS its byte. granny's LABEL_102 reads
    /// <c>v33-&gt;model.costs[(uint8)(*v28 - v27[v123])]</c> - the byte's own index -
    /// so adding 254 here prices every literal byte >= 34 as a long match. That
    /// both mis-ranks candidates against real matches and asks the model for a
    /// symbol index it does not have.
    /// </remarks>
    private long LitCost(int ctx, int sym) => _litlen[ctx].CostOf(sym);

    /// <summary>
    /// Price of coding an explicit distance, in granny's 2^-14-bit cost units.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="OffsetEncode"/>. Only reachable for <c>code &gt;= 8</c>;
    /// rep candidates are priced directly against <c>top.costs[i]</c> instead.
    /// </remarks>
    private long OffsCost(int ctx, int offs)
    {
        int codeVal = offs + 7;
        if (codeVal >= RepSlots)
        {
            int d = codeVal - 8;
            int v18 = (d >> 5) + 1;
            int bsym = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v18);
            return _offsTop[ctx].CostOf((d & 0x1F) + 8)
                 + _offsBot.CostOf(bsym) + ((long)bsym << 14);
        }
        return _offsTop[ctx].CostOf(codeVal);
    }

    /// <summary>
    /// Price of a whole match: litlen for the length plus offset for the
    /// distance, in granny's 2^-14-bit cost units.
    /// </summary>
    private long MatchCost(int ctx, int len, int offs)
        => LitLenCost(ctx, len) + OffsCost(ctx, offs);

    /// <summary>
    /// Advance the adaptive distance penalty, once per parse step, as `encode`
    /// does at the end of every iteration:
    /// <code>
    /// v118 += ((v102 &lt;&lt; 8) / v91 + 1 - v118 + 4) &gt;&gt; 3;
    /// v17  = (v118 + 128) &gt;&gt; 8;   v109 = v17;
    /// </code>
    /// </summary>
    /// <param name="winCost">`v102` - the winning candidate's cost.</param>
    /// <param name="winEnd">`v91` - the winning candidate's end advance.</param>
    /// <remarks>
    /// <para>
    /// `v102`/`v91` are the pair chosen at LABEL_104: the literal run when the
    /// run won, `bestCost`/`bestEnd` when the match won. The `<c>(cost &lt;&lt; 8)
    /// / end</c>` term is a cost-per-byte rate, so the penalty converges on what
    /// one more byte of advance is expected to cost - which is why it must not be
    /// treated as the constant 0x20000 the seed starts it at.
    /// </para>
    /// <para>
    /// Three details are load-bearing. The shift is on the 32-bit register, so
    /// the cost is taken modulo 2^24 before the divide rather than widened; the
    /// divide is <b>signed</b> and truncates toward zero (x86 <c>idiv</c>, which
    /// is what C# <c>/</c> on ints does); and the estimate is 64-bit with
    /// arithmetic shifts throughout. Reordering any of them drifts the penalty,
    /// which then changes which candidate wins on every later position.
    /// </para>
    /// <para>
    /// <c>winEnd == 0</c> has no native behaviour to mirror - `idiv` would raise
    /// #DE - and cannot occur: a zero run budget is guarded by `if (v20 != 0)`,
    /// and a zero bestEnd only ever accompanies a match that lost, where
    /// `winEnd` is the run budget. Skipping is the defensive reading.
    /// </para>
    /// </remarks>
    private void AdvancePenalty(long winCost, int winEnd)
    {
        if (winEnd <= 0)
        {
            return;
        }

        int scaled = unchecked((int)winCost << 8);
        int perByte = scaled / winEnd;
        int plusOne = unchecked(perByte + 1);

        _estimate += ((long)plusOne - _estimate + 4) >> 3;
        _penalty = (_estimate + 128) >> 8;
    }

    /// <summary>
    /// ransenc_putbits: append raw bits as flagged records. Runs longer than 16
    /// bits split, high half first, with a 0xC000 continuation after.
    /// </summary>
    private void PutBits(uint val, int nbits)
    {
        uint v = val & ((1u << nbits) - 1);

        if (nbits >= 0x10)
        {
            _recs.Add(EncRec.Resolved(new RansEncSym((ushort)(v >> 16), (ushort)((nbits - 16) | 0x8000))));
            _recs.Add(EncRec.Resolved(new RansEncSym((ushort)v, 0xC000)));
        }
        else
        {
            _recs.Add(EncRec.Resolved(new RansEncSym((ushort)v, (ushort)(nbits | 0x8000))));
        }
    }

    /// <summary>
    /// ransenc_attempt_flush: fold the reverse symbol buffer into emitted 16-bit
    /// words. Records are walked last-to-first because the decoder runs
    /// backwards. Returns false when the block should be stored raw.
    /// </summary>
    private bool Flush(int uncompSize,
                      (DefSumModel.ModelState[] Litlen, DefSumModel.ModelState[] OffsTop, DefSumModel.ModelState OffsBot) snap,
                      uint[] repSnap,
                      uint rep0Snap)
    {
        // Words are emitted to DECREASING addresses; collecting them forward
        // and reversing at the end reproduces the original layout.
        var words = new List<ushort>();

        uint v7 = 0x10000;
        uint v9 = 0x10000;

        for (int i = _recs.Count - 1; i >= 0; i--)
        {
            EncRec rec = _recs[i];
            var s = new RansEncSym(rec.Start, rec.Flag);
            uint freq = s.Freq;

            if (freq >= 0x8000)
            {
                if (freq == 0xC000)
                {
                    words.Add(s.Start);
                }
                else
                {
                    int nb = (int)(freq & 0x7FFF);
                    uint v14 = v7;
                    if (nb != 0 && v7 >= (1u << (32 - nb)))
                    {
                        words.Add((ushort)v7);
                        v14 = v7 >> 16;
                    }
                    v7 = v9;
                    v9 = (uint)(s.Start | (v14 << nb));
                }
            }
            else
            {
                uint v13 = v7;
                if (v7 >= freq << 17)
                {
                    words.Add((ushort)v7);
                    v13 = v7 >> 16;
                }
                v7 = v9;
                v9 = (uint)(s.Start + (v13 % freq) + ((v13 / freq) << 15));
            }
        }

        // The size test measures the rANS PAYLOAD ONLY - the block header is
        // not part of it. sub_180015D10, before writing the header:
        //
        //     v15 = v3 - v8;                                  // payload bytes
        //     if ((v8 - v4) < 0xA
        //         || v15 + 10 >= a2
        //         || v15 + (v15 >> 7) >= a2) return nullptr;   // -> store raw
        //
        // where a2 is the block's uncompressed size. Counting the header
        // towards v15 makes the test far stricter than granny's: for a 17-byte
        // all-zero block granny emits 10 bytes total, of which 8 are header, so
        // it tests 2 + 10 >= 17 (accept) where the header-inclusive reading
        // tests 10 + 10 >= 17 (reject). That mismatch pushed small, very
        // compressible blocks onto the store-raw path.
        int payloadBytes = words.Count * 2;

        // Tail: normalise both registers to RansTop, which the decoder's
        // MarkTransition requires before it will advance the window.
        int v17 = 31 - System.Numerics.BitOperations.LeadingZeroCount(v7);
        words.Add((ushort)v7);
        int v18 = v17 - 16;
        if (v18 != 0 && v9 >= (1u << (32 - v18)))
        {
            words.Add((ushort)v9);
            v9 >>= 16;
        }

        uint v19 = (uint)((v9 << v18) | ((v7 >> 16) & ((1u << v18) - 1)));
        if (v19 >= 0x10000000)
        {
            words.Add((ushort)v19);
            v19 >>= 16;
        }

        // Written low-half first: the original stores result[1] = v20 and then
        // result[0] = HIWORD(v20), and result[0] sits at the LOWER address.
        // So the write order is v20 then HIWORD(v20), which the Reverse() below
        // turns back into w0 = HIWORD(v20), w1 = v20 - the order BlockHeader
        // reads them in. Reversing these two silently corrupts every stream.
        uint v20 = (uint)((uint)v18 | (16u * v19));
        words.Add((ushort)(v20 & 0xFFFF));
        words.Add((ushort)(v20 >> 16));

        words.Reverse();

        // encoder_flush_block's rejection test, in sub_180015D10. All three arms
        // matter and none of them is "the output is small":
        //   (v8 - v4) < 0xA                  room for the header below the payload
        //   v15 + 10 >= a2                    not enough saving
        //   v15 + (v15 >> 7) >= a2            ditto, tighter
        //
        // v3 - v4 is the record area the caller filled, 4 bytes per record, and
        // v8 has walked down from v3 by exactly v15. So (v8 - v4) is
        // 4*records - payload: the room left beneath what was just written.
        int roomBelow = (_recs.Count * 4) - payloadBytes;

        if (roomBelow < 0x0A
            || payloadBytes + 10 >= uncompSize
            || payloadBytes + (payloadBytes >> 7) >= uncompSize)
        {
            // The decoder decodes a raw block without touching the models or the
            // rep cache, so the adaptation AND the rotation the discarded parse
            // applied must not survive.
            RestoreModels(snap);
            Array.Copy(repSnap, _rep, RepSlots);
            _rep0 = rep0Snap;
            _recs.Clear();
            return false;
        }

        // Block framing: there is NO separate marker word. decode_chunk peeks a
        // u16 to choose between raw and compressed, but for the compressed case
        // it does NOT advance the cursor - BlockHeader then re-reads those same
        // two bytes as w0. So the first emitted word must itself be non-zero,
        // and it is the header's w0, not a marker.

        foreach (ushort w in words)
        {
            _out.Add((byte)w);
            _out.Add((byte)(w >> 8));
        }

        _recs.Clear();
        return true;
    }


    /// <summary>
    /// The uncompressed fallback: a u16 zero marker followed by the raw bytes,
    /// padded to an even length.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pad byte is emitted <b>iff the raw payload is an odd number of
    /// bytes</b>. An earlier version of this method never emitted one, on the
    /// reasoning that granny's decoder keeps 16 bytes of lookback slack
    /// (<c>read_mark = src_end - 16</c>) and therefore does not need it. That
    /// reasoning is right about the decoder and wrong about the encoder: the
    /// capture it rested on used a 144-byte payload, and 144 is even, so no
    /// capture of that shape can observe the odd case at all.
    /// </para>
    /// <para>
    /// Confirmed against the native 2.12.0.14 encoder
    /// (<c>GrannyBitKnitCompress</c>), which is ground truth rather than a
    /// transcription. Over every size from 1 to 80 bytes of incompressible
    /// input, granny's output is exactly one byte longer than ours when the
    /// size is odd and identical when it is even, and the extra byte is
    /// always <c>0x00</c>.
    /// </para>
    /// <para>
    /// Blocks are cut on 64 KiB source boundaries, so in practice only the
    /// final block of a stream can be odd.
    /// </para>
    /// </remarks>
    private void StoreRaw(ReadOnlySpan<byte> src, int pos, int end)
    {
        _out.Add(0);
        _out.Add(0);
        for (int i = pos; i < end; i++)
        {
            _out.Add(src[i]);
        }

        // Round the raw payload up to an even length.
        if (((end - pos) & 1) != 0)
        {
            _out.Add(0);
        }
    }
}
