using Microsoft.ML.Tokenizers;

namespace ProductSearch.Core.Encoding;

/// <summary>
/// Loads Xenova <c>spiece.model</c> (SentencePiece) for SigLIP text encoding.
/// </summary>
public sealed class SigLipTokenizer
{
    public const int DefaultContextLength = 64;

    private readonly LlamaTokenizer _tokenizer;
    private readonly int _contextLength;

    public SigLipTokenizer(string modelsDir, int contextLength = DefaultContextLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDir);
        _contextLength = contextLength;

        var spiecePath = Path.Combine(modelsDir, "spiece.model");
        if (!File.Exists(spiecePath))
            throw new FileNotFoundException("SigLIP spiece.model not found.", spiecePath);

        _tokenizer = LlamaTokenizer.Create(File.OpenRead(spiecePath), addBeginOfSentence: true, addEndOfSentence: true);
    }

    public (long[] InputIds, long[] AttentionMask) Encode(string text)
    {
        var encoding = _tokenizer.EncodeToIds(text);
        var ids = new long[_contextLength];
        var mask = new long[_contextLength];
        var n = Math.Min(encoding.Count, _contextLength);
        for (var i = 0; i < n; i++)
        {
            ids[i] = encoding[i];
            mask[i] = 1;
        }

        return (ids, mask);
    }
}
