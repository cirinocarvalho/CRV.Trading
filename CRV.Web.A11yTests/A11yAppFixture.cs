using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Xunit;

namespace CRV.Web.A11yTests;

/// <summary>
/// Hosts CRV.Web on Kestrel in Production against an empty temp data directory, so the
/// run never sees the real DB or broker tokens, and owns the headless browser every scan uses.
/// </summary>
public sealed class A11yAppFixture : IAsyncLifetime
{
    private readonly string _originalDirectory = Environment.CurrentDirectory;
    private WebApplicationFactory<Program>? _factory;
    private IPlaywright? _playwright;

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "crv-a11y-" + Guid.NewGuid().ToString("N"));
    public Uri BaseAddress { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;
    public IServiceProvider Services => _factory!.Services;

    public async Task InitializeAsync()
    {
        // Program.cs moves the working directory to DATA_DIR, so the DB and token files land there.
        Environment.SetEnvironmentVariable("DATA_DIR", DataDir);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Production"));
        _factory.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        _factory.StartServer();

        var addresses = _factory.Services.GetRequiredService<IServer>()
            .Features.GetRequiredFeature<IServerAddressesFeature>().Addresses;
        BaseAddress = new Uri(addresses.First());
        A11ySeed.Apply(_factory.Services);

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (Browser != null) await Browser.CloseAsync();
        _playwright?.Dispose();
        if (_factory != null) await _factory.DisposeAsync();

        Environment.CurrentDirectory = _originalDirectory;
        Environment.SetEnvironmentVariable("DATA_DIR", null);
        try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { }
    }
}

[CollectionDefinition(Name)]
public sealed class A11yCollection : ICollectionFixture<A11yAppFixture>
{
    public const string Name = "a11y";
}
