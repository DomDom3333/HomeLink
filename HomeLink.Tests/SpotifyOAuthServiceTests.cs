using HomeLink.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeLink.Tests;

public class SpotifyOAuthServiceTests : IDisposable
{
    private readonly List<string> _environmentVariablesToClear = [];

    [Fact]
    public void ResolveRedirectUri_DerivesFromRequest_WhenNotConfigured()
    {
        SpotifyOAuthService service = CreateService();

        string redirectUri = service.ResolveRedirectUri(CreateRequest("https", "homelink.example.com"));

        Assert.Equal("https://homelink.example.com/api/spotify/callback", redirectUri);
    }

    [Fact]
    public void ResolveRedirectUri_PrefersConfiguredValue()
    {
        SpotifyOAuthService service = CreateService(("Spotify:RedirectUri", "https://configured.example.com/api/spotify/callback"));

        string redirectUri = service.ResolveRedirectUri(CreateRequest("http", "localhost:5119"));

        Assert.Equal("https://configured.example.com/api/spotify/callback", redirectUri);
    }

    [Fact]
    public void ResolveRedirectUri_PrefersEnvironmentVariableOverConfiguration()
    {
        SetEnvironmentVariable("SPOTIFY_REDIRECT_URI", "https://env.example.com/api/spotify/callback");
        SpotifyOAuthService service = CreateService(("Spotify:RedirectUri", "https://configured.example.com/api/spotify/callback"));

        string redirectUri = service.ResolveRedirectUri(CreateRequest("https", "homelink.example.com"));

        Assert.Equal("https://env.example.com/api/spotify/callback", redirectUri);
    }

    [Fact]
    public void BuildAuthorizeUrl_IncludesScopesRedirectUriAndState()
    {
        SpotifyOAuthService service = CreateService();

        string url = service.BuildAuthorizeUrl("client-id", "https://homelink.example.com/api/spotify/callback", "state-value");

        Assert.StartsWith("https://accounts.spotify.com/authorize?", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("client_id=client-id", url);
        Assert.Contains($"scope={Uri.EscapeDataString(SpotifyOAuthService.Scopes)}", url);
        Assert.Contains($"redirect_uri={Uri.EscapeDataString("https://homelink.example.com/api/spotify/callback")}", url);
        Assert.Contains("state=state-value", url);
    }

    [Fact]
    public void TryConsumeState_AcceptsIssuedStateExactlyOnce()
    {
        SpotifyOAuthService service = CreateService();

        string state = service.CreateState();

        Assert.True(service.TryConsumeState(state));
        Assert.False(service.TryConsumeState(state));
    }

    [Fact]
    public void TryConsumeState_RejectsUnknownOrEmptyState()
    {
        SpotifyOAuthService service = CreateService();

        Assert.False(service.TryConsumeState("never-issued"));
        Assert.False(service.TryConsumeState(null));
        Assert.False(service.TryConsumeState("  "));
    }

    [Fact]
    public void SetupKey_IsOptional_ButEnforcedWhenConfigured()
    {
        SpotifyOAuthService withoutKey = CreateService();
        Assert.False(withoutKey.SetupKeyRequired);
        Assert.True(withoutKey.IsSetupKeyValid(null));

        SpotifyOAuthService withKey = CreateService(("Spotify:SetupKey", "s3cret"));
        Assert.True(withKey.SetupKeyRequired);
        Assert.True(withKey.IsSetupKeyValid("s3cret"));
        Assert.False(withKey.IsSetupKeyValid("wrong"));
        Assert.False(withKey.IsSetupKeyValid(null));
    }

    private SpotifyOAuthService CreateService(params (string Key, string Value)[] settings)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();

        return new SpotifyOAuthService(NullLogger<SpotifyOAuthService>.Instance, configuration);
    }

    private static HttpRequest CreateRequest(string scheme, string host)
    {
        DefaultHttpContext context = new();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        return context.Request;
    }

    private void SetEnvironmentVariable(string name, string? value)
    {
        _environmentVariablesToClear.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (string name in _environmentVariablesToClear)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        GC.SuppressFinalize(this);
    }
}
