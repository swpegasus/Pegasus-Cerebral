using System.Text.Json;
using Cerebral.BrowserPlugin;
using Cerebral.Core;

internal static class BrowserPublicPageChecks
{
    internal static async Task RunAsync(string root, bool includeSource = true, bool disposeOpenTab = false)
    {
        var steps = new List<object>();
        var profile = new AgentProfile { Id = "browser-public-check", Capabilities = ["browser.read", "browser.navigate"] };
        profile.PluginSettings[BrowserServicePlugin.Id] = BrowserJson.Element(new BrowserPolicy
        {
            AllowedDomains = ["example.com", "example.org"], NavigationTimeoutSeconds = 15,
            ActionTimeoutSeconds = 5, MinimumActionDelayMilliseconds = 100
        });
        var plugin = new BrowserServicePlugin();
        plugin.Initialize(new PublicHost(root));
        var context = new ToolExecutionContext(profile, "public-page-check", Guid.NewGuid().ToString("N"));
        var stage = "catalog";
        var failed = false;
        try
        {
            if (plugin.Tools.Count != 8 || !plugin.Tools.Any(tool => tool.Name == "browser.request"))
                throw new InvalidOperationException("Current eight-tool catalog is incomplete.");
            async Task<JsonElement> Call(string name, object arguments)
            {
                stage = name;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var result = await plugin.ExecuteAsync(new(name, BrowserJson.Element(arguments)), context, CancellationToken.None);
                var envelope = JsonDocument.Parse(result.Content).RootElement;
                steps.Add(new { tool = name, result.Success, outcome = result.EffectiveOutcome.ToString(), code = envelope.GetProperty("code").GetString(), milliseconds = clock.ElapsedMilliseconds });
                if (!result.Success) throw new InvalidOperationException(result.Content);
                return envelope.GetProperty("data").Clone();
            }
            if (includeSource)
            {
                var source = await Call("browser.request", new { requestId = "public-example-read", url = "https://example.com/" });
                if (source.GetProperty("statusCode").GetInt32() != 200 || !source.GetProperty("text").GetString()!.Contains("Example Domain"))
                    throw new InvalidOperationException("Public HTTP source did not contain the expected example page.");
            }
            var opened = await Call("browser.open", new { url = "https://example.org/" });
            var tabId = opened.GetProperty("tabId").GetString()!;
            var read = await Call("browser.inspect", new { tabId });
            if (read.GetProperty("title").GetString() != "Example Domain" || string.IsNullOrWhiteSpace(read.GetProperty("text").GetString()))
                throw new InvalidOperationException("The rendered public page did not return its title and visible body text.");
            if (!disposeOpenTab)
            {
                await Call("browser.navigate", new { tabId, url = "https://example.com/" });
                read = await Call("browser.inspect", new { tabId });
                if (read.GetProperty("title").GetString() != "Example Domain" || string.IsNullOrWhiteSpace(read.GetProperty("text").GetString()))
                    throw new InvalidOperationException("Public navigation did not return its title and visible body text.");
                await Call("browser.close", new { tabId });
            }
        }
        catch (Exception error)
        {
            failed = true;
            Console.WriteLine(BrowserJson.Text(new { suite = "browser-public-page", passed = false, stage, steps,
                error = error.Message, evidence = "actual public-page attempt; no private pages, login, form submission or existing browser profile" }));
            throw;
        }
        finally
        {
            try { plugin.Dispose(); }
            catch (Exception error)
            {
                Console.WriteLine(BrowserJson.Text(new { suite = "browser-public-cleanup", passed = false, error = error.GetType().Name }));
                if (!failed) throw;
            }
        }
        Console.WriteLine(BrowserJson.Text(new { suite = "browser-public-page", passed = true, steps, disposedOpenTab = disposeOpenTab,
            evidence = "real public HTTPS and production system Edge in an isolated temporary profile; direct plugin host, not installed Worker dispatch" }));
    }

    private sealed class PublicHost(string root) : IServicePluginHost
    {
        public string PluginId => BrowserServicePlugin.Id;
        public string PackageDirectory => AppContext.BaseDirectory;
        public string DataDirectory => root;
        public void Publish(string eventType, string? jobId, object? data) { }
    }
}
