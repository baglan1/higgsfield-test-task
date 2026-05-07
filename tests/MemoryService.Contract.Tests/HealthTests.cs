using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace MemoryService.Contract.Tests;

public sealed class HealthTests : IClassFixture<MemoryServiceFactory>
{
    private readonly MemoryServiceFactory _factory;
    public HealthTests(MemoryServiceFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_returns_200_with_status_ok()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<HealthBody>();
        body!.status.Should().Be("ok");
    }

    private sealed record HealthBody(string status);
}
