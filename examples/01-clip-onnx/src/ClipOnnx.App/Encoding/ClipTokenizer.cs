using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClipOnnx.App.Encoding;

/// <summary>
/// OpenAI CLIP text tokenizer (BPE) used by the text ONNX encoder.
///
/// Files (from the HF CLIP package next to the ONNX models):
///   vocab.json  — token string → integer id
///   merges.txt  — ordered BPE merge rules (rank = line order)
///
/// Sequence layout (fixed length ContextLength = 77):
///   [ &lt;|startoftext|&gt; , …BPE tokens… , &lt;|endoftext|&gt; , pad… ]
///   input_ids      : long[77]  token ids (0 = pad)
///   attention_mask : long[77]  1 for real tokens, 0 for pad
///
/// Why 77? CLIP's text transformer was trained with max context 77.
/// Truncation always keeps EOT in the last slot when overflow occurs.
/// </summary>
public sealed class ClipTokenizer
{
    /// <summary>CLIP text max tokens (including SOT/EOT). Must match ONNX input shape [1,77].</summary>
    public const int ContextLength = 77;

    public const int SotId = 49406;
    public const int EotId = 49407;

    private const string Sot = "<|startoftext|>";
    private const string Eot = "<|endoftext|>";

    /// <summary>
    /// OpenAI CLIP regex (minus special tokens — those are added explicitly).
    /// Digits are matched one-at-a-time so "19" → "1","9".
    /// </summary>
    private static readonly Regex ClipPat = new(
        @"'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<(string A, string B), int> _mergeRanks;
    private readonly Dictionary<byte, char> _byteEncoder;

    public ClipTokenizer(string vocabPath, string mergesPath)
    {
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException("CLIP vocab.json not found. See README model setup.", vocabPath);
        if (!File.Exists(mergesPath))
            throw new FileNotFoundException("CLIP merges.txt not found. See README model setup.", mergesPath);

        using var vocabStream = File.OpenRead(vocabPath);
        _vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(vocabStream)
            ?? throw new InvalidOperationException("Failed to parse vocab.json");

        if (!_vocab.TryGetValue(Sot, out var sot) || sot != SotId
            || !_vocab.TryGetValue(Eot, out var eot) || eot != EotId)
        {
            throw new InvalidOperationException(
                $"Unexpected CLIP special token ids (expected SOT={SotId}, EOT={EotId}).");
        }

        // merges.txt: skip header comments; each "a b" line is one merge ranked by order.
        _mergeRanks = new Dictionary<(string, string), int>();
        var rank = 0;
        foreach (var line in File.ReadLines(mergesPath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                _mergeRanks[(parts[0], parts[1])] = rank;
                rank++;
            }
        }

        // Map every byte 0–255 to a printable unicode char so BPE can operate on strings.
        _byteEncoder = BuildByteEncoder();

        ValidateGoldenSample();
    }

    /// <summary>
    /// Encode raw query text → (input_ids, attention_mask) for the text ONNX session.
    /// Pipeline: clean → lower → CLIP regex split → bytes_to_unicode → BPE → vocab ids → pad/truncate to 77.
    /// </summary>
    public (long[] InputIds, long[] AttentionMask) Encode(string text)
    {
        var tokens = new List<int> { _vocab[Sot] };
        var cleaned = WhitespaceClean(BasicClean(text)).ToLowerInvariant();
        foreach (Match m in ClipPat.Matches(cleaned))
        {
            var word = m.Value;
            if (word.Length == 0)
                continue;

            // UTF-8 bytes → CLIP unicode alphabet, then BPE merge into subword tokens.
            var encoded = BytesToUnicode(System.Text.Encoding.UTF8.GetBytes(word));
            foreach (var bpeToken in Bpe(encoded))
            {
                if (!_vocab.TryGetValue(bpeToken, out var id))
                {
                    throw new InvalidOperationException(
                        $"CLIP BPE token '{bpeToken}' missing from vocab.json (query fragment '{word}').");
                }

                tokens.Add(id);
            }
        }

        tokens.Add(_vocab[Eot]);

        var ids = new long[ContextLength];
        var mask = new long[ContextLength];
        var n = Math.Min(tokens.Count, ContextLength);
        for (var i = 0; i < n; i++)
        {
            ids[i] = tokens[i];
            mask[i] = 1;
        }

        // Overflow: force EOT into the last position (CLIP convention).
        if (tokens.Count > ContextLength)
        {
            ids[ContextLength - 1] = _vocab[Eot];
            mask[ContextLength - 1] = 1;
        }

        return (ids, mask);
    }

    /// <summary>Index of EOT in a padded id sequence (for last_hidden_state pooling).</summary>
    public static int FindEotIndex(ReadOnlySpan<long> ids)
    {
        for (var i = ids.Length - 1; i >= 0; i--)
        {
            if (ids[i] == EotId)
                return i;
        }

        return Math.Max(0, ids.Length - 1);
    }

    /// <summary>
    /// Byte-pair encoding: start with characters (last marked &lt;/w&gt;), repeatedly merge
    /// the highest-ranked adjacent pair from merges.txt until no merge applies.
    /// </summary>
    private IEnumerable<string> Bpe(string token)
    {
        if (token.Length == 0)
            yield break;

        var word = token.Select(c => c.ToString()).ToList();
        // CLIP marks end-of-word so merges can distinguish "ing" vs "ing</w>".
        word[^1] = word[^1] + "</w>";

        while (word.Count > 1)
        {
            var bestRank = int.MaxValue;
            var bestA = "";
            var bestB = "";
            var found = false;
            for (var i = 0; i < word.Count - 1; i++)
            {
                if (_mergeRanks.TryGetValue((word[i], word[i + 1]), out var r) && r < bestRank)
                {
                    bestRank = r;
                    bestA = word[i];
                    bestB = word[i + 1];
                    found = true;
                }
            }

            if (!found)
                break;

            var merged = bestA + bestB;
            var newWord = new List<string>();
            for (var i = 0; i < word.Count;)
            {
                if (i < word.Count - 1 && word[i] == bestA && word[i + 1] == bestB)
                {
                    newWord.Add(merged);
                    i += 2;
                }
                else
                {
                    newWord.Add(word[i]);
                    i++;
                }
            }

            word = newWord;
        }

        foreach (var w in word)
            yield return w;
    }

    private string BytesToUnicode(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
            sb.Append(_byteEncoder[b]);
        return sb.ToString();
    }

    private static string BasicClean(string text)
        => text.Replace('\u2018', '\'').Replace('\u2019', '\'')
               .Replace('\u201c', '"').Replace('\u201d', '"').Trim();

    private static string WhitespaceClean(string text)
        => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>
    /// Smoke-check against OpenAI CLIP: SOT/EOT ids and a short known phrase.
    /// Golden ids for "a photo of a cat" from openai/clip-vit-base-patch32.
    /// </summary>
    private void ValidateGoldenSample()
    {
        var (ids, _) = Encode("a photo of a cat");
        // Expected: [49406, 320, 1125, 539, 320, 2368, 49407, 0…]
        int[] golden = [49406, 320, 1125, 539, 320, 2368, 49407];
        for (var i = 0; i < golden.Length; i++)
        {
            if (ids[i] != golden[i])
            {
                throw new InvalidOperationException(
                    $"CLIP tokenizer golden mismatch at index {i}: got {ids[i]}, expected {golden[i]} " +
                    "(\"a photo of a cat\"). Check vocab.json / merges.txt.");
            }
        }
    }

    /// <summary>
    /// OpenAI CLIP bytes_to_unicode: printable latin ranges map to themselves;
    /// remaining bytes map into the private-use area so every byte is a single char.
    /// </summary>
    private static Dictionary<byte, char> BuildByteEncoder()
    {
        var bs = Enumerable.Range('!', '~' - '!' + 1)
            .Concat(Enumerable.Range('¡', '¬' - '¡' + 1))
            .Concat(Enumerable.Range('®', 'ÿ' - '®' + 1))
            .ToList();
        var cs = bs.ToList();
        var n = 0;
        for (var b = 0; b < 256; b++)
        {
            if (bs.Contains(b)) continue;
            bs.Add(b);
            cs.Add(256 + n);
            n++;
        }

        var map = new Dictionary<byte, char>();
        for (var i = 0; i < bs.Count; i++)
            map[(byte)bs[i]] = (char)cs[i];
        return map;
    }
}
