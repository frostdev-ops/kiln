// SPDX-License-Identifier: MIT
// The game's own item icons in Kiln windows (Ui.ItemImage, UiDraw.ItemIcon; since 0.2.0-dev.29.15). Guide: docs/kiln/ui.md
// "Item icons"; the game's data: docs/re/resources.md section 4.
//
// Pipeline: an item id -> the UI texture the game itself draws for it (ItemIconKey: a game item's own GUID; a new item of
// kiln.content its own GUID once kiln.assets serves its PNG icon, else its base item's, as kiln.content's UI texture hook
// aliases it) -> kiln.assets' texture_rgba (that keen::UiTextureResource's pixels, one mip level as RGBA8: the game's icons
// are BC7, 256x256 with 9 levels) -> kiln.ui's image_load_rgba (an overlay texture, uploaded at the next frame). Nothing is
// written anywhere: the pixels live in this process only.
//
// Threads: a draw asks on the render thread and never waits: an icon not loaded yet shows the placeholder, and one worker
// thread reads and decodes it (most take well under a millisecond). An icon scrolled out of its window is not asked for.
// Memory: at most 256 icons and 24 MiB of pixels (kiln.ui lets 960 overlay images exist: native/services/ui/image_budget.h;
// the rest are mods' own pictures); past that the icons not drawn for a second go first, and while every icon is drawn now a
// new one waits without being read. An icon is drawn once kiln.ui uploaded it (image_state ready); one the overlay could not
// give a descriptor set is freed and read again later. Whatever fails is asked for again later, less often each time. A new
// client world (another session) drops them all.
using System.Collections.Concurrent;

namespace Kiln;

/// <summary>Which UI texture the game draws for an item: pure (host-tested).</summary>
internal static class ItemIconKey
{
    /// <summary>KILN_ICON_SERVED (kiln_assets.h icon_state).</summary>
    public const int Served = 2;

    /// <summary>The texture GUID for <paramref name="item"/>, or null when it is not known (yet). <paramref name="final"/>:
    /// the answer cannot change by itself (a game item; a new item whose own icon is served, or that has no icon file).</summary>
    public static Guid? Of(ItemId item, Func<uint, ContentNames.Def?> defined, Func<Guid, int> iconState, Func<ItemId, Guid?> gameItem,
        out bool final)
    {
        final = false;
        if (item.Value == 0) return null;
        if (gameItem(item) is { } g)
        {
            final = true;
            return g;  // the ids of the game's items and of new items never meet (FNV-1a of different GUIDs)
        }
        if (defined(item.Value) is { } d)
        {
            if (iconState(d.Guid) == Served)
            {
                final = true;
                return d.Guid;  // kiln.assets serves the mod's PNG icon under the item's own GUID
            }
            if (d.IconGuid is { } alias)
            {
                final = !d.HasIconFile;  // a PNG icon still pending can be served later
                return alias;            // the base item's texture (or "icon"'s), as the game shows it meanwhile
            }
            return null;                 // not in a world yet: no clone, no alias
        }
        return null;
    }

    /// <summary>The texture size an icon drawn <paramref name="pixels"/> wide is decoded at: the smallest of 32, 64, 128
    /// and 256 that is at least as large (the game's icon mips are powers of two from 256).</summary>
    public static int Bucket(float pixels) => pixels <= 32 ? 32 : pixels <= 64 ? 64 : pixels <= 128 ? 128 : 256;
}

/// <summary>The bookkeeping of the icon cache: entries, their last use, which to drop to stay within the limits (pure).
/// The limits count textures (entries holding pixels) and their bytes; entries without one (loading, missing) are a few bytes
/// each and are bounded at four times the texture limit.</summary>
internal sealed class IconTable<TKey> where TKey : notnull
{
    public sealed class Entry
    {
        public ulong Handle;       // 0 while loading or when there is no icon
        public int Width, Height;
        public long Bytes;         // > 0: holds a texture
        public long LastUsed;      // Environment.TickCount64 (or a test's clock)
        public long RetryAt;       // a missing icon is looked for again from then on; 0: never
        public bool Loading;
        public bool Ready;         // kiln.ui uploaded Handle with its descriptor set (image_state): drawn
        public Guid Texture;       // the texture Handle shows
        public int Attempts;       // failures in a row (no texture, a failed upload): the wait before the next grows
        public int Waits;          // loads in a row that found the cache full of icons drawn now
    }

    private readonly Dictionary<TKey, Entry> _map = new();
    public int MaxEntries { get; }
    public long MaxBytes { get; }
    public long Bytes { get; private set; }
    public int Textures { get; private set; }
    public int Count => _map.Count;

    public IconTable(int maxEntries, long maxBytes)
    {
        MaxEntries = maxEntries;
        MaxBytes = maxBytes;
    }

    public bool TryGet(TKey key, out Entry entry) => _map.TryGetValue(key, out entry!);

    public Entry Add(TKey key, long now)
    {
        var e = new Entry { Loading = true, LastUsed = now };
        _map[key] = e;
        return e;
    }

    public void SetBytes(Entry e, long bytes)
    {
        Textures += (bytes > 0 ? 1 : 0) - (e.Bytes > 0 ? 1 : 0);
        Bytes += bytes - e.Bytes;
        e.Bytes = bytes;
    }

    /// <summary>Whether one more texture of <paramref name="bytes"/> fits within the limits.</summary>
    public bool Room(long bytes) => Textures < MaxEntries && Bytes + bytes <= MaxBytes;

    /// <summary>Over a limit: <see cref="Trim"/> has work.</summary>
    public bool Over => Textures > MaxEntries || Bytes > MaxBytes || _map.Count > 4 * MaxEntries;

    /// <summary>Takes out entries that were not used since <paramref name="usedBefore"/> (oldest first; never one that is
    /// loading) until the limits hold again. Returns what was taken out (their handles are freed by the caller), null when
    /// nothing was: within the limits it looks at nothing and makes nothing.</summary>
    public List<Entry>? Trim(long usedBefore)
    {
        if (!Over) return null;
        List<Entry>? gone = null;
        foreach (var (k, e) in _map.Where(p => !p.Value.Loading && p.Value.LastUsed < usedBefore).OrderBy(p => p.Value.LastUsed).ToList())
        {
            if (!Over) break;
            _map.Remove(k);
            SetBytes(e, 0);
            (gone ??= []).Add(e);
        }
        return gone;
    }

    /// <summary>Makes room for one more texture of <paramref name="bytes"/>: takes out textures not used since
    /// <paramref name="usedBefore"/>, oldest first, until it fits. Returns what was taken out (empty when nothing could go: every
    /// texture is drawn now; the load then waits).</summary>
    public List<Entry> MakeRoom(long bytes, long usedBefore)
    {
        var gone = new List<Entry>();
        if (Room(bytes)) return gone;
        foreach (var (k, e) in _map.Where(p => !p.Value.Loading && p.Value.Bytes > 0 && p.Value.LastUsed < usedBefore).OrderBy(p => p.Value.LastUsed).ToList())
        {
            if (Room(bytes)) break;
            _map.Remove(k);
            SetBytes(e, 0);
            gone.Add(e);
        }
        return gone;
    }

    /// <summary>Takes every entry out (a world change); returns them for their handles.</summary>
    public List<Entry> Clear()
    {
        var all = _map.Values.ToList();
        _map.Clear();
        Bytes = 0;
        Textures = 0;
        return all;
    }
}

/// <summary>The game's item icons as overlay images: the cache, its worker and the placeholder.</summary>
internal static unsafe class ItemIcons
{
    internal const int MaxEntries = 256;  // at most half of kiln.ui's 960 images (image_budget.h), the placeholder included
    internal const long MaxBytes = 24L << 20;
    private const long KeepMs = 1000;
    private const long RetryMs = 5000, RetryMostMs = 120_000;            // something that may change soon: 5 s, doubling to 2 min
    private const long RetryFinalMs = 60_000, RetryFinalMostMs = 600_000; // no texture to read: 1 min, doubling to 10 min
    private const long WaitMs = 250, WaitMostMs = 2000;                  // the cache is full of icons drawn now
    private const long TrimEveryMs = 16;                                 // the over-limit scan: about once a frame

    private static readonly object Gate = new();
    private static readonly IconTable<(uint Item, int Bucket)> Table = new(MaxEntries, MaxBytes);
    private static readonly ConcurrentQueue<(uint Item, int Bucket)> Queue = new();
    private static readonly SemaphoreSlim Wake = new(0);
    private static readonly ConcurrentDictionary<uint, UiImage> Views = new();
    private static Thread? _worker;
    private static ulong _placeholder;
    private static bool _placeholderTried;
    private static long _placeholderRetryAt;
    private static uint _session;
    private static long _sessionCheckedAt;
    private static int _queued;
    private static long _loaded, _missing, _failedUploads;
    private static long _trimmedAt;
    private static string _lastProblem = "";

    // ---- seams for the tests ----
    internal static Func<long> Clock = () => Environment.TickCount64;
    internal static Func<ItemId, Guid?> GameItem = id => GameNames.ItemGuid(id, wait: true);
    internal static Func<uint, ContentNames.Def?> Defined = id => ContentNames.FindNow(id);
    internal static Func<Guid, int> IconState = ResourceStore.IconState;
    internal static Func<Guid, int, (byte[] Rgba, int W, int H)?> Decode = DecodeNative;
    internal static Func<byte[], int, int, ulong> Upload = UploadNative;
    internal static Action<ulong> Free = FreeNative;
    internal static Func<uint> Session = () => ClientEvents.Session;
    internal static Func<ulong, int> ImageState = UiNative.ImageState;
    internal static bool Synchronous;  // tests: load on the asking thread instead of the worker

    /// <summary>Whether an icon's box at (x, y), w x h, shows in its window (wx, wy, ww, wh; screen pixels): one scrolled out
    /// of a list is neither loaded nor drawn. A window of no known size: shown.</summary>
    internal static bool OnScreen(float x, float y, float w, float h, float wx, float wy, float ww, float wh) =>
        ww <= 0 || wh <= 0 || (x < wx + ww && x + w > wx && y < wy + wh && y + h > wy);

    /// <summary>Icons can be drawn here: a game client with the overlay drawing (kiln.ui with image_load_rgba) and
    /// kiln.assets with texture_rgba.</summary>
    public static bool Available => UiNative.ImageLoadRgbaSlot != null && ResourceStore.HasTextureRgba;

    /// <summary>The pixels a trimmed icon drawn <paramref name="pixels"/> wide is read for: twice that, so the art, magnified
    /// out of its margin, stays sharp (a 40 px trimmed icon reads the 128 level; the cache's limits are unchanged).</summary>
    internal static float TrimmedPixels(float pixels) => pixels * 2;

    /// <summary>The image object a mod keeps for an item (one per item; drawing it asks the cache).</summary>
    public static UiImage? View(ItemId item)
    {
        if (!UiNative.HasGameUi) return null;
        return Views.GetOrAdd(item.Value, id => new UiImage { Item = new ItemId(id), Width = 64, Height = 64, Owner = null });
    }

    /// <summary>The overlay image to draw for the item at <paramref name="pixels"/> wide now: the icon, else (loading,
    /// unknown, no icon) the placeholder, else 0. Render thread (a draw); never waits, never throws.</summary>
    public static ulong Handle(ItemId item, float pixels, out int width, out int height)
    {
        width = height = 0;
        try
        {
            var key = (item.Value, ItemIconKey.Bucket(pixels));
            var now = Clock();
            List<IconTable<(uint, int)>.Entry>? drop = null;
            ulong h = 0, failed = 0;
            lock (Gate)
            {
                CheckSession(now, ref drop);
                if (Table.TryGet(key, out var e))
                {
                    e.LastUsed = now;
                    if (e.Handle != 0 && !e.Ready)
                    {
                        // drawn once kiln.ui uploaded it with its descriptor set; without one (the overlay's pool was full) it
                        // is never drawn: freed, read again later
                        var state = ImageState(e.Handle);
                        if (state == UiNative.ImageReady)
                        {
                            e.Ready = true;
                            e.Attempts = 0;
                        }
                        else if (state != UiNative.ImageWaiting)
                        {
                            failed = e.Handle;
                            e.Handle = 0;
                            Table.SetBytes(e, 0);
                            e.Texture = default;
                            e.RetryAt = now + RetryDelay.Of(++e.Attempts, RetryMs, RetryMostMs);
                            _failedUploads++;
                        }
                    }
                    if (e.Handle != 0 && e.Ready)
                    {
                        h = e.Handle;
                        width = e.Width;
                        height = e.Height;
                    }
                    // a missing icon, or one that may change (a new item's base icon while its own PNG is pending): looked at
                    // again, the picture it has stays drawn meanwhile
                    if (!e.Loading && e.RetryAt != 0 && now >= e.RetryAt) Request(key, e);
                }
                else if (item.Value != 0)
                {
                    Request(key, Table.Add(key, now));
                }
                // over a limit (missing icons piling up): one scan about once a frame, not one per icon drawn
                if (Table.Over && now - _trimmedAt >= TrimEveryMs)
                {
                    _trimmedAt = now;
                    if (Table.Trim(now - KeepMs) is { } trimmed) (drop ??= []).AddRange(trimmed);
                }
            }
            if (failed != 0)
            {
                Free(failed);
                Note($"item 0x{item.Value:x8}: the overlay could not upload its icon (no descriptor set); read again later");
            }
            if (drop != null) foreach (var d in drop) if (d.Handle != 0) Free(d.Handle);
            if (Synchronous) Drain();
            if (h != 0) return h;
            var p = Placeholder();
            if (p != 0) width = height = 16;
            return p;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Note($"item icon 0x{item.Value:x8}: {e.Message}");
            return 0;
        }
    }

    private static void Request((uint Item, int Bucket) key, IconTable<(uint, int)>.Entry e)
    {
        e.Loading = true;
        e.RetryAt = 0;
        if (Interlocked.Increment(ref _queued) > 256)
        {
            Interlocked.Decrement(ref _queued);
            e.Loading = false;
            e.RetryAt = Clock() + WaitMs;  // asked again in a moment, when the queue has room
            return;
        }
        Queue.Enqueue(key);
        if (!Synchronous) EnsureWorker();
        Wake.Release();
    }

    private static void CheckSession(long now, ref List<IconTable<(uint, int)>.Entry>? drop)
    {
        if (now - _sessionCheckedAt < 1000 && _sessionCheckedAt != 0) return;
        _sessionCheckedAt = now;
        var s = Session();
        if (s == _session) return;
        var first = _session == 0 && Table.Count == 0;
        _session = s;
        if (!first) (drop ??= []).AddRange(Table.Clear());  // another client world: new items may be another server's
    }

    private static void EnsureWorker()
    {
        if (_worker != null) return;
        lock (Gate)
        {
            if (_worker != null) return;
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Kiln item icons", Priority = ThreadPriority.BelowNormal };
            _worker.Start();
        }
    }

    private static void WorkerLoop()
    {
        while (true)
        {
            Wake.Wait();
            try
            {
                Drain();
            }
            catch (Exception e)
            {
                // never out of the thread (an exception there ends the game): out of memory included, the request is dropped
                Note($"the icon worker: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    /// <summary>Loads what is queued (the worker; the tests on their own thread).</summary>
    internal static void Drain()
    {
        while (Queue.TryDequeue(out var key))
        {
            Interlocked.Decrement(ref _queued);
            Load(key);
        }
    }

    private static void Load((uint Item, int Bucket) key)
    {
        try
        {
            LoadCore(key);
        }
        catch (Exception e)
        {
            // anything unforeseen, out of memory included: the request is dropped and the icon asked for again later, never left
            // loading (a loading entry is never asked for again nor trimmed)
            lock (Gate)
                if (Table.TryGet(key, out var x) && x.Loading)
                {
                    x.Loading = false;
                    x.RetryAt = Clock() + RetryDelay.Of(++x.Attempts, RetryMs, RetryMostMs);
                }
            Note($"item 0x{key.Item:x8}: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void LoadCore((uint Item, int Bucket) key)
    {
        var item = new ItemId(key.Item);
        Guid? texture = null;
        var final = false;
        (byte[] Rgba, int W, int H)? px = null;
        string? why = null;
        try
        {
            texture = ItemIconKey.Of(item, Defined, IconState, GameItem, out final);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            why = e.Message;
            final = false;
        }
        if (texture is { } t)
        {
            List<IconTable<(uint, int)>.Entry>? freed = null;
            var waiting = false;
            lock (Gate)
            {
                if (!Table.TryGet(key, out var self) || !self.Loading) return;  // dropped meanwhile (a world change)
                if (self.Handle != 0 && self.Texture == t)
                {
                    // looked at again (a new item's base icon while its own PNG is pending): the same texture, nothing to read
                    self.Loading = false;
                    self.RetryAt = final ? 0 : Clock() + RetryDelay.Of(++self.Attempts, RetryMs, RetryMostMs);
                    return;
                }
                // room before reading: icons not drawn for a second go first, oldest first; while every icon is drawn now
                // this one waits on the placeholder, its texture not read
                if (self.Bytes == 0)
                {
                    var estimate = (long)key.Bucket * key.Bucket * 4;
                    freed = Table.MakeRoom(estimate, Clock() - KeepMs);
                    if (!Table.Room(estimate))
                    {
                        Wait(self);
                        waiting = true;
                    }
                }
            }
            if (freed != null) foreach (var f in freed) if (f.Handle != 0) Free(f.Handle);
            if (waiting) return;
            try
            {
                px = Decode(t, key.Bucket);
                if (px is null) why = $"no texture {t} to read";
            }
            catch (OutOfMemoryException)
            {
                why = "out of memory reading its texture";
                final = false;
            }
            catch (Exception e)
            {
                why = e.Message;
                final = false;
            }
        }
        else why ??= "not an item known here (yet)";
        ulong handle = 0;
        var bytes = 0L;
        var uploadFailed = false;
        if (px is { } p)
        {
            bytes = (long)p.W * p.H * 4;
            if (bytes > MaxBytes)
            {
                // larger than the whole cache: it never fits, never read again
                why = $"{p.W}x{p.H} is larger than the item icon cache";
                px = null;
                final = true;
            }
            else
            {
                List<IconTable<(uint, int)>.Entry> freed;
                var full = false;
                lock (Gate)
                {
                    if (!Table.TryGet(key, out var self) || !self.Loading) return;
                    freed = self.Bytes > 0 ? [] : Table.MakeRoom(bytes, Clock() - KeepMs);
                    if (self.Bytes == 0 && !Table.Room(bytes))
                    {
                        Wait(self);  // a picture larger than its size class (not square): the draws must move on first
                        full = true;
                    }
                }
                foreach (var f in freed) if (f.Handle != 0) Free(f.Handle);
                if (full) return;
                try
                {
                    handle = Upload(p.Rgba, p.W, p.H);
                    if (handle == 0) why = "the overlay took no image";
                }
                catch (Exception e)
                {
                    why = $"the overlay took no image: {e.Message}";
                    handle = 0;
                }
                uploadFailed = handle == 0;
            }
        }
        ulong unused = 0;
        lock (Gate)
        {
            var now = Clock();
            if (!Table.TryGet(key, out var e)) unused = handle;  // dropped meanwhile (a world change)
            else
            {
                e.Loading = false;
                e.Waits = 0;
                if (handle != 0 && px is { } q && texture is { } shown)
                {
                    unused = e.Handle;  // the picture it replaces (a re-read)
                    e.Handle = handle;
                    e.Ready = false;    // drawn once kiln.ui uploaded it (image_state)
                    e.Texture = shown;
                    e.Width = q.W;
                    e.Height = q.H;
                    Table.SetBytes(e, bytes);
                    // a new item's base icon while its own PNG is pending: look again later, its own may be served by then
                    e.RetryAt = final ? 0 : now + RetryMs;
                    _loaded++;
                }
                else
                {
                    // a picture it has stays. No texture to read: looked for again after a minute, then less often; a failed
                    // read or upload, or an id not known yet: after 5 s, then less often; too large: never
                    e.Attempts++;
                    e.RetryAt = bytes > MaxBytes ? 0
                        : final && !uploadFailed ? now + RetryDelay.Of(e.Attempts, RetryFinalMs, RetryFinalMostMs)
                        : now + RetryDelay.Of(e.Attempts, RetryMs, RetryMostMs);
                    if (e.Handle == 0) _missing++;
                }
            }
        }
        if (unused != 0) Free(unused);
        if (why != null && texture is not null) Note($"item 0x{key.Item:x8}: {why}");
    }

    /// <summary>The cache is full of icons drawn now: asked for again in a moment, a little later each time (Gate held).</summary>
    private static void Wait(IconTable<(uint, int)>.Entry self)
    {
        self.Loading = false;
        self.RetryAt = Clock() + RetryDelay.Of(++self.Waits, WaitMs, WaitMostMs);
    }

    private static (byte[], int, int)? DecodeNative(Guid texture, int bucket)
    {
        var rc = ResourceStore.TextureRgba(new ResourceId(texture, 0xf9e86997u, 0), (uint)bucket, out var rgba, out var w, out var h);
        return rc == 0 ? (rgba, w, h) : null;
    }

    private static ulong UploadNative(byte[] rgba, int w, int h)
    {
        var f = UiNative.ImageLoadRgbaSlot;
        if (f == null) return 0;
        fixed (byte* p = rgba) return f(0, p, (uint)w, (uint)h);
    }

    private static void FreeNative(ulong handle)
    {
        if (UiNative.HasGameUi) UiNative.T->ImageFree(handle);
    }

    /// <summary>A neutral square (a faint fill and frame) drawn while an icon loads or when there is none. 0 without the
    /// overlay.</summary>
    internal static ulong Placeholder()
    {
        if (_placeholder != 0 || _placeholderTried) return _placeholder;
        lock (Gate)
        {
            if (_placeholderTried || Clock() < _placeholderRetryAt) return _placeholder;
            try
            {
                _placeholder = Upload(PlaceholderPixels(), 16, 16);
                _placeholderTried = true;  // 0: no overlay image here at all
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _placeholderRetryAt = Clock() + RetryMs;  // tried again in a moment
                Note($"the placeholder: {e.Message}");
            }
            return _placeholder;
        }
    }

    internal static byte[] PlaceholderPixels()
    {
        var px = new byte[16 * 16 * 4];
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
            {
                var edge = x == 0 || y == 0 || x == 15 || y == 15;
                var corner = (x is 0 or 15) && (y is 0 or 15);
                var i = (y * 16 + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = 200;
                px[i + 3] = corner ? (byte)0 : edge ? (byte)90 : (byte)36;
            }
        return px;
    }

    private static void Note(string text)
    {
        if (text == _lastProblem) return;
        _lastProblem = text;
        Backend.Current.Log(0, LogLevel.Debug, "Kiln.Ui item icons: " + text);
    }

    /// <summary>Diagnostics: (entries, textures held, their bytes, loads done, loads that found no icon, queued).</summary>
    internal static (int Count, int Textures, long Bytes, long Loaded, long Missing, int Queued) Stats
    {
        get
        {
            lock (Gate) return (Table.Count, Table.Textures, Table.Bytes, _loaded, _missing, Volatile.Read(ref _queued));
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Table.Clear();
            while (Queue.TryDequeue(out _)) { }
            _queued = 0;
            Views.Clear();
            _placeholder = 0;
            _placeholderTried = false;
            _placeholderRetryAt = 0;
            _session = 0;
            _sessionCheckedAt = 0;
            _loaded = _missing = _failedUploads = 0;
            _trimmedAt = 0;
            _lastProblem = "";
            Clock = () => Environment.TickCount64;
            GameItem = id => GameNames.ItemGuid(id, wait: true);
            Defined = id => ContentNames.FindNow(id);
            IconState = ResourceStore.IconState;
            Decode = DecodeNative;
            Upload = UploadNative;
            Free = FreeNative;
            Session = () => ClientEvents.Session;
            ImageState = UiNative.ImageState;
            Synchronous = false;
        }
    }
}
