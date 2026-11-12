namespace Zakupki.Categorization.Llm;

/// <summary>
/// Deterministic LLM stub: no network. Used when <c>LLM_API_KEY</c> is missing
/// and in tests. Optional canned answers by <see cref="LlmClassificationRequest.RegNumber"/>.
/// </summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly IReadOnlyDictionary<string, LlmClassificationResponse> _byReg;
    private readonly Func<LlmClassificationRequest, LlmClassificationResponse>? _factory;

    public FakeLlmClient()
        : this(null, null)
    {
    }

    public FakeLlmClient(IReadOnlyDictionary<string, LlmClassificationResponse>? byRegNumber)
        : this(byRegNumber, null)
    {
    }

    public FakeLlmClient(Func<LlmClassificationRequest, LlmClassificationResponse> factory)
        : this(null, factory)
    {
    }

    private FakeLlmClient(
        IReadOnlyDictionary<string, LlmClassificationResponse>? byRegNumber,
        Func<LlmClassificationRequest, LlmClassificationResponse>? factory)
    {
        _byReg = byRegNumber ?? new Dictionary<string, LlmClassificationResponse>(StringComparer.Ordinal);
        _factory = factory;
    }

    public bool IsLiveProvider => false;

    public Task<LlmClassificationResponse> ClassifyAsync(
        LlmClassificationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_factory is not null)
        {
            return Task.FromResult(_factory(request));
        }

        if (_byReg.TryGetValue(request.RegNumber, out var canned))
        {
            return Task.FromResult(canned);
        }

        return Task.FromResult(new LlmClassificationResponse
        {
            Unsure = true,
            Confidence = 0,
            RawText = "fake-llm:no-match"
        });
    }
}
