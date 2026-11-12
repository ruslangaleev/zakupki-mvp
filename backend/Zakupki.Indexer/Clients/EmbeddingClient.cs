using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Zakupki.Indexer.Configuration;

namespace Zakupki.Indexer.Clients;

public sealed class EmbeddingClient(
    HttpClient httpClient,
    IOptions<IndexerOptions> options,
    ILogger<EmbeddingClient> logger)
{
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        var batchSize = Math.Max(1, options.Value.EmbedBatchSize);
        var result = new List<float[]>(texts.Count);

        for (var offset = 0; offset < texts.Count; offset += batchSize)
        {
            var batch = texts.Skip(offset).Take(batchSize).ToList();
            using var response = await httpClient.PostAsJsonAsync(
                "/embed",
                new EmbedRequest(batch),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("Embeddings failed: {Status} {Body}", response.StatusCode, body);
                response.EnsureSuccessStatusCode();
            }

            var payload = await response.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: cancellationToken)
                          ?? throw new InvalidOperationException("Empty embeddings response");

            foreach (var vector in payload.Embeddings)
            {
                result.Add(vector);
            }
        }

        return result;
    }

    private sealed record EmbedRequest(IReadOnlyList<string> Texts);

    private sealed class EmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public List<float[]> Embeddings { get; set; } = [];
    }
}
