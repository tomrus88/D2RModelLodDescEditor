namespace Granny.Format;

/// <summary>
/// Measured decoder accuracy, as of the last validation run against
/// granny2_x64.dll. Tests assert these numbers so progress is visible and
/// regressions are loud.
/// </summary>
public static class BitKnit2Status
{
    /// <summary>
    /// Number of leading bytes that match the oracle exactly. Measured on
    /// larm_hvy_lod0.model section 0, which is pinned by a dedicated test.
    /// </summary>
    /// <remarks>
    /// Improved 25 -> 37 -> 96 -> 42,010 across fixes to the reload
    /// half-order, the 128-bit model tables, the block-header cursor, the
    /// framing loop, and the offs-bot register swap. Still NOT byte-exact
    /// end to end.
    /// </remarks>
    public const int ExactPrefixBytes = 42010;

    /// <summary>
    /// Shortest exact prefix across the sampled corpus, measured against the
    /// 2.11 oracle. Guards against regressions anywhere.
    /// </summary>
    /// <remarks>
    /// Was 29,543 when the corpus was 762 files. The corpus has since grown
    /// to 1,462 and now includes models that diverge much earlier (the worst
    /// is legs_med_lod0.model at 13,414), so the floor moved down. This is a
    /// change in the SAMPLE, not a decoder regression: the pinned file above
    /// still measures exactly 42,010.
    ///
    /// Note this floor is a corpus-wide minimum and is therefore only as
    /// stable as the file set. Per-file prefixes are far more informative.
    /// </remarks>
    public const int MinimumPrefixBytes = 13414;

    /// <remarks>
    /// This used to be 34 of the first 400, and the decoder genuinely stalled
    /// that often. It is now 400 of the first 400, and in fact 9,916 of 9,916
    /// files across the whole corpus: the symbol loop was gated on
    /// <c>read &lt;= readMark</c>, so whenever the compressed payload ran out
    /// before <c>dest_mark</c> was reached the tail of the section stayed
    /// zero-filled (always 4-6 bytes, always at the very end).
    /// </remarks>
    public const int ByteExactFileCount = 400;
}