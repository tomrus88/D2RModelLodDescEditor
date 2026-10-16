namespace Granny.Format;

/// <summary>
/// BitKnit2 decompressor, ported from Granny 2.12 as shipped in D2R.exe.
/// </summary>
/// <remarks>
/// Reference points, both verified:
/// <list type="bullet">
/// <item>D2R.exe (Granny 2.12.0.12), IDA db <c>2b648d1dca70</c>:
/// decode_chunk <c>sub_14428ADB0</c>, decode_bitknit_step <c>sub_14428A2A0</c>.</item>
/// <item>granny2_x64.dll (2.11.8.0), IDA db <c>f5fb18ae6d81</c>:
/// decode_chunk <c>0x180074F40</c>, decode_bitknit_step <c>0x180074680</c>.</item>
/// </list>
/// Both binaries are the same build as their databases; the two versions
/// differ in the framing details called out on the members below.
/// </remarks>
public sealed class BitKnit2
{
    /// <summary>Section format code for BitKnit2 compression.</summary>
    public const uint FormatCode = 4;

    /// <summary>
    /// Stream magic that opens every BitKnit2 payload, immediately after the
    /// fixup blob's 4-byte compressed-length prefix.
    /// </summary>
    public const ushort Tag = 0x75B1;

    // Framing states (state field at a1+72 in 2.12).
    private const int StHeader = 1;
    private const int StAfterTag = 2;
    private const int StMark = 3;
    private const int StCopyEven = 4;
    private const int StCopyOdd = 5;
    private const int StBlock = 7;
    private const int StBlockNext = 8;

    /// <summary>
    /// Bytes of lookback granny keeps past the last payload byte.
    /// </summary>
    /// <remarks>
    /// granny's <c>decode_chunk</c> is called with <c>read_mark = src_end - 16</c>,
    /// and <c>BitKnit_DecompressChunk</c> finalises by feeding a 16-byte zero
    /// buffer, so the decoder's input buffer always carries 16 bytes of slack
    /// past the compressed data. Those bytes are readable but are not part of
    /// the stream - they exist so a final copy can reach the last payload byte
    /// without the encoder having to emit a pad.
    /// </remarks>
    private const int LookbackSlack = 16;

    private const uint RansTop = 0x10000;
    private const int MatchThreshold = 0x100;
    private const int LongMatchThreshold = 0x120;

    // Not readonly: Reset() rebuilds them per stream.
    private DefSumModel[] _litlen;
    private DefSumModel[] _offsTop;
    private DefSumModel _offsBot;

    private readonly uint[] _rep = new uint[8];
    private uint _rep0;
    private uint _mtf;

    private uint _x;
    private uint _xalt;

    private int _destBeg;
    private int _destCur;
    private int _destMark;
    private int _destEnd;
    private int _state;

    public BitKnit2()
    {
        _litlen = DefSumModel.BuildLitlen();
        (_offsTop, _offsBot) = DefSumModel.BuildOffs();

        // decoder_init writes the 64-bit constant 0x100000001 across two
        // adjacent slots of a u32[8], so every rep offset starts at 1.
        Array.Fill(_rep, 1u);
        _rep0 = 1;
        _mtf = 16434824;
        _x = RansTop;
        _xalt = RansTop;
        _state = StHeader;
    }

    /// <summary>Reset to the post-decoder_init state.</summary>
    /// <remarks>
    /// A <see cref="BitKnit2"/> instance carries the full decoder state
    /// (coder registers, adaptive models, rep offsets, cursors), so it is
    /// single-use per stream. Decompress resets it so an instance can be
    /// reused, which keeps callers from silently decoding against stale
    /// adaptive tables.
    /// </remarks>
    public void Reset()
    {
        _litlen = DefSumModel.BuildLitlen();
        (_offsTop, _offsBot) = DefSumModel.BuildOffs();

        // decoder_init writes the 64-bit constant 0x100000001 across two
        // adjacent slots of a u32[8], so every rep offset starts at 1.
        Array.Fill(_rep, 1u);
        _rep0 = 1;
        _mtf = 16434824;
        _x = RansTop;
        _xalt = RansTop;
        _state = StHeader;
        _destBeg = _destCur = _destMark = 0;
        _destEnd = 0;
        _current = null;
        Read = 0;
    }

    /// <summary>Decode a BitKnit2 section payload into a buffer of dstSize bytes.</summary>
    /// <param name="src">Compressed payload (starts with the 0x75B1 tag).</param>
    /// <param name="dstSize">ExpandedDataSize from the section header.</param>
    public byte[] Decompress(ReadOnlySpan<byte> src, int dstSize)
    {
        var dst = new byte[dstSize];
        Reset();
        _current = dst;
        _destBeg = 0;
        _destEnd = dstSize;
        _destCur = 0;
        _destMark = 0;
        Trace = TraceEnabled;

        int read = 0;

        // granny's decode_chunk receives read_mark = src_end - 16, i.e. it
        // always keeps a 16-byte lookback tail past the last payload byte.
        // BitKnit_DecompressChunk makes that concrete: it finalises by calling
        // decoder_feed(&zero_buffer, 0x10), feeding 16 bytes of slack.
        //
        // The previous value here was src.Length - 1. That clamp is one byte
        // too tight for a final even-length raw block: the last payload byte
        // sits exactly ON the mark, so the final copy cannot reach it and the
        // decoder demands a pad byte that granny never writes. Granny's own
        // decoder does not need that pad, and neither should ours.
        //
        // The 16 bytes are virtual: every read is clamped to data.Length by
        // the copy sites, so this only widens the state machine's stopping
        // point, it never reads out of bounds.
        int readMark = src.Length - 1 + LookbackSlack;

        for (int guard = 0; guard < 4096; guard++)
        {
            if (_destCur >= dstSize)
            {
                break;
            }

            int before = _destCur;
            int st = _state;
            read = Chunk(src, dst, read, readMark);
            Read = read;

            if (read > src.Length)
            {
                break;
            }
            if (_destCur == before && st is not (StBlock or StCopyEven or StCopyOdd))
            {
                break;
            }
        }

        return dst;
    }

    /// <summary>Where the decode stopped, for diagnostics against the oracle.</summary>
    public (int DestCur, int ReadPos, int State) Progress => (_destCur, Read, _state);

    /// <summary>
    /// decode_chunk: the framing state machine.
    /// </summary>
    private int Chunk(ReadOnlySpan<byte> data, byte[] outBuf, int read, int readMark)
    {
        int v4 = read;

        if (_state == StHeader)
        {
            ushort tag = (ushort)(data[v4] | (data[v4 + 1] << 8));
            v4 += 2;
            _state = tag == Tag ? StAfterTag : -1;
            _state = AdvanceDestMark();
        }

        while (_state >= 0 && v4 <= readMark)
        {
            int state = _state;

            // Reaching dest_mark - by either the raw copy below or a completed
            // rANS block - means "roll the 64 KiB window forward", which is what
            // MarkTransition does. Handling it here rather than inline in each
            // producer is what lets a raw block be followed by another block:
            // without it a stream that stores an incompressible block mid-way
            // stalls on StMark, because StMark matches no branch below.
            if (state == StMark)
            {

                _state = MarkTransition();
                continue;
            }

            if (state == StAfterTag)
            {
                // bk2decode.txt:494-501 (Granny 2.12, decode_chunk):
                //     v12 = *(_WORD *)v6
                //     if ( *(_WORD *)v6 == 0 ) v6 += 2
                //     v7 = 4 ; if ( v12 != 0 ) v7 = 7
                // The ZERO branch (an uncompressed literal run) advances TWO
                // bytes; the compressed-block branch consumes nothing. An
                // earlier note here claimed 2.12 advanced one byte and 2.11
                // advanced two - that was backwards.
                //
                // This branch does not execute anywhere in the sampled corpus
                // (changing 1 -> 2 is byte-identical on all 40 files tested),
                // so measurement cannot arbitrate; the decompiled 2.12 wins.
                ushort word = (ushort)(data[v4] | (data[v4 + 1] << 8));
                if (word != 0)
                {
                    _state = StBlock;
                }
                else
                {
                    _state = StCopyEven;
                    v4 += 2;
                }
            }
            else if (state is StCopyEven or StCopyOdd)
            {
                // granny: v14 = read_mark - v6; if that >= dest_mark - dest_cur,
                // v14 = dest_mark - dest_cur. read_mark is 16 past the stream,
                // so in practice the output bound wins. Clamp to what the span
                // actually holds; the shortfall is granny's zero slack and is
                // never part of the stream.
                int n = Math.Min(_destMark - _destCur, readMark - v4);
                int avail = data.Length - v4;
                if (n > avail)
                {
                    n = avail;
                }
                if (n > 0)
                {
                    data.Slice(v4, n).CopyTo(outBuf.AsSpan(_destCur));
                    _destCur += n;
                    v4 += n;
                }
                else
                {
                    break;
                }

                int v11 = ((n & 1) ^ (state - StCopyEven)) + StCopyEven;
                _state = v11;

                if (_destCur == _destMark)
                {
                    // 2.12: v4 = v15 + 1, then `if (v17 != 5) v4 = v15`.
                    v4 += 1;
                    if (v11 != StCopyOdd)
                    {
                        v4 -= 1;
                    }

                    // A raw run that reaches dest_mark lands in state 3 exactly
                    // like a compressed block does, so it needs the same
                    // window roll-forward - otherwise state 3 falls through to
                    // decode_chunk's terminal `return` and the stream stops dead
                    // at the first block boundary after a stored-raw block.
                    //
                    // advance_dest_mark gates on the coder registers being back
                    // at their top value. A raw run touches neither, so after a
                    // compressed predecessor they are still wherever that block
                    // left them; returning -1 here would turn a legal stream
                    // into a failure. Only the window arithmetic matters here,
                    // so roll it forward directly when the registers are not at
                    // the top, and otherwise take the state machine's own path.
                    _state = (_x == RansTop && _xalt == RansTop)
                        ? MarkTransition()
                        : AdvanceDestMark();
                }
            }
            else if (state is StBlock or StBlockNext)
            {
                v4 = Step(data, outBuf, v4, readMark);

                // decode_bitknit_step consumes a whole block; the original
                // returns from decode_chunk immediately afterwards unless
                // the block ran to dest_mark, in which case state 3 rolls
                // the window forward.
                if (_state != StMark)
                {
                    return v4;
                }

                _state = MarkTransition();
            }
            else
            {
                return v4;
            }
        }

        return v4;
    }

    /// <summary>
    /// advance_dest_mark: extend the block window, but only when both coder
    /// registers are back at their top value.
    /// </summary>
    private int AdvanceDestMark()
    {
        if (_state < 0)
        {
            return _state;
        }
        if (_x != RansTop || _xalt != RansTop)
        {
            return -1;
        }

        if (_destMark != _destEnd && _destCur != _destEnd)
        {
            int span = _destEnd - _destBeg;
            int upto = ((_destCur - _destBeg) | 0xFFFF) + 1;
            if (upto < span)
            {
                span = upto;
            }
            _destMark = _destBeg + span;
            return _state == StMark ? 2 : _state;
        }

        return 0;
    }

    private int MarkTransition()
    {

        if (_x != RansTop || _xalt != RansTop)
        {
            return -1;
        }
        if (_destMark == _destEnd || _destCur == _destEnd)
        {
            return 0;
        }

        int span = _destEnd - _destBeg;
        int upto = ((_destCur - _destBeg) | 0xFFFF) + 1;
        if (upto < span)
        {
            span = upto;
        }
        _destMark = _destBeg + span;
        return 2;
    }

    /// <summary>
    /// decode_bitknit_step: the block body - rANS symbol decode, literal /
    /// match selection, the long-match extra-bit path, the two-level offset
    /// model and the move-to-front rep stack.
    /// </summary>
    private int Step(ReadOnlySpan<byte> data, byte[] outBuf, int read, int readMark)
    {
        // Context base. 2.12 is explicit that this is -dest_beg (a1+0),
        // NOT the entry cursor (a1+8); the effective slot is
        // ((-dest_beg) + dest_cur) & 3.
        int ctxBase = (-_destBeg) & 3;

        if (_state == StBlock)
        {
            // The block header consumes 8-10 bytes; the symbol loop below
            // must continue from AFTER it, not from the tag.
            int inRead = read;
            read = BlockHeader(data, read);
        }

        // The input bound must NOT gate this loop. The game keeps pulling 16-bit
        // symbol words until dest_mark is reached; once the compressed payload is
        // exhausted the reader simply has nothing left to consume, and decoding
        // continues on the carried state. Gating on `read <= readMark` stopped the
        // final chunk a few bytes early and left the tail of the last string
        // zero-filled. The individual readers below already clamp their own
        // 2-byte fetches, so an exhausted input is safe to run with.
        while (_destCur < _destMark)
        {
            int ctx = _destCur & 3;
            int slot = (ctxBase + ctx) & 3;
            var lit = _litlen[slot];

            int sym = lit.Decode(ref _x, ref _xalt, ref read, data);

            if (sym < MatchThreshold)
            {
                // 0x180074af4..0x180074b0a (2.12): store, then jump back
                // to the top of the loop for the next symbol. There is no
                // inline "second literal" - the loop simply re-enters.

                outBuf[_destCur] = (byte)(outBuf[_destCur - (int)_rep0] + sym);
                _decisions?.Add((_destCur, 0, (int)_rep0, sym));
                _destCur++;
                continue;
            }

            // ---- match ------------------------------------------------
            var top = _offsTop[slot];
            int length;

            if (sym >= LongMatchThreshold)
            {
                // The shift count is `sym - 0x11F`. Hex-Rays prints
                // `v40 - 31` for 2.12, but that is the already-decremented
                // symbol in rbx; the instruction is `lea ecx, [rbx - 0x11F]`.
                // Using sym-31 was measured to collapse decoding entirely.
                int extra = sym - 0x11F;
                uint oldX = _x;
                uint bits = _x >> extra;
                if (bits < 0x10000 && read + 2 <= data.Length)
                {
                    bits = (bits << 16) | (uint)(data[read] | (data[read + 1] << 8));
                    read += 2;
                }
                _x = _xalt;
                _xalt = bits;
                sym = (1 << extra) + 0x11E + (int)(oldX & ((1u << extra) - 1));
            }

            // lea r13, [rbx - 0xFE] -> length is derived AFTER the
            // long-match rewrite, not before it.
            length = sym - 254;

            int tsym = top.Decode(ref _x, ref _xalt, ref read, data);

            int offs;
            if (tsym >= 8)
            {
                int bsym = _offsBot.Decode(ref _x, ref _xalt, ref read, data);

                // DefSumModel.Decode performs the x/xalt swap on exit, so on
                // return _x holds the OLD xalt and _xalt holds the new
                // range. The offs-bot model at 2.12 sub_14428A2A0 writes the
                // fresh range straight into r15d (x) and leaves r12d (xalt)
                // untouched - there is NO swap on this path - so undo the
                // helper's swap:
                //     x    = new range
                //     xalt = old xalt
                (_x, _xalt) = (_xalt, _x);

                // 2.12: v79 = v31 (xalt) ; v31 >>= bsym & 0xF ;
                //        if (v31 < 0x10000) v31 = w | (v31 << 16)
                //        if (bsym >= 0x10)  v79 = w | (v79 << 16)
                uint v62 = _xalt;
                _xalt >>= bsym & 0xF;
                if (_xalt < 0x10000 && read + 2 <= data.Length)
                {
                    _xalt = (_xalt << 16) | (uint)(data[read] | (data[read + 1] << 8));
                    read += 2;
                }
                if (bsym >= 0x10 && read + 2 <= data.Length)
                {
                    v62 = (v62 << 16) | (uint)(data[read] | (data[read + 1] << 8));
                    read += 2;
                }

                // 2.12 writes it as (32 << bsym) - 39 + tsym + 32*((1<<bsym)-1 & v62),
                // which is algebraically the same as the 2.11 form below.
                //
                // Do NOT "simplify" -39 to -7. A side-by-side Python test
                // against the emulated decoder suggested -7 was better, but
                // that harness feeds a single 32-byte window and does not
                // reproduce BitKnit_Decompress's re-feed loop. Applying -7 here
                // collapses the oracle prefix from 42,010 bytes to 84, so -39
                // is correct. Verified by measurement, not by reading.
                offs = unchecked((int)(((uint)(32 << bsym) - 39) + (uint)tsym
                            + ((v62 & ((1u << bsym) - 1)) << 5)));

                int a = (int)((_mtf >> 18) & 7);
                int c = (int)((_mtf >> 21) & 7);
                _rep[c] = _rep[a];
                _rep[a] = (uint)offs;
            }
            else
            {
                // 2.12: v69 = (mtf >> (3 * tsym)) & 7 ;
                //        v70 = rep[v69] ;
                //        v88 = v88 & ~(-8 << (3*tsym)) | ~(...) & (v69 + 8*v88)
                int s = (int)((_mtf >> (3 * tsym)) & 7);
                offs = (int)_rep[s];
                uint field = 0xFFFFFFF8u << (3 * tsym);
                _mtf = (((uint)s + (_mtf << 3)) & ~field) | (_mtf & field);
            }

            if (offs > _destCur - _destBeg || _destEnd - _destCur < ((length + 7) & ~7))
            {
                _state = -1;
                break;
            }

            // Overlapping back-reference copy, 8 bytes at a time.
            int src = _destCur - offs;
            _decisions?.Add((_destCur, length, offs, -1));
            for (int i = 0; i < length; i++)
            {
                outBuf[_destCur++] = outBuf[src++];
            }
            _rep0 = (uint)offs;
        }

        // 2.12 (sub_14428A2A0, 0x14428AB1C; see bk2decode.txt lines 421-423):
        //     if (v78->state >= 0 && dest_cur >= v32)
        //       v78->state = 3;
        // A single match can overshoot dest_mark, leaving _destCur BEYOND
        // it. Without this, the next call's `while (_destCur < _destMark)`
        // is false, the step does nothing, and the section can never finish.
        // That stall was observed on the torso_lod samples, which stopped
        // dead at the 64 KiB window (dest_cur 65576 vs dest_mark 65536).
        //
        // The state >= 0 half matters: the bounds-failure path above sets
        // state = -1 to abort, and without the guard this would resurrect it.
        if (_state >= 0 && _destCur >= _destMark)
        {
            _state = StMark;
        }

        return read;
    }

    /// <summary>Current input cursor (diagnostics).</summary>
    public int Read { get; private set; }

    /// <summary>
    /// The per-block range-coder header.
    /// </summary>
    /// <remarks>
    /// The two header words are combined as <c>w0 &lt;&lt; 16 | w1</c> - note
    /// the order. Getting this backwards silently corrupts every later
    /// symbol and was the single most damaging bug in the Python port.
    /// <code>
    /// r10 = (w0 &lt;&lt; 16) | w1
    /// v13 = (r10 &amp; 0xF) + 0x10
    /// v12 = r10 &gt;&gt; 4
    /// r9  = v12 ; if (v12 &lt; 0x10000) r9 = w | (v12 &lt;&lt; 16), p += 2
    /// x    = r9 &gt;&gt; (v13 &amp; 0xF) ; if (x &lt; 0x10000) x = w | (x &lt;&lt; 16), p += 2
    /// if (v13 &gt;= 0x10) r9 = w | (r9 &lt;&lt; 16), p += 2
    /// xalt = (r9 &amp; ((1 &lt;&lt; v13) - 1)) | (1 &lt;&lt; v13)
    /// </code>
    /// 2.12 asserts the header is already &gt;= 0x10000 (via
    /// <c>__debugbreak()</c>), so real streams take the no-reload path for
    /// the first two steps.
    /// </remarks>
    private int BlockHeader(ReadOnlySpan<byte> data, int read)
    {
        uint w0 = (uint)(data[read] | (data[read + 1] << 8));
        uint w1 = (uint)(data[read + 2] | (data[read + 3] << 8));

        uint r10 = (w0 << 16) | w1;
        int v13 = (int)(r10 & 0xF) + 0x10;
        uint v12 = r10 >> 4;

        // Reloads follow `shl edx, 10h ; or edx, eax`: the value being reloaded
        // goes to the HIGH half and the freshly read 16-bit word to the LOW
        // half. OR-ing the word into the high half instead inverts them and
        // silently corrupts every subsequent symbol.
        int p = read + 4;
        uint r9 = v12;
        if (v12 < 0x10000 && p + 2 <= data.Length)
        {
            p += 2;
            r9 = (v12 << 16) | (uint)(data[p - 2] | (data[p - 1] << 8));
        }

        _x = r9 >> (v13 & 0xF);
        if (_x < 0x10000 && p + 2 <= data.Length)
        {
            p += 2;
            _x = (_x << 16) | (uint)(data[p - 2] | (data[p - 1] << 8));
        }

        if (v13 >= 0x10 && p + 2 <= data.Length)
        {
            p += 2;
            r9 = (r9 << 16) | (uint)(data[p - 2] | (data[p - 1] << 8));
        }

        _xalt = (r9 & ((1u << v13) - 1)) | (1u << v13);

        // 0x18007489d: a block-start literal is emitted when the cursor is
        // still at dest_beg and not at dest_end.
        if (_destCur == _destBeg && _destCur != _destEnd && p + 2 <= data.Length)
        {
            uint v18 = (uint)(data[p] | (data[p + 1] << 8));
            var outBuf = _current!;
            outBuf[_destCur] = (byte)_x;
            uint x2 = _x >> 8;
            _x = _xalt;
            _xalt = x2;
            if (x2 < 0x10000 && p + 2 <= data.Length)
            {
                p += 2;
                _xalt = (x2 << 16) | v18;
            }
            _destCur++;
        }

        Read = p;
        return p;
    }

    /// <summary>Buffer currently being filled, needed by the header literal.</summary>
    private byte[]? _current;

private List<(int pos, int len, int offs, int sym)>? _decisions;

    /// <summary>
    /// When set, records every literal/match decision as
    /// <c>(destPos, length, offset, symbol)</c> - length 0 for a literal, and
    /// symbol -1 for a match.
    /// </summary>
    /// <remarks>
    /// Decoding a stream records the parse that PRODUCED it, so decoding granny's
    /// stored bytes yields granny's own decisions and decoding our bytes yields
    /// ours. Comparing the two logs localises an encoder mismatch to a single
    /// decision instead of an opaque byte offset. Opt-in and off by default.
    /// </remarks>
    public List<(int pos, int len, int offs, int sym)>? DecisionLog
    {
        get => _decisions;
        set => _decisions = value;
    }

    /// <summary>Enable tracing to stderr.</summary>
    public bool TraceEnabled { get; set; }

    /// <summary>Whether this instance is currently tracing.</summary>
    public bool Trace { get; private set; }
}