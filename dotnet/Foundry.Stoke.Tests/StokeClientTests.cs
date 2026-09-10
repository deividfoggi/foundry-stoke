using System.Text.Json.Nodes;
using Foundry.Stoke;
using Foundry.Stoke.Auth;
using Foundry.Stoke.Errors;
using Foundry.Stoke.Session;
using Foundry.Stoke.Store;

namespace Foundry.Stoke.Tests;

public sealed class StokeClientTests
{
    [Fact]
    public void Construction_RejectsMissingOperationsImmediately()
    {
        var options = new StokeOptions("https://project.example.com");

        var error = Assert.Throws<ArgumentNullException>(() => new StokeClient(options, null!));

        Assert.Equal("operations", error.ParamName);
    }

    [Fact]
    public void Construction_RejectsMissingOptions()
    {
        var error = Assert.Throws<ArgumentNullException>(
            () => new StokeClient(null!, new FakeSessionOperations()));

        Assert.Equal("options", error.ParamName);
    }

    [Theory]
    [InlineData("http://project.example.com", null)]
    [InlineData("project.example.com", null)]
    [InlineData("", null)]
    [InlineData("https://other.example.com", "project.example.com")]
    public void Construction_RejectsInvalidEndpoint(string endpoint, string? expectedHost)
    {
        var options = new StokeOptions(endpoint) { ExpectedHost = expectedHost };

        Assert.Throws<InvalidEndpointException>(() => new StokeClient(options, new FakeSessionOperations()));
    }

    [Fact]
    public void Construction_ExposesTrustedConfigurationAndInjectedCredential()
    {
        var credential = new object();
        var options = new StokeOptions("https://project.example.com")
        {
            ExpectedHost = "project.example.com",
            Credential = credential,
        };
        var client = new StokeClient(options, new FakeSessionOperations());

        Assert.Equal("https://project.example.com", client.Options.ProjectEndpoint);
        Assert.Equal("project.example.com", client.Options.ExpectedHost);
        Assert.Same(credential, client.CredentialProvider.ResolveCredential());
    }

    [Fact]
    public async Task DefaultStores_RetainRecordsAndAreIsolatedPerClient()
    {
        var options = new StokeOptions("https://project.example.com");
        var first = new StokeClient(options, new FakeSessionOperations());
        var second = new StokeClient(options, new FakeSessionOperations());
        var record = new StoreRecord("session-1", "agent-a", "tracked-session", new JsonObject { ["value"] = 7 });

        await first.Store.CreateAsync(record);
        var persisted = await first.Store.ReadAsync(record.Id, record.PartitionKey);

        Assert.Equal(7, persisted.Payload["value"]!.GetValue<int>());
        Assert.Single(await first.Store.QueryByPartitionAsync("agent-a"));
        Assert.Empty(await second.Store.QueryByPartitionAsync("agent-a"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InjectedStore_PreservesRecordsAcrossClientsAndConcurrency(bool useFileSystem)
    {
        var directory = useFileSystem ? Directory.CreateTempSubdirectory("stoke-client-").FullName : null;
        try
        {
            IDurableStoreProvider store = directory is null ? new InMemoryStore() : new FileSystemStore(directory);
            var writer = new StokeClient(
                new StokeOptions("https://project.example.com") { Store = store }, new FakeSessionOperations());
            var record = await writer.Store.CreateAsync(
                new StoreRecord("session-1", "agent-a", "tracked-session", new JsonObject { ["value"] = 7 }));
            var reader = new StokeClient(
                new StokeOptions("https://project.example.com")
                {
                    Store = directory is null ? store : new FileSystemStore(directory),
                }, new FakeSessionOperations());

            var persisted = await reader.Store.ReadAsync(record.Id, record.PartitionKey);
            Assert.Equal(7, persisted.Payload["value"]!.GetValue<int>());
            persisted.Payload["value"] = 8;
            await reader.Store.UpsertAsync(persisted, persisted.Etag);
            await Assert.ThrowsAsync<ConcurrencyConflictException>(
                () => writer.Store.UpsertAsync(record, record.Etag));
        }
        finally
        {
            if (directory is not null)
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Sessions_CompleteLifecycleAndRememberDeletionAcrossAccesses()
    {
        var client = new StokeClient(new StokeOptions("https://project.example.com"), new FakeSessionOperations());

        var created = await client.Sessions.CreateSessionAsync("agent-a");
        Assert.Equal(SessionState.Active, created.State);
        Assert.Equal(900, created.IdleTimeoutSeconds);
        Assert.Equal(created.AgentSessionId, (await client.Sessions.GetSessionAsync("agent-a", created.AgentSessionId)).AgentSessionId);
        Assert.Single(await client.Sessions.ListSessionsAsync("agent-a"));
        Assert.Empty(await client.Sessions.ListSessionsAsync("agent-b"));

        await client.Sessions.StopSessionAsync("agent-a", created.AgentSessionId);
        Assert.Equal(SessionState.Idle, (await client.Sessions.GetSessionAsync("agent-a", created.AgentSessionId)).State);
        await client.Sessions.DeleteSessionAsync("agent-a", created.AgentSessionId);

        Assert.Empty(await client.Sessions.ListSessionsAsync("agent-a"));
        await Assert.ThrowsAsync<SessionClosedException>(() => client.Sessions.GetSessionAsync("agent-a", created.AgentSessionId));
        await Assert.ThrowsAsync<SessionClosedException>(() => client.Sessions.StopSessionAsync("agent-a", created.AgentSessionId));
        await Assert.ThrowsAsync<SessionClosedException>(() => client.Sessions.DeleteSessionAsync("agent-a", created.AgentSessionId));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(3600)]
    public async Task Sessions_UseExplicitIdleTimeout(int idleTimeoutSeconds)
    {
        var client = new StokeClient(new StokeOptions("https://project.example.com"), new FakeSessionOperations());

        var session = await client.Sessions.CreateSessionAsync("agent-a", idleTimeoutSeconds);

        Assert.Equal(idleTimeoutSeconds, session.IdleTimeoutSeconds);
    }

    [Theory]
    [InlineData(299)]
    [InlineData(3601)]
    public async Task Sessions_RejectInvalidIdleTimeoutWithoutCreatingSession(int idleTimeoutSeconds)
    {
        var client = new StokeClient(new StokeOptions("https://project.example.com"), new FakeSessionOperations());

        await Assert.ThrowsAsync<InvalidIdleTimeoutException>(
            () => client.Sessions.CreateSessionAsync("agent-a", idleTimeoutSeconds));

        Assert.Empty(await client.Sessions.ListSessionsAsync("agent-a"));
    }

    [Fact]
    public async Task Sessions_PropagateCancellationWithoutCreatingSession()
    {
        var client = new StokeClient(new StokeOptions("https://project.example.com"), new FakeSessionOperations());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.Sessions.CreateSessionAsync("agent-a", cancellationToken: cancellation.Token));

        Assert.Empty(await client.Sessions.ListSessionsAsync("agent-a"));
    }

    [Fact]
    public async Task FromEnvironment_ComposesSessionsAndResolvesConfiguredFallback()
    {
        var environ = new Dictionary<string, string>
        {
            ["FOUNDRY_PROJECT_ENDPOINT"] = "https://project.example.com",
            ["FOUNDRY_EXPECTED_HOST"] = "project.example.com",
            [CredentialProvider.ApiKeyEnv] = "test-only-key",
        };

        var client = StokeClient.FromEnvironment(new FakeSessionOperations(), environ);
        var session = await client.Sessions.CreateSessionAsync("agent-a");

        Assert.Equal("https://project.example.com", client.Options.ProjectEndpoint);
        Assert.Equal("project.example.com", client.Options.ExpectedHost);
        Assert.Equal(SessionState.Active, session.State);
        var credential = Assert.IsType<ApiKeyCredential>(client.CredentialProvider.ResolveCredential());
        Assert.Equal("test-only-key", credential.GetApiKey());
        Assert.Empty(await client.Store.QueryByPartitionAsync("agent-a"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FromEnvironment_RejectsMissingEndpoint(string? endpoint)
    {
        var environ = new Dictionary<string, string>();
        if (endpoint is not null)
        {
            environ["FOUNDRY_PROJECT_ENDPOINT"] = endpoint;
        }

        var error = Assert.Throws<ConfigurationException>(
            () => StokeClient.FromEnvironment(new FakeSessionOperations(), environ));

        Assert.Contains("FOUNDRY_PROJECT_ENDPOINT", error.Message);
    }

    [Theory]
    [InlineData("http://project.example.com", "project.example.com")]
    [InlineData("https://other.example.com", "project.example.com")]
    public void FromEnvironment_RejectsInvalidEndpoint(string endpoint, string expectedHost)
    {
        var environ = new Dictionary<string, string>
        {
            ["FOUNDRY_PROJECT_ENDPOINT"] = endpoint,
            ["FOUNDRY_EXPECTED_HOST"] = expectedHost,
        };

        Assert.Throws<InvalidEndpointException>(
            () => StokeClient.FromEnvironment(new FakeSessionOperations(), environ));
    }

    [Fact]
    public void FromEnvironment_RejectsMissingOperationsImmediately()
    {
        var error = Assert.Throws<ArgumentNullException>(
            () => StokeClient.FromEnvironment(null!, new Dictionary<string, string>()));

        Assert.Equal("operations", error.ParamName);
    }

    [Fact]
    public async Task FromEnvironment_DoesNotRequireCredentialsUntilResolution()
    {
        var client = StokeClient.FromEnvironment(new FakeSessionOperations(), new Dictionary<string, string>
        {
            ["FOUNDRY_PROJECT_ENDPOINT"] = "https://project.example.com",
        });

        Assert.Equal(SessionState.Active, (await client.Sessions.CreateSessionAsync("agent-a")).State);
        Assert.Throws<NoCredentialAvailableException>(() => client.CredentialProvider.ResolveCredential());
    }

    private sealed class FakeSessionOperations : ISessionOperations
    {
        private readonly Dictionary<(string Agent, string Session), RawSession> _sessions = new();
        private int _nextId;

        public Task<RawSession> CreateSessionAsync(
            string agentDefinitionId, int idleTimeoutSeconds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = new RawSession($"session-{++_nextId}", "active");
            _sessions.Add((agentDefinitionId, session.AgentSessionId), session);
            return Task.FromResult(session);
        }

        public Task<RawSession> GetSessionAsync(
            string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_sessions[(agentDefinitionId, agentSessionId)]);
        }

        public Task<IReadOnlyList<RawSession>> ListSessionsAsync(
            string agentDefinitionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<RawSession> sessions = _sessions
                .Where(entry => entry.Key.Agent == agentDefinitionId)
                .Select(entry => entry.Value)
                .ToList();
            return Task.FromResult(sessions);
        }

        public Task StopSessionAsync(
            string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sessions[(agentDefinitionId, agentSessionId)] = new RawSession(agentSessionId, "idle");
            return Task.CompletedTask;
        }

        public Task DeleteSessionAsync(
            string agentDefinitionId, string agentSessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sessions.Remove((agentDefinitionId, agentSessionId));
            return Task.CompletedTask;
        }
    }
}
