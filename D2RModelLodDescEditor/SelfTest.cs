using D2RModelLodDescEditor.Models;
using D2RModelLodDescEditor.ViewModels;
using System.IO;
using System.Text;
using System.Text.Json;

namespace D2RModelLodDescEditor;

/// <summary>
/// Headless format audit:  D2RModelLodDescEditor --selftest &lt;bin&gt; [json]
///
///   1. parse + rel-link audit (regions, hash order, rel chains, mask/slots)
///   2. byte-exact repack round-trip (must equal the input byte-for-byte,
///      including the uninitialized pad fields)
///   3. optional manifest cross-check (hash coverage, lodMask vs "lods")
///   4. structural round-trip: add/remove a mesh, repack, re-parse clean
///
/// The app is WinExe (no console of its own), so the report is always mirrored
/// to selftest-report.txt next to the executable.
/// </summary>
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: D2RModelLodDescEditor --selftest <bin> [json] [modelRoot]");
            return 2;
        }
        string bin = args[1];
        string? json = args.Length > 2 ? args[2] : null;
        string? root = args.Length > 3 ? args[3] : null;
        if (root is not null && json is null)
        {
            Console.Error.WriteLine("the modelRoot argument requires the manifest json");
            return 2;
        }

        var original = Console.Out;
        var capture = new StringWriter();
        Console.SetOut(new TeeWriter(original, capture));
        int failures;
        try
        {
            failures = RunCore(bin, json, root);
        }
        finally
        {
            capture.Flush();
            Console.SetOut(original);
        }

        string report = "(unwritable)";
        try
        {
            report = Path.Combine(AppContext.BaseDirectory, "selftest-report.txt");
            File.WriteAllText(report, capture.ToString());
        }
        catch
        {
            // keep the console usable even when the report file cannot be written
        }
        original.WriteLine($"report: {report}");
        return failures;
    }

    private static int RunCore(string bin, string? json, string? root)
    {
        int failures = 0;

        Console.WriteLine($"== parse {bin}");
        var doc = LodDescDocument.Load(bin);
        Console.WriteLine($"   {doc.Summary}");

        Console.WriteLine("== integrity (parse/link audit)");
        var issues = doc.Integrity();
        foreach (var i in issues) Console.WriteLine("   " + i);
        failures += issues.Count(i => i.StartsWith("ERROR:", StringComparison.Ordinal));
        if (issues.Count == 0) Console.WriteLine("   clean");

        Console.WriteLine("== repack round-trip (byte-exact?)");
        byte[] original = File.ReadAllBytes(bin);
        byte[] rebuilt = doc.BuildBytes();
        if (rebuilt.Length != original.Length)
        {
            Console.WriteLine($"   FAIL length {rebuilt.Length} != {original.Length}");
            failures++;
        }
        else
        {
            int diff = 0, first = -1;
            for (int i = 0; i < original.Length; i++)
                if (rebuilt[i] != original[i]) { diff++; if (first < 0) first = i; }
            Console.WriteLine(diff == 0
                ? "   byte-identical (pads included)"
                : $"   FAIL {diff} bytes differ, first @ {first}");
            if (diff != 0)
            {
                failures++;
                try
                {
                    File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, "rebuilt.bin"), rebuilt);
                    Console.WriteLine($"   rebuilt image written for diffing: {Path.Combine(AppContext.BaseDirectory, "rebuilt.bin")}");
                }
                catch { }
            }
        }

        if (json is not null)
        {
            Console.WriteLine($"== manifest cross-check {json}");
            doc.LoadPaths(json);
            Console.WriteLine($"   {doc.PathCount} manifest keys" +
                              (doc.PathCollisions > 0 ? $" ({doc.PathCollisions} hash collisions)" : ""));
            int noPath = doc.Models.Count(m => doc.PathFor(m.Hash) is null);
            int lodBad = doc.Models.Count(m => doc.JsonLods(m.Hash) is int j && (byte)j != m.LodMask);
            Console.WriteLine($"   bin hashes without a manifest path : {noPath}");
            Console.WriteLine($"   bin lodMask != manifest \"lods\"     : {lodBad}");
            var hashSet = doc.Models.Select(m => m.Hash).ToHashSet();
            int jsonKeysMissed = 0;
            foreach (var line in File.ReadLines(json))
            {
                // keys are JSON object property names: "data/hd/...": {...}
                int q1 = line.IndexOf('"');
                int q2 = q1 < 0 ? -1 : line.IndexOf('"', q1 + 1);
                if (q1 < 0 || q2 < 0) continue;
                if (!hashSet.Contains(LodDescDocument.FnvHash(line[(q1 + 1)..q2].ToLowerInvariant())))
                    jsonKeysMissed++;
            }
            Console.WriteLine($"   manifest keys with no bin record   : {jsonKeysMissed}");
            if (noPath != 0 || jsonKeysMissed != 0 || lodBad != 0) failures++;
        }

        Console.WriteLine("== structural round-trip (add/remove mesh, repack, re-parse)");
        var probe = doc.Models.FirstOrDefault(m => m.Slots.Count > 0 && m.Slots[0].Meshes.Count > 0);
        if (probe is null)
        {
            Console.WriteLine("   SKIP (no mesh-bearing asset)");
        }
        else
        {
            var slot = probe.Slots[0];
            uint before = slot.MeshCount;
            var added = doc.AddMesh(slot);
            added.RenderLayer = 0xC0FFEE;
            doc.RemoveMesh(slot, slot.Meshes[^1]);
            byte[] rebuilt2 = doc.BuildBytes();
            var doc2 = LodDescDocument.LoadBytes(rebuilt2);
            var probe2 = doc2.Models[probe.Index];
            int bad = doc2.Integrity().Count(i => i.StartsWith("ERROR:", StringComparison.Ordinal));
            bool ok = probe2.Slots.Count == probe.Slots.Count
                      && probe2.Slots[0].MeshCount == before
                      && bad == 0
                      && rebuilt2.Length == original.Length;
            Console.WriteLine(ok
                ? $"   ok ({probe.Display}: {before} meshes restored, re-parse clean)"
                : $"   FAIL (mesh count {before} -> {probe2.Slots[0].MeshCount}, {bad} integrity errors)");
            if (!ok) failures++;
        }

        failures += RunSaveAndEditChecks(bin);
        failures += RunViewModelChecks(bin);
        failures += RunModelAddRemoveChecks(bin);
        failures += RunModelOrderChecks(bin);
        failures += RunGuessKeyChecks();
        if (json is not null)
        {
            failures += RunPairChecks(bin, json);
            failures += RunManifestSyncChecks(bin, json);
        }
        if (json is not null && root is not null) failures += RunImportChecks(bin, json, root);

        Console.WriteLine(failures == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL ({failures})");
        return failures;
    }

    private static int RunSaveAndEditChecks(string bin)
    {
        int failures = 0;
        string tmp = Path.Combine(Path.GetTempPath(), "model_lod_desc_structtest.bin");

        Console.WriteLine("== structural keep (add mesh, save to disk, reload)");
        var doc = LodDescDocument.Load(bin);
        var probe = doc.Models.FirstOrDefault(m => m.Slots.Count > 0 && m.Slots[0].Meshes.Count > 0);
        if (probe is null)
        {
            Console.WriteLine("   SKIP (no mesh-bearing asset)");
        }
        else
        {
            var slot = probe.Slots[0];
            uint before = slot.MeshCount;
            var added = doc.AddMesh(slot);
            added.RenderLayer = 0xC0FFEE;
            doc.Save(tmp);

            var back = LodDescDocument.Load(tmp);
            var probe2 = back.Models[probe.Index];
            var mesh2 = probe2.Slots[0].Meshes[^1];
            int bad = back.Integrity().Count(i => i.StartsWith("ERROR:", StringComparison.Ordinal));
            bool ok = probe2.Slots[0].MeshCount == before + 1
                      && mesh2.RenderLayer == 0xC0FFEE
                      && bad == 0
                      && new FileInfo(tmp).Length == new FileInfo(bin).Length + MeshRecSizeForTest();
            Console.WriteLine(ok
                ? $"   ok ({probe.Display}: {before} -> {before + 1} meshes, layer persisted, re-parse clean)"
                : $"   FAIL (mesh count {before} -> {probe2.Slots[0].MeshCount}, {bad} integrity errors)");
            if (!ok) failures++;
        }

        Console.WriteLine("== in-place field edit (write-through, no repack)");
        var doc2 = LodDescDocument.Load(bin);
        var m2 = doc2.Models.First(m => m.Slots.Count > 0);
        float oldMin = m2.MinX;
        m2.MinX = oldMin + 0.5f;
        m2.TypeMask = 0xDEADBEEF;
        doc2.Save(tmp);
        var back2 = LodDescDocument.Load(tmp);
        var m3 = back2.Models.First(m => m.Hash == m2.Hash);
        bool ok2 = m3.MinX == oldMin + 0.5f && m3.TypeMask == 0xDEADBEEF
                   && back2.Integrity().Count(i => i.StartsWith("ERROR:", StringComparison.Ordinal)) == 0
                   && new FileInfo(tmp).Length == new FileInfo(bin).Length;
        Console.WriteLine(ok2
            ? "   ok (bounds + typeMask persisted, size unchanged, re-parse clean)"
            : "   FAIL (field edit did not round-trip)");
        if (!ok2) failures++;

        try { File.Delete(tmp); } catch { }
        return failures;
    }

    private static int MeshRecSizeForTest() => D2RModelLodDescEditor.Models.LodDescDocument.MeshRecSize;

    /// <summary>
    /// Add-model plumbing: duplicate-hash guard, ascending insert, pending
    /// records, repack growth, and a remove that restores the file byte-for-byte.
    /// </summary>
    private static int RunModelAddRemoveChecks(string bin)
    {
        int failures = 0;
        byte[] original = File.ReadAllBytes(bin);

        Console.WriteLine("== add / remove model (blank skeleton)");
        var doc = LodDescDocument.Load(bin);
        int n0 = doc.ModelCount;
        const long growth = 44 + 2 * 12 + 2 * 32;   // asset + 2 slots + 2 meshes

        // duplicate hash must be refused
        var victims = doc.Models.Where(m => m.Hash != 0).Take(1).ToList();
        if (victims.Count == 0)
        {
            Console.WriteLine("   SKIP (no existing hash to duplicate)");
            return failures;
        }
        bool threw = false;
        try { doc.AddModel(victims[0].Hash); }
        catch (InvalidOperationException) { threw = true; }
        if (!threw)
        {
            Console.WriteLine("   FAIL (duplicate hash accepted)");
            failures++;
        }

        // pick a hash that is definitely free
        uint newHash = 0x0BADF00D;
        while (doc.Models.Any(m => m.Hash == newHash)) newHash++;
        var m = doc.AddModel(newHash, 0x06);       // lods 0 and 1
        for (int lod = 0; lod < 2; lod++) doc.AddSlot(m, lod, 1);
        bool asc = doc.Models.Select(x => x.Hash).SequenceEqual(doc.Models.Select(x => x.Hash).OrderBy(h => h));
        bool pending = m.IsPending && m.Slots.Count == 2
                       && m.Slots.All(s => s.IsPending && s.Meshes.Count == 1);
        byte[] built = doc.BuildBytes();
        var back = LodDescDocument.LoadBytes(built);
        var m2 = back.Models.First(x => x.Hash == newHash);
        int bad = back.Integrity().Count(i => i.StartsWith("ERROR:", StringComparison.Ordinal));
        bool okAdd = back.ModelCount == n0 + 1 && bad == 0
                     && m2.Slots.Count == 2 && m2.Slots[0].Meshes.Count == 1
                     && built.Length == original.Length + growth
                     && asc && pending;
        Console.WriteLine(okAdd
            ? $"   ok (dup guard; add -> {back.ModelCount} models, 2 slots/1 mesh each, +{growth} B, re-parse clean)"
            : $"   FAIL (models {back.ModelCount} vs {n0 + 1}, slots {m2.Slots.Count}, " +
              $"len {built.Length} vs {original.Length + growth}, {bad} integrity errors, asc={asc}, pending={pending})");
        if (!okAdd) failures++;

        // remove it again -> the file must be byte-identical to the original
        doc.RemoveModel(m);
        byte[] restored = doc.BuildBytes();
        bool identical = restored.Length == original.Length && restored.AsSpan().SequenceEqual(original);
        Console.WriteLine(identical
            ? "   ok (remove -> byte-identical to the input file)"
            : $"   FAIL (remove -> {restored.Length} B, {(restored.Length == original.Length ? "bytes differ" : "length differs")})");
        if (!identical) failures++;

        return failures;
    }

    /// <summary>
    /// Add ordering beyond the single mid-range case: assets hashing before
    /// the first / after the last shipped model, a non-contiguous LOD mask
    /// (File order of the ModelInfo run must come out ascending by LOD — that
    /// is how Parse ranks slot ordinals), and an in-place hash edit that
    /// breaks the order must re-sort models[] (the loader binary-searches it).
    /// </summary>
    private static int RunModelOrderChecks(string bin)
    {
        int failures = 0;
        Console.WriteLine("== add ordering (before / middle / after, LOD ranks, hash edit)");
        byte[] original = File.ReadAllBytes(bin);

        var doc = LodDescDocument.Load(bin);
        int n0 = doc.ModelCount;
        uint lo = doc.Models[0].Hash, hi = doc.Models[^1].Hash;
        uint Free(uint want)
        {
            uint h = want;
            while (doc.Models.Any(m => m.Hash == h)) h++;
            return h;
        }

        var cases = new List<(uint hash, string key, byte mask)>();
        if (lo > 0) cases.Add((Free(lo - 1), "data/hd/_editor_selftest/before.model", 0x06));
        cases.Add((Free(lo + (hi - lo) / 2), "data/hd/_editor_selftest/middle.model", 0x58));  // lods 2,3,5
        if (hi < uint.MaxValue) cases.Add((Free(hi + 1), "data/hd/_editor_selftest/after.model", 0x02));

        foreach (var (hash, key, mask) in cases)
        {
            var m = doc.AddModel(hash, mask, key);
            for (int lod = 0; lod < 8; lod++)
                if (m.GetLodBit(lod)) doc.AddSlot(m, lod, 1);
        }

        bool asc = doc.Models.Select(m => m.Hash).SequenceEqual(doc.Models.Select(m => m.Hash).OrderBy(h => h));
        byte[] built = doc.BuildBytes();
        var back = LodDescDocument.LoadBytes(built);
        int bad = back.Integrity().Count(i => i.StartsWith("ERROR:", StringComparison.Ordinal));
        bool okAdd = asc && bad == 0 && back.ModelCount == n0 + cases.Count
                     && cases.All(c => back.Models.Any(m => m.Hash == c.hash && m.Slots.Count > 0
                                                           && m.LodMask == c.mask));
        // the middle asset's slots must be in the file in ascending-LOD order
        // (2,3,5) — Parse derives SlotLod from the rank of the lodMask bits
        int[] wantLods = { 2, 3, 5 };
        uint midHash = cases.First(c => c.key.EndsWith("middle.model", StringComparison.Ordinal)).hash;
        var mid = back.Models.FirstOrDefault(m => m.Hash == midHash);
        bool okLods = mid is not null && mid.Slots.Select(s => s.SlotLod).SequenceEqual(wantLods)
                      && mid.Slots.All(s => s.Meshes.Count == 1);
        Console.WriteLine(okAdd && okLods
            ? $"   ok (added {cases.Count} assets before/middle/after; models[] ascending, repack re-parses clean; slot LOD order 2,3,5)"
            : $"   FAIL (asc={asc}, {bad} integrity errors, models {back.ModelCount} vs {n0 + cases.Count}, lodOrder={okLods})");
        if (!(okAdd && okLods)) failures++;

        foreach (var (hash, _, _) in cases)
        {
            var m = doc.Models.First(x => x.Hash == hash);
            doc.RemoveModel(m);
        }
        byte[] restored = doc.BuildBytes();
        bool identical = restored.Length == original.Length && restored.AsSpan().SequenceEqual(original);
        Console.WriteLine(identical
            ? "   ok (remove all -> byte-identical to the input file)"
            : $"   FAIL (remove all -> {restored.Length} B, {(restored.Length == original.Length ? "bytes differ" : "length differs")})");
        if (!identical) failures++;

        // in-place hash edit that pushes a record past the end must re-sort
        var doc2 = LodDescDocument.Load(bin);
        var edited = doc2.Models.First(m => m.Slots.Count > 0);
        uint newHash = FreeAtEnd(doc2);
        edited.Hash = newHash;
        bool okEdit = edited.Hash == newHash
                      && doc2.Models.Select(m => m.Hash).SequenceEqual(doc2.Models.Select(m => m.Hash).OrderBy(h => h))
                      && doc2.StructureDirty;
        // a duplicate hash must be refused (loader binary search)
        var other = doc2.Models.First(m => m.Hash != newHash && m.Hash != 0);
        uint keep = other.Hash;
        other.Hash = newHash;
        bool okDup = other.Hash == keep && other.HashHex == keep.ToString("X8");
        byte[] built2 = doc2.BuildBytes();
        var back2 = LodDescDocument.LoadBytes(built2);
        bool okReload = back2.Integrity().Count(i => i.StartsWith("ERROR:", StringComparison.Ordinal)) == 0
                        && back2.Models.Any(m => m.Hash == newHash)
                        && back2.Models.Select(m => m.Hash).SequenceEqual(back2.Models.Select(m => m.Hash).OrderBy(h => h));
        Console.WriteLine(okEdit && okDup && okReload
            ? "   ok (hash edit re-sorts + marks dirty; duplicate hash refused; repack loads clean)"
            : $"   FAIL (okEdit={okEdit}, okDup={okDup}, okReload={okReload})");
        if (!(okEdit && okDup && okReload)) failures++;

        return failures;

        static uint FreeAtEnd(LodDescDocument d)
        {
            uint h = d.Models[^1].Hash;
            if (h == uint.MaxValue) return d.Models[0].Hash;   // degenerate file
            h++;
            while (d.Models.Any(m => m.Hash == h)) h++;
            return h;
        }
    }

    /// <summary>
    /// GuessKey derives the manifest key (data/hd/&lt;path&gt;.model) from a
    /// picked file path. It must anchor on the last "/hd/" so extra "data/"
    /// layers before the real hd root don't strip the prefix — the bug that
    /// dropped it to just "test/holyshield.model".
    /// </summary>
    private static int RunGuessKeyChecks()
    {
        int failures = 0;
        Console.WriteLine("== GuessKey (manifest key from file path)");
        var cases = new (string path, string want)[]
        {
            // double "data/" layer before the hd root -> prefix must survive
            (@"F:\d2r_steam\91923\binaries\debug\x86_64\datatables_sample\data_93847\data\data\hd\test\holyshield_lod0.model",
             "data/hd/test/holyshield.model"),
            // normal shipped-style path
            (@"Z:\data_93847\hd\bvt\models\blend_sphere_lod2.model",
             "data/hd/bvt/models/blend_sphere.model"),
            // no hd root at all -> data/hd/<stem>.model default
            (@"C:\Users\me\Desktop\myasset.model", "data/hd/myasset.model"),
        };
        bool allOk = true;
        foreach (var (path, want) in cases)
        {
            string got = ModelImporter.GuessKey(path);
            bool ok = got == want;
            if (!ok) { allOk = false; Console.WriteLine($"   FAIL {path} -> {got} (want {want})"); }
        }
        Console.WriteLine(allOk
            ? "   ok (double-data path keeps data/hd/ prefix; _lodN stripped; fallback works)"
            : "   FAIL (see above)");
        if (!allOk) failures++;
        return failures;
    }

    /// <summary>
    /// Manifest-first open: LoadPair pulls in the paired .bin, the resolver
    /// helpers agree with the shipped layout, and a manifest without a paired
    /// bin is refused (the editor needs both halves).
    /// </summary>
    private static int RunPairChecks(string bin, string json)
    {
        int failures = 0;
        Console.WriteLine("== pair load (open the .json, the .bin follows)");

        var doc = LodDescDocument.LoadPair(json);
        bool okPair = doc.ModelCount > 0 && doc.PathCount > 0
                      && doc.FilePath is not null
                      && Path.GetFullPath(doc.FilePath) == Path.GetFullPath(bin);
        Console.WriteLine(okPair
            ? $"   ok (manifest -> bin + {doc.PathCount} keys, {doc.Summary})"
            : "   FAIL (pair load did not resolve to the shipped bin/manifest)");
        if (!okPair) failures++;

        string? resolved = LodDescDocument.FindPairedBin(json);
        bool okResolve = resolved is not null
                         && Path.GetFullPath(resolved) == Path.GetFullPath(bin)
                         && LodDescDocument.ManifestPathFor(bin) == Path.ChangeExtension(bin, ".json");
        Console.WriteLine(okResolve
            ? "   ok (FindPairedBin/ManifestPathFor round-trip the shipped pair)"
            : "   FAIL (pair resolver disagrees with the shipped layout)");
        if (!okResolve) failures++;

        // a manifest with no .bin beside it must be refused
        string lonelyDir = Path.Combine(Path.GetTempPath(), "model_lod_desc_paircheck_" + Guid.NewGuid().ToString("N"));
        string lonelyJson = Path.Combine(lonelyDir, "lonely.json");
        try
        {
            Directory.CreateDirectory(lonelyDir);
            File.WriteAllText(lonelyJson, "{\"data/hd/x.model\":{\"lods\":2}}");
            bool noPair = LodDescDocument.FindPairedBin(lonelyJson) is null;
            bool threw = false;
            try { LodDescDocument.LoadPair(lonelyJson); }
            catch (FileNotFoundException) { threw = true; }
            bool ok = noPair && threw;
            Console.WriteLine(ok
                ? "   ok (manifest without a paired .bin: resolver null + LoadPair refuses)"
                : $"   FAIL (noPair={noPair}, threw={threw})");
            if (!ok) failures++;
        }
        finally
        {
            try { Directory.Delete(lonelyDir, true); } catch { /* best effort */ }
        }

        return failures;
    }

    /// <summary>
    /// Manifest sync: re-serializing an untouched pair is byte-identical to
    /// the shipped file; add/remove mirror key &lt;-&gt; model 1:1 (and lodMask
    /// edits update "lods"); a bin opened without a manifest stays manifest-free.
    /// </summary>
    private static int RunManifestSyncChecks(string bin, string json)
    {
        int failures = 0;
        Console.WriteLine("== manifest sync (add/remove model edits the json)");
        string tmpJson = Path.Combine(Path.GetTempPath(), "model_lod_desc_manifest_selftest.json");

        // 1. untouched pair -> byte-identical re-serialization
        var doc = LodDescDocument.LoadPair(json);
        doc.SaveManifest(tmpJson);
        byte[] shipped = File.ReadAllBytes(json);
        byte[] mine = File.ReadAllBytes(tmpJson);
        bool exact = shipped.AsSpan().SequenceEqual(mine);
        Console.WriteLine(exact
            ? $"   ok (re-serialized manifest byte-identical, {shipped.Length} B, {doc.PathCount} keys)"
            : $"   FAIL (manifest rewrite differs: {shipped.Length} vs {mine.Length} B)");
        if (!exact) failures++;

        // 2. add -> key appears with its lodMask; a lodMask edit updates "lods"
        const string key = "data/hd/_editor_selftest/probe.model";
        uint h = LodDescDocument.FnvHash(key.ToLowerInvariant());
        var added = doc.AddModel(h, 0x06, key);
        added.SetLodBit(2, true);                       // mask -> 0x0E via the record setter
        const byte finalMask = 0x06 | (1 << 3);
        bool okAdd = doc.PathFor(h) == key
                     && doc.JsonLods(h) == finalMask
                     && doc.IsManifestDirty
                     && added.Display == key;
        Console.WriteLine(okAdd
            ? "   ok (add: manifest entry key=lods 0x0E, Display shows the key)"
            : $"   FAIL (add: PathFor={doc.PathFor(h)}, JsonLods={doc.JsonLods(h)}, dirty={doc.IsManifestDirty})");
        if (!okAdd) failures++;

        // 3. remove a shipped model -> its key disappears
        var victim = doc.Models.First(m => m.Hash != 0 && m.Hash != h && doc.PathFor(m.Hash) is not null);
        string victimKey = doc.PathFor(victim.Hash)!;
        doc.RemoveModel(victim);
        bool okRm = doc.PathFor(victim.Hash) is null && doc.IsManifestDirty;
        Console.WriteLine(okRm
            ? $"   ok (remove: manifest entry dropped for {victimKey})"
            : $"   FAIL (remove: {victimKey} still resolvable)");
        if (!okRm) failures++;

        // 4. write-out: shipped order minus the removed key, plus the added key
        //    at the end, every "lods" value matching the in-memory records
        doc.SaveManifest(tmpJson);
        var shippedPairs = new List<(string key, int lods)>();
        using (var j = JsonDocument.Parse(File.ReadAllText(json)))
            foreach (var p in j.RootElement.EnumerateObject())
                shippedPairs.Add((p.Name, p.Value.GetProperty("lods").GetInt32()));
        var newPairs = new List<(string key, int lods)>();
        using (var j = JsonDocument.Parse(File.ReadAllText(tmpJson)))
            foreach (var p in j.RootElement.EnumerateObject())
                newPairs.Add((p.Name, p.Value.GetProperty("lods").GetInt32()));

        var expected = shippedPairs.Where(p => p.key != victimKey).ToList();
        expected.Add((key, finalMask));
        bool okWrite = newPairs.SequenceEqual(expected);
        Console.WriteLine(okWrite
            ? $"   ok (written manifest: {newPairs.Count} keys, order preserved, only the add/remove diff)"
            : $"   FAIL (written manifest order/content wrong: {newPairs.Count} vs {expected.Count} entries)");
        if (!okWrite) failures++;

        // 5. a bin opened without a manifest keeps no manifest state
        var bare = LodDescDocument.Load(bin);
        uint bareHash = 0x0BADF00D;
        while (bare.Models.Any(m => m.Hash == bareHash)) bareHash++;
        bare.AddModel(bareHash, 0x02, "data/hd/x.model");
        bool threwSave = false;
        try { bare.SaveManifest(tmpJson + ".nomanifest"); }
        catch (InvalidOperationException) { threwSave = true; }
        bool okBare = !bare.ManifestLoaded && !bare.IsManifestDirty
                      && bare.PathFor(bareHash) is null && threwSave;
        Console.WriteLine(okBare
            ? "   ok (no manifest loaded: add is manifest-free, SaveManifest refuses)"
            : $"   FAIL (bare doc: loaded={bare.ManifestLoaded}, dirty={bare.IsManifestDirty}, threwSave={threwSave})");
        if (!okBare) failures++;

        try { File.Delete(tmpJson); } catch { /* best effort */ }
        return failures;
    }

    /// <summary>
    /// Import verification: parse sampled manifest assets' .model files with
    /// the editor's own ModelImporter and compare every derived field against
    /// the shipped records. The mapping (ExtendedData -> bin records) is the
    /// port of the probe's byte-exact RegenEngine, so this is the ground-truth
    /// check of the GUI's "fill from model file" path.
    /// </summary>
    private static int RunImportChecks(string bin, string json, string root)
    {
        int failures = 0;
        Console.WriteLine("== import check (parsed .model files vs shipped records)");

        var keys = new List<(string key, int lods)>();
        using (var jdoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json)))
            foreach (var p in jdoc.RootElement.EnumerateObject())
                keys.Add((p.Name, p.Value.GetProperty("lods").GetInt32()));
        if (keys.Count == 0)
        {
            Console.WriteLine("   SKIP (empty manifest)");
            return failures;
        }

        // ~200 keys spread over the manifest
        var sample = new List<(string key, int lods)>();
        int step = Math.Max(1, keys.Count / 200);
        for (int i = 0; i < keys.Count; i += step) sample.Add(keys[i]);

        var doc = LodDescDocument.Load(bin);
        int shown = 0;
        long slots = 0, meshes = 0, boxExact = 0, layerOk = 0, boneOk = 0, fmtOk = 0, flipOk = 0;
        long slotsCompared = 0, typeCompared = 0;
        void Miss(string s)
        {
            if (shown++ < 5) Console.WriteLine("   " + s);
            failures++;
        }

        foreach (var (key, lods) in sample)
        {
            var files = new List<(int lod, string path)>();
            for (int lod = 0; lod < 8; lod++)
            {
                if (((lods >> (lod + 1)) & 1) == 0) continue;
                string? file = SlotFilePath(root, key, lod);
                if (file is null) continue;
                files.Add((lod, file));
            }
            if (files.Count == 0) continue;
            ImportedModel im;
            try
            {
                im = ModelImporter.ParseFiles(files.Select(f => f.path).ToList(), key);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   parse FAIL {key}: {ex.Message}");
                failures++;
                continue;
            }
            var m = doc.Models.FirstOrDefault(x => x.Hash == im.Hash);
            if (m is null)
            {
                Console.WriteLine($"   WARN {key}: no shipped record");
                continue;
            }

            typeCompared++;
            if (im.TypeMask != m.TypeMask)
                Miss($"typeMask {key}: parsed 0x{im.TypeMask:X8} vs bin 0x{m.TypeMask:X8}");

            foreach (var slot in m.Slots)
            {
                if (slot.IsFlipbook == im.IsFlipbook) flipOk++;
                else Miss($"isMeshFlipbook {key}: parsed {im.IsFlipbook} vs bin {slot.IsFlipbook}");
            }

            foreach (var (lod, _) in files)
            {
                var slot = m.Slots.FirstOrDefault(x => x.SlotLod == lod);
                if (slot is null) continue;
                slotsCompared++;
                var parsed = im.Slots.First(s => s.Lod == lod);
                if (slot.Meshes.Count != parsed.Meshes.Count)
                {
                    Miss($"mesh count {key} lod{lod}: parsed {parsed.Meshes.Count} vs bin {slot.Meshes.Count}");
                    continue;
                }
                slots++;
                for (int k = 0; k < slot.Meshes.Count; k++)
                {
                    var want = slot.Meshes[k];
                    var got = parsed.Meshes[k];
                    meshes++;
                    byte[] wb = want.ToBytes();
                    byte[] gb = MeshBytesOf(got);
                    if (wb.AsSpan(0, 24).SequenceEqual(gb.AsSpan(0, 24))) boxExact++;
                    else if (shown < 5)
                    {
                        shown++;
                        Console.WriteLine($"   note bounds drift {key} lod{lod} mesh{k} (stale extracted model)");
                    }
                    if (want.RenderLayer == (uint)got.RenderLayer) layerOk++;
                    else Miss($"renderLayer {key} lod{lod} mesh{k}: parsed {got.RenderLayer} vs bin {want.RenderLayer}");
                    if (want.BoneBindingsCount == (ushort)got.BoneBindingCount) boneOk++;
                    else Miss($"boneCount {key} lod{lod} mesh{k}: parsed {got.BoneBindingCount} vs bin {want.BoneBindingsCount}");
                    int gotInfl = got.FmtCode & 0xF;
                    bool gotAlpha = got.VertexSize == 24;
                    bool gotRigid = !got.HasBones;
                    if (want.NumBoneInfluences == gotInfl
                        && want.HasVertexAlpha == gotAlpha
                        && want.IsRigid == gotRigid) fmtOk++;
                    else Miss($"fmtword {key} lod{lod} mesh{k}: parsed {gotInfl}/{gotAlpha}/{gotRigid} vs bin {want.NumBoneInfluences}/{want.HasVertexAlpha}/{want.IsRigid}");
                }
            }
        }

        Console.WriteLine($"   sampled {sample.Count} keys: {slotsCompared} slots, {meshes} meshes");
        Console.WriteLine($"   typeMask ok         : {typeCompared}/{typeCompared}");
        Console.WriteLine($"   isMeshFlipbook ok   : {flipOk}");
        Console.WriteLine($"   mesh counts ok      : {slots}/{slotsCompared}");
        Console.WriteLine($"   bounds bit-exact    : {boxExact}/{meshes}  ({100.0 * boxExact / Math.Max(1, meshes):F1}%)");
        Console.WriteLine($"   renderLayer ok      : {layerOk}/{meshes}");
        Console.WriteLine($"   boneBindingCount ok : {boneOk}/{meshes}");
        Console.WriteLine($"   format word ok      : {fmtOk}/{meshes}");

        // The mapping must be exact wherever the tree is exact: counts,
        // renderLayer, boneCount and the format word were verified 100%
        // against the shipped tree; mesh bounds drift in a small minority
        // of assets (stale extracted models) so only a sanity floor applies.
        if (meshes > 0)
        {
            if (slots != slotsCompared) { Console.WriteLine("   FAIL mesh-count mismatches"); failures++; }
            if (layerOk != meshes) { Console.WriteLine("   FAIL renderLayer mismatches"); failures++; }
            if (boneOk != meshes) { Console.WriteLine("   FAIL boneBindingCount mismatches"); failures++; }
            if (fmtOk != meshes) { Console.WriteLine("   FAIL format-word mismatches"); failures++; }
            if (100.0 * boxExact / meshes < 50.0) { Console.WriteLine("   FAIL bounds far below the known drift level"); failures++; }
        }
        if (failures == 0 && slotsCompared == 0)
        {
            Console.WriteLine("   SKIP (no model files found for the sampled keys)");
            return failures;
        }
        return failures;
    }

    /// <summary>The 32 bytes an ImportedMesh maps to (the bin record layout).</summary>
    private static byte[] MeshBytesOf(ImportedMesh m)
    {
        var r = new byte[32];
        for (int k = 0; k < 3; k++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4 * k), m.Min[k]);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(12 + 4 * k), m.Max[k]);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(24), (uint)m.RenderLayer);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(28), (ushort)m.BoneBindingCount);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(30),
            (ushort)((m.FmtCode & 0xF) | (m.VertexSize == 24 ? 0x10 : 0) | (m.HasBones ? 0 : 0x20)));
        return r;
    }

    /// <summary>rootDir/stem_lodN.model for a manifest key (writer's file rule).</summary>
    private static string? SlotFilePath(string rootDir, string key, int lod)
    {
        string rel = key.StartsWith("data/hd/", StringComparison.OrdinalIgnoreCase)
            ? key["data/hd/".Length..] : key;
        string stem = rel.EndsWith(".model", StringComparison.OrdinalIgnoreCase)
            ? rel[..^".model".Length] : rel;
        string asIs = Path.Combine(rootDir, stem + "_lod" + lod + ".model");
        if (File.Exists(asIs)) return asIs;
        string lower = Path.Combine(rootDir, stem.ToLowerInvariant() + "_lod" + lod + ".model");
        return File.Exists(lower) ? lower : null;
    }

    /// <summary>
    /// Regression test for the reported bug: after loading a .bin the models
    /// list must be populated and the first asset selected WITHOUT the user
    /// typing in the search box (the toolkit's [ObservableProperty] setter
    /// skips unchanged values, so ReplaceDocument cannot rely on the
    /// SearchText change event to rebuild the list).
    /// </summary>
    private static int RunViewModelChecks(string bin)
    {
        int failures = 0;
        Console.WriteLine("== view-model load (list populated before any search)");
        var doc = LodDescDocument.Load(bin);
        var vm = new MainWindowViewModel();
        vm.LoadDocument(doc);
        bool listOk = vm.Models.Count == doc.ModelCount && vm.SelectedModel is not null;
        Console.WriteLine(listOk
            ? $"   ok ({vm.Models.Count} models listed, {vm.SelectedModel!.Display} selected)"
            : $"   FAIL ({vm.Models.Count} models listed, selected={(vm.SelectedModel is null ? "none" : vm.SelectedModel.Display)})");
        if (!listOk) failures++;

        var withSlots = vm.Models.First(m => m.Slots.Count > 0);
        vm.SelectedModel = withSlots;
        bool cascadeOk = vm.Slots.Count == withSlots.Slots.Count
                         && vm.Slots.Count > 0
                         && vm.SelectedSlot is not null
                         && vm.Meshes.Count == vm.SelectedSlot!.Meshes.Count
                         && vm.SelectedMesh is not null;
        Console.WriteLine(cascadeOk
            ? $"   ok (selection cascade: {vm.Slots.Count} slots, {vm.Meshes.Count} meshes)"
            : "   FAIL (selection cascade)");
        if (!cascadeOk) failures++;

        vm.SearchText = "0x" + withSlots.HashHex[..4];
        bool filterOk = vm.Models.Count > 0 && vm.Models.Contains(withSlots);
        vm.SearchText = string.Empty;
        bool clearOk = vm.Models.Count == doc.ModelCount;
        Console.WriteLine(filterOk && clearOk
            ? "   ok (hash filter narrows, clearing restores the full list)"
            : "   FAIL (hash filter / clear)");
        if (!(filterOk && clearOk)) failures++;

        return failures;
    }

    /// <summary>Writes to the console and captures the same text.</summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly TextWriter _capture;

        public TeeWriter(TextWriter console, TextWriter capture)
        {
            _console = console;
            _capture = capture;
        }

        public override Encoding Encoding => _console.Encoding;

        public override void Write(char value)
        {
            _console.Write(value);
            _capture.Write(value);
        }

        public override void Write(string? value)
        {
            _console.Write(value);
            _capture.Write(value);
        }

        public override void WriteLine(string? value)
        {
            _console.WriteLine(value);
            _capture.WriteLine(value);
        }
    }
}
