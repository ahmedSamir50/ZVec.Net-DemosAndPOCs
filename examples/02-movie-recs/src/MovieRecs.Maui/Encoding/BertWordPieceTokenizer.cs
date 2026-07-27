using System.Text;

namespace MovieRecs.Maui.Encoding;

/// <summary>
/// Minimal Bert WordPiece tokenizer for all-MiniLM-L6-v2.
/// </summary>
/// <remarks>
/// <para>
/// <b>vocab.txt contract:</b> each line is one token string; the <b>0-based line index</b>
/// is that token's integer id. Bert models (including MiniLM) expect sequences of those ids,
/// not raw text.
/// </para>
/// <para>
/// Pipeline: NFC + lowercase → split whitespace/punct → WordPiece (longest match, <c>##</c>
/// continuations) → wrap with <c>[CLS]</c> … <c>[SEP]</c> → pad to <c>maxLength</c> with
/// <c>[PAD]</c>. The attention mask is 1 for real tokens and 0 for pads so mean-pooling
/// ignores padding.
/// </para>
/// </remarks>
internal sealed class BertWordPieceTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly int _unkId;
    private readonly int _clsId;
    private readonly int _sepId;
    private readonly int _padId;

    public BertWordPieceTokenizer(string vocabPath)
    {
        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = new StreamReader(vocabPath);
        var i = 0;
        while (reader.ReadLine() is { } line)
        {
            // Line order IS the id — do not sort or dedupe.
            _vocab[line] = i++;
        }

        // Bert reserved specials — look up by string so we track this model's vocab.
        // Fallbacks are the classic Bert-uncased ids (all-MiniLM-L6-v2 uses the same numbers).
        // [UNK] = out-of-vocab / failed WordPiece; [CLS] = sequence start (always first);
        // [SEP] = sequence end; [PAD] = length filler (attention must be 0).
        _unkId = _vocab.GetValueOrDefault("[UNK]", 100);
        _clsId = _vocab.GetValueOrDefault("[CLS]", 101);
        _sepId = _vocab.GetValueOrDefault("[SEP]", 102);
        _padId = _vocab.GetValueOrDefault("[PAD]", 0);
    }

    /// <summary>
    /// Encode text to fixed-length <c>input_ids</c> + <c>attention_mask</c> for ONNX MiniLM.
    /// </summary>
    public (long[] InputIds, long[] AttentionMask) Encode(string text, int maxLength)
    {
        maxLength = Math.Clamp(maxLength, 8, 512);
        // Always start with [CLS]. Leave one slot for [SEP] (hence maxLength - 1 below).
        var tokens = new List<int> { _clsId };
        foreach (var word in BasicTokens(text))
        {
            foreach (var id in WordPiece(word))
            {
                // Stop before the last slot so [SEP] always fits.
                if (tokens.Count >= maxLength - 1)
                    break;
                tokens.Add(id);
            }
            if (tokens.Count >= maxLength - 1)
                break;
        }
        tokens.Add(_sepId);

        var ids = new long[maxLength];
        var mask = new long[maxLength];
        for (var i = 0; i < maxLength; i++)
        {
            if (i < tokens.Count)
            {
                ids[i] = tokens[i];
                mask[i] = 1; // real token — include in mean-pool
            }
            else
            {
                ids[i] = _padId;
                mask[i] = 0; // pad — MiniLM/mean-pool must ignore these positions
            }
        }
        return (ids, mask);
    }

    /// <summary>
    /// Greedy longest-match WordPiece. Continuation pieces use the Bert <c>##</c> prefix
    /// (e.g. <c>playing</c> → <c>play</c> + <c>##ing</c>).
    /// </summary>
    private List<int> WordPiece(string token)
    {
        var ids = new List<int>();
        if (_vocab.TryGetValue(token, out var id))
        {
            ids.Add(id);
            return ids;
        }

        var start = 0;
        while (start < token.Length)
        {
            var end = token.Length;
            var found = -1;
            // Longest match first: shrink end until a vocab hit.
            while (start < end)
            {
                var piece = start == 0
                    ? token[start..end]
                    : "##" + token[start..end];
                if (_vocab.TryGetValue(piece, out var pid))
                {
                    found = pid;
                    break;
                }
                end--;
            }

            if (found < 0)
            {
                // No substring matched — whole token becomes [UNK].
                ids.Clear();
                ids.Add(_unkId);
                return ids;
            }

            ids.Add(found);
            start = end;
        }

        if (ids.Count == 0)
            ids.Add(_unkId);
        return ids;
    }

    /// <summary>
    /// Bert-uncased basic tokenize: Unicode NFC, lowercase, split on whitespace and punctuation.
    /// </summary>
    private static IEnumerable<string> BasicTokens(string text)
    {
        text = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
                continue;
            }

            if (IsPunctuation(ch))
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
                // Punctuation is its own token (often in vocab as a single char).
                yield return ch.ToString();
                continue;
            }

            sb.Append(ch);
        }

        if (sb.Length > 0)
            yield return sb.ToString();
    }

    private static bool IsPunctuation(char ch) =>
        char.IsPunctuation(ch) || ch is '"' or '`' or '^' or '~' or '<' or '>' or '#' or '$' or '%' or '&' or '*' or '+' or '/' or '=' or '@' or '\\' or '|' or '_';
}
