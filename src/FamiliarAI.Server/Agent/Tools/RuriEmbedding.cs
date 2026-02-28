using System.Numerics.Tensors;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace FamiliarAI.Server.Agent.Tools;

/// <summary>
/// ruri-v3 ONNX embedding model (cl-nagoya/ruri-v3).
///
/// Model files are loaded from (in order):
///   1. ~/.familiar_ai/models/ruri-v3/
///   2. ./models/ruri-v3/  (relative to working directory, dev convenience)
///
/// Files required: model.onnx, tokenizer.model
/// </summary>
public sealed class RuriEmbedding : IDisposable
{
    private const string DocumentPrefix = "検索文書: ";
    private const string QueryPrefix    = "検索クエリ: ";

    private readonly InferenceSession _session;
    private readonly SentencePieceTokenizer _tokenizer;

    public int Dimensions { get; }

    public RuriEmbedding(string modelDir)
    {
        var modelPath     = Path.Combine(modelDir, "model.onnx");
        var tokenizerPath = Path.Combine(modelDir, "tokenizer.model");

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ruri-v3 model not found: {modelPath}");
        if (!File.Exists(tokenizerPath))
            throw new FileNotFoundException($"ruri-v3 tokenizer not found: {tokenizerPath}");

        _session = new InferenceSession(modelPath);
        using var stream = File.OpenRead(tokenizerPath);
        _tokenizer = SentencePieceTokenizer.Create(
            stream,
            addBeginningOfSentence: true,
            addEndOfSentence: true);

        // Probe dimensions by running a dummy input
        Dimensions = GetEmbedding("test").Length;
    }

    /// <summary>Encode documents (passage-side) for storage.</summary>
    public float[][] EncodeDocuments(IEnumerable<string> texts)
        => texts.Select(t => GetEmbedding(DocumentPrefix + t)).ToArray();

    /// <summary>Encode queries for retrieval.</summary>
    public float[][] EncodeQueries(IEnumerable<string> texts)
        => texts.Select(t => GetEmbedding(QueryPrefix + t)).ToArray();

    public float[] EncodeDocument(string text) => GetEmbedding(DocumentPrefix + text);
    public float[] EncodeQuery(string text)    => GetEmbedding(QueryPrefix + text);

    private float[] GetEmbedding(string text)
    {
        IReadOnlyList<int> rawIds = _tokenizer.EncodeToIds(text);
        long[] inputIds     = new long[rawIds.Count];
        long[] attentionMask = new long[rawIds.Count];
        for (int i = 0; i < rawIds.Count; i++)
        {
            inputIds[i]      = rawIds[i];
            attentionMask[i] = 1L;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(
                "input_ids",
                new DenseTensor<long>(inputIds, [1, inputIds.Length])),
            NamedOnnxValue.CreateFromTensor(
                "attention_mask",
                new DenseTensor<long>(attentionMask, [1, inputIds.Length])),
        };

        using var results = _session.Run(inputs);
        var output = results.First().AsTensor<float>();
        return Normalize(MeanPooling(output));
    }

    private static float[] MeanPooling(Microsoft.ML.OnnxRuntime.Tensors.Tensor<float> tensor)
    {
        int dim = tensor.Dimensions[2];
        int len = tensor.Dimensions[1];
        float[] avg = new float[dim];
        for (int i = 0; i < len; i++)
            for (int d = 0; d < dim; d++)
                avg[d] += tensor[0, i, d];
        for (int d = 0; d < dim; d++)
            avg[d] /= len;
        return avg;
    }

    private static float[] Normalize(float[] v)
    {
        float norm = TensorPrimitives.Norm<float>(v);
        if (norm > 0f) TensorPrimitives.Divide<float>(v, norm, v);
        return v;
    }

    /// <summary>Cosine similarity between a query vector and a matrix of document vectors.</summary>
    /// <param name="query">Already-normalized query vector.</param>
    /// <param name="docs">Row-major matrix; each row is a normalized document vector.</param>
    public static float[] CosineSimilarity(float[] query, float[][] docs)
    {
        var scores = new float[docs.Length];
        for (int i = 0; i < docs.Length; i++)
            scores[i] = TensorPrimitives.Dot<float>(query, docs[i]);
        return scores;
    }

    /// <summary>Find the default model directory (user data or local dev).</summary>
    public static string ResolveModelDir()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".familiar_ai", "models", "ruri-v3"),
            Path.Combine(Directory.GetCurrentDirectory(), "models", "ruri-v3"),
        };

        foreach (var dir in candidates)
            if (File.Exists(Path.Combine(dir, "model.onnx")))
                return dir;

        throw new DirectoryNotFoundException(
            "ruri-v3 model not found. Place model.onnx + tokenizer.model in " +
            $"~/.familiar_ai/models/ruri-v3/ or ./models/ruri-v3/");
    }

    public void Dispose() => _session.Dispose();
}
