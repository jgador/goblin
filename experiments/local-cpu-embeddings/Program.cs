using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LocalCpuEmbeddings;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var json = new JsonSerializerOptions { WriteIndented = true };
string command = args.ElementAtOrDefault(0) ?? "help";
string output = args.ElementAtOrDefault(1) ?? "results";
if (command != "load") Directory.CreateDirectory(output);
void Write(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, json) + "\n");
string model = args.ElementAtOrDefault(2) ?? "models/bge-int8.onnx";
int batch = int.Parse(args.ElementAtOrDefault(3) ?? "1");
int threads = int.Parse(args.ElementAtOrDefault(4) ?? "1");
bool lengthOrder = args.ElementAtOrDefault(5) == "length";
string db = Path.Combine(output, "memory.db");
if (command == "environment") { Write("environment.json", LinuxInfo.EnvironmentReport()); Console.WriteLine(File.ReadAllText(Path.Combine(output, "environment.json"))); return; }
if (command == "tokens")
{
    var tokenizer = new WordPiece("models/vocab.txt");
    var texts = Corpus.Generate().Select(x => x.Content).Concat(new[] { "Café naïve résumé, WorkStore.cs PR #381 libssl3 +1", "你好，世界!", "foo\tbar\n\u0000 baz", new string('a', 101), "😀 OpenSSL—dependency", "[CLS] select [MASK] [SEP] [PAD] [UNK] foo[MASK]bar [mask]", string.Concat(Enumerable.Repeat(" long text", 1000)) }).ToArray();
    Write("tokens.json", texts.Select(t => new { Text = t, Ids = tokenizer.Encode(t) })); return;
}
if (command == "load")
{
    int loadThreads = int.Parse(args[1]);
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    var workers = Enumerable.Range(0, loadThreads).Select(_ => new Thread(() =>
    {
        double x = 0.12345;
        while (!stop.IsCancellationRequested)
        {
            long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 45 / 1000;
            while (Stopwatch.GetTimestamp() < until) { for (int i = 0; i < 200; i++) x = Math.Sqrt(x + 1.234); }
            Thread.Sleep(5); GC.KeepAlive(x);
        }
    })
    { IsBackground = true }).ToArray();
    foreach (var t in workers) t.Start(); foreach (var t in workers) t.Join(); return;
}
if (command == "ingest")
{
    if (File.Exists(db)) throw new IOException("Refusing to overwrite an existing memory.db. Use a fresh output directory.");
    var corpus = Corpus.Generate(); Write("corpus.json", corpus);
    var tokenizer = new WordPiece("models/vocab.txt");
    var lengths = corpus.Select(x => tokenizer.Encode(x.Content).Length).Order().ToArray();
    using var sampler = new ResourceSampler(); using var store = new MemoryStore(db);
    var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime;
    var watch = Stopwatch.StartNew(); store.Ingest(corpus); store.Checkpoint(); watch.Stop();
    double ingestionCpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds;
    process.Refresh();
    int count = store.Lexical("migration 0048").Count;
    if (count == 0 || store.Count("ready") != 0 || store.Count("pending") != 2000) throw new InvalidOperationException("Immediate FTS/pending invariant failed.");
    // Query cost measured separately; no model loaded. Warm up before 200 repetitions.
    store.Lexical("migration 0048"); var fts = new List<double>();
    for (int i = 0; i < 200; i++) { var sw = Stopwatch.StartNew(); store.Lexical(i % 2 == 0 ? "migration 0048" : "libssl3"); fts.Add(sw.Elapsed.TotalMilliseconds); }
    await Task.Delay(100); sampler.WriteCsv(Path.Combine(output, "ingest-resources.csv"));
    Write("ingestion.json", new { Count = corpus.Count, Seconds = watch.Elapsed.TotalSeconds, RecordsPerSecond = corpus.Count / watch.Elapsed.TotalSeconds, DatabaseBytes = new FileInfo(db).Length, ProcessRssBytes = process.WorkingSet64, ProcessCpuSeconds = ingestionCpuSeconds, IngestionAverageProcessCpuPercent = ingestionCpuSeconds / watch.Elapsed.TotalSeconds * 100, FtsImmediatelySearchable = true, Pending = store.Count("pending"), Vectors = store.Count("ready"), FtsAverageMs = fts.Average(), FtsP95Ms = fts.Order().ElementAt((int)(fts.Count * .95)), NoiseFilteredCount = corpus.Count(x => !Corpus.ShouldEmbed(x)), NoiseFilteredPercent = corpus.Count(x => !Corpus.ShouldEmbed(x)) * 100.0 / corpus.Count, TokenLengths = new { Min = lengths[0], Median = lengths[1000], P95 = lengths[1900], Max = lengths[^1], TruncatedAt512 = lengths.Count(x => x == 512) }, Resources = sampler.Summary() });
    Console.WriteLine($"Ingested {corpus.Count}: {corpus.Count / watch.Elapsed.TotalSeconds:F0}/s; {new FileInfo(db).Length} bytes; FTS available."); return;
}
if (command is "bench" or "embed" or "worker" or "retrieve" or "validate")
{
    using var sampler = new ResourceSampler();
    var sw = Stopwatch.StartNew(); using var embedder = new Embedder(model, "models/vocab.txt", threads); sw.Stop();
    var process = Process.GetCurrentProcess(); process.Refresh(); long loadedRss = process.WorkingSet64; double loadSeconds = sw.Elapsed.TotalSeconds;
    Console.WriteLine($"Loaded {Path.GetFileName(model)} in {loadSeconds:F3}s; RSS {loadedRss / 1048576.0:F1} MiB; {threads} inference threads.");
    if (command == "validate")
    {
        string[] texts = Corpus.Generate().Take(embedder.Dimensions == 768 ? 8 : 64).Select(x => x.Content).ToArray();
        float[][] vectors = embedder.Embed(texts); Write("vectors-" + Path.GetFileNameWithoutExtension(model) + ".json", new { Texts = texts, Vectors = vectors, Norms = vectors.Select(v => Math.Sqrt(v.Sum(x => (double)x * x))).ToArray(), SingletonBatchCosine = Dot(vectors[0], embedder.Embed([texts[0]])[0]) }); return;
    }
    if (command == "bench")
    {
        if (File.Exists(db)) throw new IOException("Use a fresh benchmark output directory.");
        using var store = new MemoryStore(db); store.Ingest(Corpus.Generate().Take(512)); store.Checkpoint();
        embedder.Embed(Corpus.Generate().Take(batch).Select(x => x.Content).ToArray());
        double since = sampler.Elapsed; var cpuStart = process.TotalProcessorTime; var all = Stopwatch.StartNew();
        List<double> batchMs = [], inferenceMs = []; int processed = 0;
        while (store.Count("pending") > 0)
        {
            var batchWatch = Stopwatch.StartNew(); var items = store.Pending(batch, lengthOrder);
            var inf = Stopwatch.StartNew(); var vectors = embedder.Embed(items.Select(x => x.Content).ToArray()); inf.Stop();
            store.Save(items, vectors, Path.GetFileName(model)); batchWatch.Stop();
            batchMs.Add(batchWatch.Elapsed.TotalMilliseconds); inferenceMs.Add(inf.Elapsed.TotalMilliseconds); processed += items.Count;
        }
        all.Stop(); double cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;
        await Task.Delay(75); sampler.WriteCsv(Path.Combine(output, "resources.csv"));
        Write("benchmark.json", new { Model = Path.GetFileName(model), ModelBytes = new FileInfo(model).Length, Dimensions = embedder.Dimensions, Threads = threads, Batch = batch, Ordering = lengthOrder ? "length" : "id", Count = processed, LoadSeconds = loadSeconds, LoadedRssBytes = loadedRss, Seconds = all.Elapsed.TotalSeconds, RecordsPerSecond = processed / all.Elapsed.TotalSeconds, EmbeddingsPerSecond = processed / (inferenceMs.Sum() / 1000), AverageBatchMs = batchMs.Average(), P95BatchMs = Percentile(batchMs, .95), AverageInferenceBatchMs = inferenceMs.Average(), CpuSeconds = cpuSeconds, AverageProcessCpuPercent = cpuSeconds / all.Elapsed.TotalSeconds * 100, SampleStartSeconds = since, Resources = sampler.Summary(since), WholeProcessResources = sampler.Summary() });
        Console.WriteLine($"Batch {batch}: {processed / all.Elapsed.TotalSeconds:F2} records/s; mean batch {batchMs.Average():F1}ms."); return;
    }
    if (command == "embed")
    {
        using var store = new MemoryStore(db); List<object> checkpoints = [];
        long startCount = store.Count("ready"); int processed = 0; var all = Stopwatch.StartNew();
        while (store.Count("pending") > 0)
        {
            int next = processed < 500 ? 500 : processed < 1000 ? 1000 : 2000;
            var items = store.Pending(Math.Min(batch, next - processed)); if (items.Count == 0) break;
            var vectors = embedder.Embed(items.Select(x => x.Content).ToArray()); store.Save(items, vectors, Path.GetFileName(model)); processed += items.Count;
            if (processed is 500 or 1000 or 2000)
            { store.Checkpoint(); checkpoints.Add(new { Count = startCount + processed, DatabaseBytes = new FileInfo(db).Length, RawVectorBytes = (startCount + processed) * embedder.Dimensions * 4, Seconds = all.Elapsed.TotalSeconds }); Console.WriteLine($"Stored {processed} embeddings, DB {new FileInfo(db).Length} bytes."); }
        }
        all.Stop(); store.Checkpoint(); sampler.WriteCsv(Path.Combine(output, "embedding-resources.csv"));
        Write("storage.json", new { Model = Path.GetFileName(model), Batch = batch, Threads = threads, LoadSeconds = loadSeconds, LoadedRssBytes = loadedRss, Processed = processed, Seconds = all.Elapsed.TotalSeconds, RecordsPerSecond = processed / all.Elapsed.TotalSeconds, Checkpoints = checkpoints, Resources = sampler.Summary() }); return;
    }
    if (command == "retrieve")
    {
        using var store = new MemoryStore(db);
        if (store.Count("ready") < 1000) throw new InvalidOperationException("Retrieval requires >=1000 real vectors.");
        (string Query, string Kind, long[] Relevant)[] queries =
        [
            ("Why did the production deployment fail?","realistic",[1,2,3,4,8,11]),
            ("What package fixed the API startup problem?","realistic",[3,4,8]),
            ("What was decided about automatically creating pull requests?","realistic",[5,10]),
            ("What happened with migration 0048?","realistic",[1,11]),
            ("What do we know about repository authorization?","realistic",[6,9]),
            ("Which deployment problem involved SSL libraries?","realistic",[3,4,8]),
            ("What source file handles durable Work persistence?","realistic",[7,12]),
            ("PR #381","identifier",[8]),("libssl3","identifier",[3,8]),
            ("migration 0048","identifier",[1,11]),("WorkStore.cs","identifier",[7]),
            ("Which dependency caused the container to fail during startup?","semantic",[3,4,8]),
            ("Why was the live rollout blocked by a slow schema change?","semantic",[1,2,11]),
            ("Which component saves the job history reliably?","semantic",[7,12]),
            ("Why did the team choose to publish client updates by hand?","semantic",[5,10]),
            ("Why couldn't the rollout finish within its allotted time?","semantic",[1,2,11]),
            ("Where are job objectives recorded permanently?","semantic",[7,12])
        ];
        List<object> results = [];
        foreach (var q in queries)
        {
            var clock = Stopwatch.StartNew(); var lexical = store.Lexical(q.Query); double lexicalMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart(); var vector = embedder.Embed([q.Query], query: true)[0]; double queryEmbedMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart(); var semantic = store.Vector(vector); double vectorMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart(); var hybrid = MemoryStore.Hybrid(lexical, semantic); double fusionMs = clock.Elapsed.TotalMilliseconds;
            object Rank(List<SearchHit> hits) => new { Top5 = hits.Take(5).ToArray(), RecallAt5 = hits.Take(5).Count(x => q.Relevant.Contains(x.Id)) / (double)q.Relevant.Length, ReciprocalRank = hits.Select((x, i) => (x, i)).Where(x => q.Relevant.Contains(x.x.Id)).Select(x => 1.0 / (x.i + 1)).FirstOrDefault() };
            results.Add(new { q.Query, q.Kind, q.Relevant, LexicalMs = lexicalMs, QueryEmbeddingMs = queryEmbedMs, VectorScanMs = vectorMs, FusionMs = fusionMs, Lexical = Rank(lexical), Vector = Rank(semantic), Hybrid = Rank(hybrid) });
        }
        Write("retrieval.json", new { Model = Path.GetFileName(model), Embedded = store.Count("ready"), RrfK = 60, CandidateLimit = 50, Results = results }); return;
    }
    if (command == "worker")
    {
        if (File.Exists(db)) throw new IOException("Use a fresh worker output directory.");
        using var store = new MemoryStore(db); store.Ingest(Corpus.Generate()); store.SkipNoise(Corpus.Generate().Where(x => !Corpus.ShouldEmbed(x)));
        bool noiseSearchable = store.Lexical("thanks").Count > 0; if (!noiseSearchable) throw new InvalidOperationException("Filtered noise must remain in FTS.");
        embedder.Embed(store.Pending(batch).Select(x => x.Content).ToArray());
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var gate = new CapacityGate(); List<object> events = [], phases = [];
        // A dedicated foreground probe makes a small lexical lookup every 100ms.
        using var probeStop = new CancellationTokenSource(); List<(double At, double QueryMs, double DelayMs)> probes = [];
        var probe = new Thread(() =>
        {
            using var readStore = new MemoryStore(db); double next = sampler.Elapsed;
            while (!probeStop.IsCancellationRequested)
            {
                double actual = sampler.Elapsed; double delay = Math.Max(0, (actual - next) * 1000); var clock = Stopwatch.StartNew(); readStore.Lexical("migration 0048", 5); clock.Stop();
                lock (probes) probes.Add((actual, clock.Elapsed.TotalMilliseconds, delay));
                next = actual + .1; Thread.Sleep(100);
            }
        })
        { IsBackground = true }; probe.Start();
        try
        {
            foreach (var phase in new[] { "idle", "moderate", "heavy", "resumed" })
            {
                Process? load = null;
                try
                {
                    if (phase is "moderate" or "heavy")
                    {
                        var info = new ProcessStartInfo(Environment.ProcessPath!); info.ArgumentList.Add(typeof(Embedder).Assembly.Location); info.ArgumentList.Add("load"); info.ArgumentList.Add(phase == "moderate" ? Math.Max(1, (int)LinuxInfo.CpuCapacity / 2).ToString() : Math.Max(1, (int)LinuxInfo.CpuCapacity).ToString());
                        load = Process.Start(info); await Task.Delay(1500, stop.Token);
                    }
                    else await Task.Delay(500, stop.Token);
                    gate.Check(); await Task.Delay(250, stop.Token);
                    double since = sampler.Elapsed; int processed = 0, paused = 0; List<double> latencies = []; var clock = Stopwatch.StartNew();
                    int target = phase == "heavy" ? 128 : 512;
                    while (!stop.IsCancellationRequested && processed < target && clock.Elapsed.TotalSeconds < (phase == "heavy" ? 6 : 100))
                    {
                        var capacity = gate.Check(); events.Add(new { Phase = phase, Seconds = sampler.Elapsed, capacity.HasCapacity, capacity.CpuPercent, capacity.AvailableRam, capacity.CpuPsi, capacity.MemoryPsi, Pending = store.Count("pending") });
                        if (!capacity.HasCapacity) { paused++; await Task.Delay(250, stop.Token); continue; }
                        var items = store.Pending(Math.Min(batch, target - processed)); if (items.Count == 0) break;
                        var batchClock = Stopwatch.StartNew(); var vectors = embedder.Embed(items.Select(x => x.Content).ToArray()); store.Save(items, vectors, Path.GetFileName(model)); latencies.Add(batchClock.Elapsed.TotalMilliseconds); processed += items.Count;
                    }
                    clock.Stop(); double end = sampler.Elapsed;
                    (double At, double QueryMs, double DelayMs)[] measured; lock (probes) measured = probes.Where(x => x.At >= since && x.At <= end).ToArray();
                    phases.Add(new { Phase = phase, Processed = processed, PausedChecks = paused, Seconds = clock.Elapsed.TotalSeconds, RecordsPerSecond = processed / clock.Elapsed.TotalSeconds, AverageBatchMs = latencies.Count == 0 ? 0 : latencies.Average(), P95BatchMs = latencies.Count == 0 ? 0 : Percentile(latencies, .95), ProbeCount = measured.Length, ForegroundP95QueryMs = measured.Length == 0 ? 0 : Percentile(measured.Select(x => x.QueryMs).ToList(), .95), ForegroundP95DelayMs = measured.Length == 0 ? 0 : Percentile(measured.Select(x => x.DelayMs).ToList(), .95), Resources = sampler.Summary(since) });
                    Console.WriteLine($"Worker {phase}: {processed} records, {paused} paused checks, {processed / clock.Elapsed.TotalSeconds:F2}/s.");
                }
                finally { if (load is not null) { if (!load.HasExited) load.Kill(entireProcessTree: true); load.WaitForExit(); load.Dispose(); } }
            }
        }
        finally { probeStop.Cancel(); probe.Join(); }
        sampler.WriteCsv(Path.Combine(output, "worker-resources.csv")); Write("worker.json", new { Model = Path.GetFileName(model), Batch = batch, Threads = threads, Phases = phases, Events = events, NoiseSkipped = store.Count("skipped_noise"), NoiseRemainsSearchable = noiseSearchable, Ready = store.Count("ready"), Pending = store.Count("pending") }); return;
    }
}
Console.WriteLine("Commands: environment|tokens|ingest|bench|embed|retrieve|validate|worker OUTPUT [MODEL] [BATCH=1] [THREADS=1]; load THREADS");

static double Dot(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum();
static double Percentile(List<double> values, double p) => values.Order().ElementAt(Math.Min(values.Count - 1, (int)(values.Count * p)));
