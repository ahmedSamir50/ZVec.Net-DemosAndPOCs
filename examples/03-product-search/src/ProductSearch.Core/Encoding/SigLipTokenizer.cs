using ProductSearch.Core.Models;
using HfTokenizer = Tokenizers.HuggingFace.Tokenizer.Tokenizer;

namespace ProductSearch.Core.Encoding;

/// <summary>
/// Loads Hugging Face <c>tokenizer.json</c> (Unigram) — same file transformers / transformers.js use.
/// Do not load <c>spiece.model</c> via Microsoft.ML.Tokenizers: that protobuf path throws
/// IndexOutOfRangeException on SigLIP (BOS/EOS ids are not valid vocab indexes).
/// </summary>
public sealed class SigLipTokenizer
{
    public const int DefaultContextLength = 64;
    public const int PadTokenId = 1;

    private readonly HfTokenizer _tokenizer;
    private readonly int _contextLength;
    private readonly bool _lowercaseText;

    public SigLipTokenizer(string modelsDir, SigLipModelDefinition model, int contextLength = DefaultContextLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDir);
        ArgumentNullException.ThrowIfNull(model);
        _contextLength = contextLength;
        _lowercaseText = model.LowercaseText;

        var jsonPath = Path.Combine(modelsDir, "tokenizer.json");
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException("SigLIP tokenizer.json not found.", jsonPath);

        _tokenizer = HfTokenizer.FromFile(jsonPath);
    }

    public (long[] InputIds, long[] AttentionMask) Encode(string text)
    {
        if (_lowercaseText)
            text = text.ToLowerInvariant();

        var encoding = _tokenizer.Encode(text, addSpecialTokens: false).First();
        var ids = new long[_contextLength];
        var mask = new long[_contextLength];
        var n = Math.Min(encoding.Ids.Count, _contextLength);
        for (var i = 0; i < n; i++)
        {
            ids[i] = encoding.Ids[i];
            mask[i] = 1;
        }

        for (var i = n; i < _contextLength; i++)
            ids[i] = PadTokenId;

        return (ids, mask);
    }
}
