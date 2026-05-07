using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MemoryService.Api.Contracts;

namespace MemoryService.Contract.Tests;

public sealed class PersistenceTests : IClassFixture<MemoryServiceFactory>
{
    private readonly MemoryServiceFactory _factory;
    public PersistenceTests(MemoryServiceFactory factory) => _factory = factory;

    private static readonly JsonSerializerOptions SnakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public async Task Turn_round_trips_and_user_memories_endpoint_responds()
    {
        var client = _factory.CreateClient();

        var req = new IngestTurnRequest(
            SessionId: "p-session-1",
            UserId:    "p-user-1",
            Messages: new() { new IngestMessage("user", "I work at Notion as a PM.", null) },
            Timestamp: DateTime.UtcNow,
            Metadata:  null);

        var json = JsonSerializer.Serialize(req, SnakeCase);
        var post = await client.PostAsync("/turns", new StringContent(json, Encoding.UTF8, "application/json"));
        post.EnsureSuccessStatusCode();
        var postedJson = await post.Content.ReadAsStringAsync();
        var posted = JsonSerializer.Deserialize<IngestTurnResponse>(postedJson, SnakeCase);
        posted!.Id.Should().NotBeNullOrEmpty();

        // The fake LLM returns no candidates, so memories will be empty,
        // but the endpoint must still respond with a well-formed shape.
        var memResp = await client.GetAsync("/users/p-user-1/memories");
        memResp.EnsureSuccessStatusCode();
        var memBody = JsonSerializer.Deserialize<MemoriesResponse>(await memResp.Content.ReadAsStringAsync(), SnakeCase);
        memBody.Should().NotBeNull();
        memBody!.Memories.Should().NotBeNull();

        // Cleanup
        var del = await client.DeleteAsync("/sessions/p-session-1");
        del.IsSuccessStatusCode.Should().BeTrue();
    }
}
