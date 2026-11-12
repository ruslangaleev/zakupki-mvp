using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zakupki.Categorization.Llm;

namespace Zakupki.Categorization.Tests;

public sealed class LlmClientTests
{
    [Fact]
    public async Task Fake_returns_canned_response_by_reg_number()
    {
        var canned = new LlmClassificationResponse
        {
            L1Id = "g05",
            L2Id = "g05c01",
            Extra = ["g05c02"],
            Confidence = 0.81,
            Unsure = false,
            RawText = "fake:ok"
        };
        ILlmClient client = new FakeLlmClient(new Dictionary<string, LlmClassificationResponse>
        {
            ["reg-1"] = canned
        });

        var got = await client.ClassifyAsync(new LlmClassificationRequest
        {
            RegNumber = "reg-1",
            Title = "лекарство",
            Okpd2Codes = ["21.20.10"]
        });

        Assert.False(client.IsLiveProvider);
        Assert.Equal("g05", got.L1Id);
        Assert.Equal("g05c01", got.L2Id);
        Assert.Equal(["g05c02"], got.Extra);
        Assert.Equal(0.81, got.Confidence);
        Assert.False(got.Unsure);
        Assert.Equal("fake:ok", got.RawText);
    }

    [Fact]
    public async Task Fake_default_is_unsure_without_canned_row()
    {
        var client = new FakeLlmClient();
        var got = await client.ClassifyAsync(new LlmClassificationRequest
        {
            RegNumber = "unknown",
            Title = "что угодно"
        });

        Assert.True(got.Unsure);
        Assert.Equal(0, got.Confidence);
        Assert.Null(got.L1Id);
        Assert.Null(got.L2Id);
    }

    [Fact]
    public void Without_api_key_di_resolves_fake_llm_client()
    {
        var previous = Environment.GetEnvironmentVariable(LlmOptions.ApiKeyEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(LlmOptions.ApiKeyEnvironmentVariable, null);

            var services = new ServiceCollection();
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Llm:Provider"] = "openai",
                    ["Llm:Model"] = "gpt-test",
                    ["Llm:TimeoutSeconds"] = "15"
                })
                .Build();
            services.AddZakupkiCategorization(config, enableMl: false);

            using var sp = services.BuildServiceProvider();
            var client = sp.GetRequiredService<ILlmClient>();

            Assert.IsType<FakeLlmClient>(client);
            Assert.False(client.IsLiveProvider);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmOptions.ApiKeyEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void CreateLlmClient_never_opens_network_even_with_key()
    {
        var previous = Environment.GetEnvironmentVariable(LlmOptions.ApiKeyEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(LlmOptions.ApiKeyEnvironmentVariable, "test-key-not-for-network");
            var client = ServiceCollectionExtensions.CreateLlmClient(new LlmOptions
            {
                Provider = "openai",
                Model = "should-not-call",
                TimeoutSeconds = 1
            });

            Assert.IsType<FakeLlmClient>(client);
            Assert.False(client.IsLiveProvider);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmOptions.ApiKeyEnvironmentVariable, previous);
        }
    }
}
