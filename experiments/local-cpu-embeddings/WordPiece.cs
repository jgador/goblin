using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalCpuEmbeddings;

// BERT uncased basic tokenization + greedy WordPiece. No model/framework dependency.
internal sealed class WordPiece
{
    private readonly Dictionary<string, int> _vocab;
    public WordPiece(string path)
    {
        _vocab = File.ReadLines(path).Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
    }

    public long[] Encode(string text, int maxLength = 512)
    {
        if (maxLength < 2) throw new ArgumentOutOfRangeException(nameof(maxLength));
        List<long> ids = [_vocab["[CLS]"]];
        foreach (string word in BasicTokens(text))
        {
            List<int> pieces = [];
            if (word.EnumerateRunes().Count() > 100) pieces.Add(_vocab["[UNK]"]);
            else
            {
                int start = 0;
                while (start < word.Length)
                {
                    int end = word.Length;
                    int? found = null;
                    while (end > start)
                    {
                        string piece = (start > 0 ? "##" : "") + word[start..end];
                        if (_vocab.TryGetValue(piece, out int value)) { found = value; break; }
                        end--;
                        if (end > start && char.IsLowSurrogate(word[end])) end--;
                    }
                    if (found is null) { pieces = [_vocab["[UNK]"]]; break; }
                    pieces.Add(found.Value);
                    start = end;
                }
            }
            ids.AddRange(pieces.Select(x => (long)x));
            if (ids.Count >= maxLength - 1) break;
        }
        if (ids.Count > maxLength - 1) ids.RemoveRange(maxLength - 1, ids.Count - (maxLength - 1));
        ids.Add(_vocab["[SEP]"]);
        return ids.ToArray();
    }

    private static IEnumerable<string> BasicTokens(string text)
    {
        foreach (string part in Regex.Split(text, @"(\[UNK\]|\[SEP\]|\[PAD\]|\[CLS\]|\[MASK\])"))
        {
            if (part is "[UNK]" or "[SEP]" or "[PAD]" or "[CLS]" or "[MASK]") yield return part;
            else foreach (string token in CleanBasicTokens(part)) yield return token;
        }
    }

    private static IEnumerable<string> CleanBasicTokens(string text)
    {
        StringBuilder cleaned = new();
        foreach (Rune r in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(r);
            if (r.Value is 0 or 0xfffd || (category is UnicodeCategory.Control or UnicodeCategory.Format && r.Value is not (9 or 10 or 13))) continue;
            if (Rune.IsWhiteSpace(r)) cleaned.Append(' ');
            else if (IsChinese(r.Value)) cleaned.Append(' ').Append(r).Append(' ');
            else cleaned.Append(r);
        }
        foreach (string token in cleaned.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            StringBuilder current = new();
            foreach (Rune r in token.ToLowerInvariant().Normalize(NormalizationForm.FormD).EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(r);
                if (category == UnicodeCategory.NonSpacingMark) continue;
                bool punctuation = (r.Value >= 33 && r.Value <= 47) || (r.Value >= 58 && r.Value <= 64) || (r.Value >= 91 && r.Value <= 96) || (r.Value >= 123 && r.Value <= 126)
                    || category is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
                if (punctuation)
                {
                    if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                    yield return r.ToString();
                }
                else current.Append(r);
            }
            if (current.Length > 0) yield return current.ToString();
        }
    }

    private static bool IsChinese(int c) => c is >= 0x4e00 and <= 0x9fff or >= 0x3400 and <= 0x4dbf or >= 0x20000 and <= 0x2a6df or >= 0x2a700 and <= 0x2b73f or >= 0x2b740 and <= 0x2b81f or >= 0x2b820 and <= 0x2ceaf or >= 0xf900 and <= 0xfaff or >= 0x2f800 and <= 0x2fa1f;
}
