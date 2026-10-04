using System.Text.Json;
using AutopilotQuant.Core.Forward;
using AutopilotQuant.Core.MarketData;
using AutopilotQuant.T4;
using AutopilotQuant.Web;
using Xunit;

namespace AutopilotQuant.Tests;

public sealed class MarketDataConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AutopilotQuantTests", Guid.NewGuid().ToString("N"));
    private readonly PaperSessionStore _store;
    private readonly PaperSession _session;
    private readonly MarketDataConfiguration _data;
    private static MarketDataSettings External => new("external", [new("MES", MarketId: "MES-expiring-test"), new("MNQ", false)]);
    private static FeedQuote Quote => new("MES", "MES-expiring-test", DateTimeOffset.UtcNow, 5000m, 5000.25m);
    public MarketDataConfigurationTests()
    {
        _store = new(_directory); _session = new(_store);
        _data = new(_directory, _session, new(), "adapter-test-key", null);
    }
    public void Dispose()
    {
        _store.Dispose();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutopilotQuantTests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(_directory).StartsWith(root, StringComparison.OrdinalIgnoreCase)) Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task Provider_Must_Be_Selected_Even_When_Adapter_Key_Exists()
    {
        Assert.False(_data.View().Configured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _data.AcceptQuoteAsync(Quote));
        await _data.SaveAsync(External);
        Assert.True(_data.View().Configured);
        Assert.True((await _data.AcceptQuoteAsync(Quote)).Instruments[0].Fresh);
        await Assert.ThrowsAsync<ArgumentException>(() => _data.AcceptQuoteAsync(Quote with { ContractId = "MES-other-expiry" }));
        await Assert.ThrowsAsync<ArgumentException>(() => _data.AcceptQuoteAsync(Quote with { Symbol = "MNQ" }));
    }

    [Fact]
    public async Task Bars_And_Quotes_Use_The_Same_Contract_Binding()
    {
        await _data.SaveAsync(External);
        var end = DateTimeOffset.UtcNow.AddMinutes(-10);
        end = new DateTimeOffset(end.UtcTicks / (5 * TimeSpan.TicksPerMinute) * (5 * TimeSpan.TicksPerMinute), TimeSpan.Zero);
        var bar = new MarketBar("MES", end, 5000, 5001, 4999, 5000, 100);
        await Assert.ThrowsAsync<ArgumentException>(() => _data.AcceptBarAsync(new("wrong-expiry", bar, true)));
        await _data.AcceptBarAsync(new("MES-expiring-test", bar, true));
        await _data.AcceptQuoteAsync(Quote);
        await _data.SaveAsync(External with { Provider = "t4-simulator" });
        var view = await _session.ViewAsync();
        Assert.True(view.Paused);
        Assert.All(view.Instruments, i => { Assert.Null(i.Quote); Assert.Empty(i.Bars); Assert.False(i.Fresh); Assert.Null(i.ContractId); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.ControlAsync("resume"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _data.AcceptQuoteAsync(Quote));
    }

    [Fact]
    public async Task Incomplete_Setup_Can_Be_Saved_But_Cannot_Test_Or_Accept_Data()
    {
        var saved = await _data.SaveAsync(MarketDataSettings.Empty with { Provider = "t4-simulator" });
        Assert.False(saved.Configured);
        Assert.Contains(saved.Missing, item => item.Contains("ApiKey"));
        Assert.False(saved.ContinuousT4StreamingSupported);
        Assert.False(saved.LiveRoutingEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _data.TestAsync(default));
        await _data.SaveAsync(MarketDataSettings.Empty with { Provider = "external" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => _data.AcceptQuoteAsync(Quote));
    }

    [Fact]
    public async Task Settings_Persist_Without_Credentials_Or_Test_Success()
    {
        const string secret = "TEST-ONLY-DO-NOT-RETURN-THIS-SECRET";
        var data = new MarketDataConfiguration(_directory, _session, new(), "adapter-test", secret);
        await data.SaveAsync(External);
        var persisted = File.ReadAllText(Path.Combine(_directory, "market-data.json"));
        Assert.DoesNotContain(secret, persisted);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(data.View()));
        var restored = new MarketDataConfiguration(_directory, _session, new(), null, null).View();
        Assert.Equal("external", restored.Settings.Provider);
        Assert.False(restored.Configured);
        Assert.Null(restored.LastTest);
        Assert.Equal("MES-expiring-test", restored.Settings.Instruments[0].MarketId);
    }

    [Fact]
    public async Task Invalid_Settings_Do_Not_Clear_Valid_Quotes()
    {
        await _data.SaveAsync(External);
        await _data.AcceptQuoteAsync(Quote);
        await Assert.ThrowsAsync<ArgumentException>(() => _data.SaveAsync(External with { Provider = "t4-live" }));
        Assert.True((await _session.ViewAsync()).Instruments[0].Fresh);
    }

    [Fact]
    public void Rejects_Duplicate_Contracts_Unknown_Symbols_And_Control_Characters()
    {
        Assert.Throws<ArgumentException>(() => MarketDataConfiguration.Validate(new("external", [new("MES", MarketId:"same"),new("MNQ", MarketId:"same")])));
        Assert.Throws<ArgumentException>(() => MarketDataConfiguration.Validate(new("external", [new("MES"),new("ES")])));
        Assert.Throws<ArgumentException>(() => MarketDataConfiguration.Validate(new("external", [new("MES", MarketId:"secret\n"),new("MNQ")])));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MarketDataSettings>("""
            {"provider":"t4-simulator","instruments":[],"apiKey":"not-accepted-from-the-browser"}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void Corrupt_Settings_Fail_Startup_Instead_Of_Enabling_Defaults()
    {
        File.WriteAllText(Path.Combine(_directory, "market-data.json"), "{broken");
        Assert.Throws<JsonException>(() => new MarketDataConfiguration(_directory, _session, new(), "adapter", null));
    }
}
