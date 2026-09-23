using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WDM.Services.Chunking;

/// <summary>
/// Fixed-resolution durable completion map. A bit means "this whole block is on
/// disk and must never be re-fetched unless explicitly invalidated". Runtime range
/// geometry is independent: leases may span arbitrary byte intervals.
/// <para/>
/// Durability rule: bits flip only for fully-covered blocks whose bytes were all
/// successfully written. In-memory <c>runs</c> track partial-block progress and are
/// deliberately NOT persisted — a crash re-downloads partial blocks (safe, never corrupt).
/// </summary>
public sealed class CompletionMap
{
    public const string MagicV2 = "WDMSTATE2";
    public const string MagicV1 = "WDMSTATE1";
    public const int DefaultBlockSize = 1024 * 1024; // 1 MiB

    private readonly object _lock = new();
    private byte[] _bits;
    private readonly SortedDictionary<long, long> _runs = new(); // start -> endExclusive (memory-only)
    private long _lastSaveTick;

    public long TotalBytes { get; }
    public int BlockSize { get; }
    public int BlockCount { get; }
    public string? Etag { get; private set; }
    public string? LastModified { get; private set; }

    public CompletionMap(long totalBytes, int blockSize = DefaultBlockSize)
    {
        if (totalBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalBytes));
        TotalBytes = totalBytes;
        BlockSize = Math.Max(64 * 1024, blockSize);
        BlockCount = (int)((totalBytes + BlockSize - 1) / BlockSize);
        _bits = new byte[(BlockCount + 7) / 8];
    }

    public void SetIdentity(string? etag, string? lastModified)
    {
        lock (_lock)
        {
            Etag = etag;
            LastModified = lastModified;
        }
    }

    private bool IsBitSet(int block)
    {
        return (_bits[block >> 3] & (1 << (block & 7))) != 0;
    }

    public bool IsBlockComplete(int block)
    {
        lock (_lock) return IsBitSet(block);
    }

    public int CompletedBlockCount
    {
        get
        {
            lock (_lock)
            {
                int n = 0;
                for (int i = 0; i < BlockCount; i++)
                    if (IsBitSet(i)) n++;
                return n;
            }
        }
    }

    /// <summary>Durably completed bytes (whole blocks only; partial tail counted exactly).</summary>
    public long CompletedBytes
    {
        get
        {
            lock (_lock)
            {
                long full = 0;
                for (int i = 0; i < BlockCount - 1; i++)
                    if (IsBitSet(i)) full++;
                long bytes = full * (long)BlockSize;
                if (BlockCount > 0 && IsBitSet(BlockCount - 1))
                    bytes += TotalBytes - (long)(BlockCount - 1) * BlockSize;
                return bytes;
            }
        }
    }

    public bool IsComplete
    {
        get
        {
            lock (_lock)
            {
                for (int i = 0; i < BlockCount; i++)
                    if (!IsBitSet(i)) return false;
                return true;
            }
        }
    }

    /// <summary>
    /// Records bytes [from, toExclusive) as written to disk. Promotes fully-covered
    /// blocks to durable bits. Call only after successful file writes.
    /// Returns newly completed bytes attributable to whole blocks.
    /// </summary>
    public void MarkCommitted(long from, long toExclusive)
    {
        if (toExclusive <= from)
            return;
        lock (_lock)
        {
            MergeRun(from, toExclusive);
            int first = (int)(from / BlockSize);
            int last = (int)((toExclusive - 1) / BlockSize);
            for (int b = first; b <= last; b++)
            {
                if (IsBitSet(b))
                    continue;
                long bs = (long)b * BlockSize;
                long be = Math.Min(bs + BlockSize, TotalBytes);
                if (IsCovered(bs, be))
                    _bits[b >> 3] |= (byte)(1 << (b & 7));
            }
        }
    }

    private void MergeRun(long from, long to)
    {
        long ns = from, ne = to;
        var drop = new List<long>();
        foreach (var kv in _runs)
        {
            if (kv.Value < ns || kv.Key > ne)
                continue;
            ns = Math.Min(ns, kv.Key);
            ne = Math.Max(ne, kv.Value);
            drop.Add(kv.Key);
        }
        foreach (long k in drop)
            _runs.Remove(k);
        _runs[ns] = ne;
    }

    private bool IsCovered(long from, long to)
    {
        foreach (var kv in _runs)
        {
            if (kv.Key > from)
                break;
            if (kv.Value >= to)
                return true;
        }
        return false;
    }

    /// <summary>Unfinished byte space: total minus durable bits minus in-memory runs.</summary>
    public List<(long Start, long EndExclusive)> GetPendingRanges()
    {
        lock (_lock)
        {
            var gaps = new List<(long, long)>();
            long cursor = 0;
            // Walk blocks; skip completed ones, then carve out runs.
            while (cursor < TotalBytes)
            {
                int b = (int)(cursor / BlockSize);
                long be = Math.Min((long)(b + 1) * BlockSize, TotalBytes);
                if (IsBitSet(b))
                {
                    cursor = be;
                    continue;
                }
                long segEnd = be;
                foreach (var kv in _runs)
                {
                    if (kv.Key >= segEnd)
                        break;
                    if (kv.Value <= cursor)
                        continue;
                    if (kv.Key > cursor)
                    {
                        gaps.Add((cursor, Math.Min(kv.Key, segEnd)));
                        cursor = kv.Value;
                    }
                    else
                    {
                        cursor = Math.Max(cursor, kv.Value);
                    }
                    if (cursor >= segEnd)
                        break;
                }
                if (cursor < segEnd)
                {
                    gaps.Add((cursor, segEnd));
                    cursor = segEnd;
                }
            }
            return gaps;
        }
    }

    // ---------- persistence ----------

    public void Save(string path)
    {
        StateRecord rec;
        lock (_lock)
        {
            rec = new StateRecord
            {
                Magic = MagicV2,
                Version = 2,
                TotalBytes = TotalBytes,
                BlockSize = BlockSize,
                BlockCount = BlockCount,
                Bits = Convert.ToBase64String(_bits),
                Etag = Etag,
                LastModified = LastModified,
            };
        }
        AtomicFile.Write(path, JsonSerializer.Serialize(rec));
    }

    public void SaveIfDirty(string path, int throttleMs = 1000)
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastSaveTick);
        if (last != 0 && now - last < throttleMs)
            return;
        Save(path);
        Interlocked.Exchange(ref _lastSaveTick, Environment.TickCount64);
    }

    public static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// Loads WDMSTATE2, else migrates WDMSTATE1 chunk bitmap / tasks.json segment
    /// snapshot into fresh V2 geometry, else starts fresh. Never interprets old
    /// state with an incompatible geometry. Identity mismatch discards to fresh
    /// (the engine validates identity first and throws when a resume is unsafe;
    /// the map itself fails safe to re-download).
    /// </summary>
    public static CompletionMap LoadOrMigrate(
        string path, long totalBytes, int blockSize,
        string? etag, string? lastModified,
        List<SegmentRecord>? legacySegments = null)
    {
        var map = new CompletionMap(totalBytes, blockSize);
        string? text = null;
        try { if (File.Exists(path)) text = File.ReadAllText(path); } catch { }
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                string? magic = doc.RootElement.TryGetProperty("Magic", out var m) ? m.GetString() : null;
                if (string.Equals(magic, MagicV2, StringComparison.Ordinal))
                {
                    var rec = JsonSerializer.Deserialize<StateRecord>(text);
                    if (rec is not null && rec.TotalBytes == totalBytes &&
                        rec.BlockSize == map.BlockSize && rec.BlockCount == map.BlockCount &&
                        !string.IsNullOrEmpty(rec.Bits))
                    {
                        byte[] bits = Convert.FromBase64String(rec.Bits);
                        if (bits.Length == map._bits.Length)
                        {
                            // Identity guard: a sidecar from a different object is
                            // worthless — drop it rather than skipping live bytes.
                            if (IdentityConflicts(rec.Etag, rec.LastModified, etag, lastModified))
                            {
                                map.SetIdentity(etag, lastModified);
                                map.Save(path);
                                return map;
                            }
                            lock (map._lock) { map._bits = bits; }
                            map.SetIdentity(etag ?? rec.Etag, lastModified ?? rec.LastModified);
                            return map;
                        }
                    }
                    // Present but incompatible: fall through to V1/snapshot/fresh.
                }
                else if (string.Equals(magic, MagicV1, StringComparison.Ordinal))
                {
                    if (TryMigrateV1(text, map))
                    {
                        map.SetIdentity(etag, lastModified);
                        map.Save(path);
                        return map;
                    }
                }
            }
            catch
            {
                // Corrupt sidecar: fresh (safe redownload), snapshot import below still applies.
            }
        }
        if (legacySegments is { Count: > 0 })
            map.ImportSegments(legacySegments);
        map.SetIdentity(etag, lastModified);
        map.Save(path);
        return map;
    }

    private static bool IdentityConflicts(string? storedEtag, string? storedMod, string? curEtag, string? curMod)
    {
        if (!string.IsNullOrWhiteSpace(storedEtag) && !string.IsNullOrWhiteSpace(curEtag) &&
            !string.Equals(storedEtag, curEtag, StringComparison.Ordinal))
            return true;
        bool etagAgrees = !string.IsNullOrWhiteSpace(storedEtag) && !string.IsNullOrWhiteSpace(curEtag) &&
            string.Equals(storedEtag, curEtag, StringComparison.Ordinal);
        if (!etagAgrees && !string.IsNullOrWhiteSpace(storedMod) && !string.IsNullOrWhiteSpace(curMod) &&
            !string.Equals(storedMod, curMod, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>Translates a WDMSTATE1 chunk bitmap into block commits.
    /// Only fully-covered blocks become durable; partial coverage is re-fetched.</summary>
    internal static bool TryMigrateV1(string text, CompletionMap map)
    {
        try
        {
            var rec = JsonSerializer.Deserialize<V1Record>(text);
            if (rec is null || rec.TotalBytes != map.TotalBytes)
                return false;
            if (rec.ChunkSize <= 0 || rec.ChunkCount <= 0 || string.IsNullOrEmpty(rec.Bits))
                return false;
            byte[] bits = Convert.FromBase64String(rec.Bits);
            if (bits.Length != (rec.ChunkCount + 7) / 8)
                return false;
            for (int i = 0; i < rec.ChunkCount; i++)
            {
                if ((bits[i >> 3] & (1 << (i & 7))) == 0)
                    continue;
                long from = (long)i * rec.ChunkSize;
                long to = Math.Min(from + rec.ChunkSize, rec.TotalBytes);
                if (from < to)
                    map.MarkCommitted(from, to);
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Imports tasks.json segment records by absolute byte range
    /// (geometry-independent: works for both old chunk and new block records).</summary>
    public void ImportSegments(List<SegmentRecord> records)
    {
        lock (_lock)
        {
            foreach (var r in records)
            {
                if (r is null || !r.Done)
                    continue;
                long from = Math.Max(0, r.Start);
                long to = r.End >= r.Start ? r.End + 1 : r.Start; // End is inclusive
                to = Math.Min(to, TotalBytes);
                if (to > from)
                {
                    MergeRun(from, to);
                    int first = (int)(from / BlockSize);
                    int last = (int)((to - 1) / BlockSize);
                    for (int b = first; b <= last; b++)
                    {
                        if (IsBitSet(b)) continue;
                        long bs = (long)b * BlockSize;
                        long be = Math.Min(bs + BlockSize, TotalBytes);
                        if (IsCovered(bs, be))
                            _bits[b >> 3] |= (byte)(1 << (b & 7));
                    }
                }
            }
        }
    }

    // ---------- UI / persistence helpers ----------

    /// <summary>Per-bucket completion fractions (0/100 per block, aggregated when huge).</summary>
    public double[] ProgressFractions(int maxBuckets = 512)
    {
        lock (_lock)
        {
            if (BlockCount <= maxBuckets)
            {
                var r = new double[BlockCount];
                for (int i = 0; i < BlockCount; i++)
                    r[i] = IsBitSet(i) ? 100.0 : 0.0;
                return r;
            }
            var agg = new double[maxBuckets];
            for (int i = 0; i < maxBuckets; i++)
            {
                int from = (int)((long)i * BlockCount / maxBuckets);
                int to = (int)((long)(i + 1) * BlockCount / maxBuckets);
                int done = 0;
                for (int b = from; b < to; b++)
                    if (IsBitSet(b)) done++;
                agg[i] = to > from ? done * 100.0 / (to - from) : 100.0;
            }
            return agg;
        }
    }

    /// <summary>Block records for tasks.json (sidecar stays authoritative for huge files).</summary>
    public List<SegmentRecord>? SnapshotSegments(int cap = 4096)
    {
        lock (_lock)
        {
            if (BlockCount > cap)
                return null;
            var list = new List<SegmentRecord>(BlockCount);
            for (int i = 0; i < BlockCount; i++)
            {
                long start = (long)i * BlockSize;
                list.Add(new SegmentRecord
                {
                    Index = i,
                    Start = start,
                    End = Math.Min(start + BlockSize, TotalBytes) - 1,
                    Done = IsBitSet(i),
                });
            }
            return list;
        }
    }

    private sealed class StateRecord
    {
        public string Magic { get; set; } = "";
        public int Version { get; set; }
        public long TotalBytes { get; set; }
        public int BlockSize { get; set; }
        public int BlockCount { get; set; }
        public string Bits { get; set; } = "";
        public string? Etag { get; set; }
        public string? LastModified { get; set; }
    }

    private sealed class V1Record
    {
        public string Magic { get; set; } = "";
        public long TotalBytes { get; set; }
        public long ChunkSize { get; set; }
        public int ChunkCount { get; set; }
        public string Bits { get; set; } = "";
    }
}
