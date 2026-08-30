using Microsoft.ML.Tokenizers;
using ProductSearch.Core.Models;

namespace ProductSearch.Core.Encoding;

/// <summary>
/// Loads SentencePiece for SigLIP 1 (<c>spiece.model</c>) or SigLIP 2 (<c>tokenizer.model</c>).
/// </summary>
public sealed class SigLipTokenizer
{
    public const int DefaultContextLength = 64;

    private readonly SentencePieceTokenizer _tokenizer;
    private readonly int _contextLength;
    private readonly bool _lowercaseText;

    public SigLipTokenizer(string modelsDir, SigLipModelDefinition model, int contextLength = DefaultContextLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDir);
        ArgumentNullException.ThrowIfNull(model);
        _contextLength = contextLength;
        _lowercaseText = model.LowercaseText;

        var spPath = Path.Combine(modelsDir, model.SentencePieceFile);
        if (!File.Exists(spPath))
            throw new FileNotFoundException($"SigLIP {model.SentencePieceFile} not found.", spPath);

        using var spStream = File.OpenRead(spPath);
        _tokenizer = SentencePieceTokenizer.Create(
            spStream,
            addBeginningOfSentence: false,
            addEndOfSentence: false);
    }

    public (long[] InputIds, long[] AttentionMask) Encode(string text)
    {
        if (_lowercaseText)
            text = text.ToLowerInvariant();

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
