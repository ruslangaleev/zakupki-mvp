using Zakupki.Indexer.Models;

namespace Zakupki.Indexer.Extraction;

public static class TextChunker
{
    public static IReadOnlyList<ExtractedChunk> Chunk(
        string source,
        string text,
        int chunkSize,
        int overlap)
    {
        var normalized = NormalizeWhitespace(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        if (normalized.Length <= chunkSize)
        {
            return
            [
                new ExtractedChunk
                {
                    ChunkId = $"{source}#0",
                    Source = source,
                    Text = normalized
                }
            ];
        }

        var chunks = new List<ExtractedChunk>();
        var step = Math.Max(1, chunkSize - overlap);
        var index = 0;
        for (var start = 0; start < normalized.Length; start += step)
        {
            var length = Math.Min(chunkSize, normalized.Length - start);
            var piece = normalized.Substring(start, length).Trim();
            if (piece.Length > 0)
            {
                chunks.Add(new ExtractedChunk
                {
                    ChunkId = $"{source}#{index}",
                    Source = source,
                    Text = piece
                });
                index++;
            }

            if (start + length >= normalized.Length)
            {
                break;
            }
        }

        return chunks;
    }

    public static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
