using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LocalCpuEmbeddings;

internal sealed class Embedder : IDisposable
{
    private readonly InferenceSession _session;
    private readonly WordPiece _tokenizer;
    private readonly bool _nomic;
    public int Dimensions => _nomic ? 768 : 384;
    public const string QueryPrefix = "Represent this sentence for searching relevant passages: ";
    public Embedder(string path, string vocab, int threads)
    {
        _nomic = Path.GetFileName(path).StartsWith("nomic-", StringComparison.Ordinal);
        _tokenizer = new WordPiece(_nomic ? Path.Combine(Path.GetDirectoryName(vocab)!, "nomic-vocab.txt") : vocab);
        using SessionOptions options = new()
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        // CPU provider is the default. Disable ORT spin-waiting between requests.
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        _session = new InferenceSession(path, options);
    }
    public float[][] Embed(IReadOnlyList<string> texts, bool query = false)
    {
        var encoded = texts.Select(t => _tokenizer.Encode((_nomic ? (query ? "search_query: " : "search_document: ") : query ? QueryPrefix : "") + t)).ToArray();
        int length = encoded.Max(x => x.Length);
        int[] shape = [texts.Count, length];
        var ids = new DenseTensor<long>(shape);
        var mask = new DenseTensor<long>(shape);
        var types = new DenseTensor<long>(shape);
        for (int i = 0; i < encoded.Length; i++)
            for (int j = 0; j < encoded[i].Length; j++) { ids[i, j] = encoded[i][j]; mask[i, j] = 1; }
        List<NamedOnnxValue> inputs = [NamedOnnxValue.CreateFromTensor("input_ids", ids), NamedOnnxValue.CreateFromTensor("attention_mask", mask)];
        if (_session.InputMetadata.ContainsKey("token_type_ids")) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", types));
        using var results = _session.Run(inputs);
        var tensor = results.First(x => x.Name == "last_hidden_state").AsTensor<float>();
        if (tensor.Dimensions.Length != 3 || tensor.Dimensions[2] != Dimensions) throw new InvalidDataException("Unexpected ONNX hidden-state shape.");
        float[][] vectors = new float[texts.Count][];
        for (int i = 0; i < vectors.Length; i++)
        {
            var v = new float[Dimensions];
            double norm = 0;
            for (int d = 0; d < v.Length; d++)
            {
                if (_nomic)
                {
                    for (int j = 0; j < encoded[i].Length; j++) v[d] += tensor[i, j, d];
                    v[d] /= encoded[i].Length;
                }
                else v[d] = tensor[i, 0, d];
            }
            // Full-dimensional Nomic layer-normalization followed by L2 normalization
            // is equivalent to centering followed by L2 (the variance scale cancels).
            if (_nomic) { float mean = v.Average(); for (int d = 0; d < v.Length; d++) v[d] -= mean; }
            for (int d = 0; d < v.Length; d++) norm += (double)v[d] * v[d];
            norm = Math.Sqrt(norm);
            if (!double.IsFinite(norm) || norm == 0) throw new InvalidDataException("Invalid embedding norm.");
            for (int d = 0; d < v.Length; d++) v[d] /= (float)norm;
            vectors[i] = v;
        }
        return vectors;
    }
    public void Dispose() => _session.Dispose();
}
