using Cerebral.Core;
using Microsoft.Playwright;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Text;

namespace Cerebral.BrowserPlugin;

internal sealed class BrowserSession : IAsyncDisposable
{
    private IBrowser? browser;
    private IBrowserContext? context;
    public BrowserSession(BrowserPolicy policy, string owner, string profile)
    {
        Policy = policy; Owner = owner; Profile = profile;
        Network = new(this);
    }
    public BrowserSession(IBrowser browser, IBrowserContext context, BrowserPolicy policy, string owner,
        string profile, string generation) : this(policy, owner, profile) => Attach(browser, context, generation);
    public bool HasBrowser => browser is not null;
    public IBrowser Browser => browser ?? throw new InvalidOperationException("No rendered browser is attached.");
    public IBrowserContext Context => context ?? throw new InvalidOperationException("No rendered browser is attached.");
    public BrowserPolicy Policy { get; }
    public string Owner { get; }
    public string Profile { get; }
    public string Generation { get; private set; } = "http-source";
    public string RequestSessionId { get; } = Guid.NewGuid().ToString("N");
    public ConcurrentDictionary<string, BrowserTab> Tabs { get; } = new(StringComparer.Ordinal);
    public DateTimeOffset LastAction { get; set; } = DateTimeOffset.UtcNow;
    public long? ObservedPageHeapBytes { get; set; }
    public int BlockedRequests;
    public string? LastGate { get; set; }
    public BrowserNetworkGate Network { get; }
    private BrowserInternetScope? internet;
    private ManagedInternetAuthority? internetOwner;

    internal void BindInternet(BrowserInternetScope? scope)
    {
        // Clear first: an unavailable/mismatched next invocation must not reuse an older grant.
        Volatile.Write(ref internet, null);
        if (scope is null) return;
        var next = scope.Authority;
        if (internetOwner is { } prior &&
            (prior.WorkerInstallationId != next.WorkerInstallationId || prior.MainConversationId != next.MainConversationId ||
             prior.ContextId != next.ContextId || prior.ConversationId != next.ConversationId || prior.RunId != next.RunId))
            throw new BrowserRejectedException("managed-internet-owner-mismatch", "This browser session belongs to another host Internet owner.");
        internetOwner ??= next;
        Volatile.Write(ref internet, scope);
    }

    internal BrowserInternetScope RequireInternet() => Volatile.Read(ref internet) ??
        throw new BrowserRejectedException("managed-internet-unavailable", "This request has no current host-managed Internet scope.");
    public void Attach(IBrowser selectedBrowser, IBrowserContext selectedContext, string generation)
    {
        if (HasBrowser) throw new InvalidOperationException("A rendered browser is already attached.");
        browser = selectedBrowser; context = selectedContext; Generation = generation;
    }
    public async ValueTask DisposeAsync()
    {
        Network.Dispose();
        // Provider shutdown may already have ended the owning driver connection.
        // Closed handles need no further RPC, but their HTTP transport is still released above.
        if (context is null || browser is null || !browser.IsConnected) return;
        try { await context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        // System Edge's graceful exit can exceed five seconds even after its context closes.
        finally { await browser.CloseAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
    }
}

internal sealed class BrowserTab(IPage page)
{
    public IPage Page { get; } = page;
    public string Snapshot { get; set; } = "";
    public List<IElementHandle> Elements { get; } = [];
    public async Task InvalidateAsync()
    {
        Snapshot = "";
        foreach (var element in Elements) await element.DisposeAsync();
        Elements.Clear();
    }
}

internal static class BrowserProjection
{
    // Bound traversal work as well as returned text. Input values and password fields never enter the projection.
    private const string Script = """
        () => {
          const walker = document.createTreeWalker(document.body || document.documentElement, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
          const elements = []; let text = ''; let seen = 0; let node;
          while ((node = walker.nextNode()) && seen++ < 3000) {
            const el = node.nodeType === Node.TEXT_NODE ? node.parentElement : node;
            if (!el || el.closest('script,style,noscript,textarea,input,[contenteditable], [hidden], [aria-hidden="true"]')) continue;
            if (node.nodeType === Node.TEXT_NODE && text.length < 12000 && el.getClientRects().length)
              text += (node.textContent || '').replace(/\s+/g,' ').slice(0, Math.max(0,12000-text.length)) + ' ';
            if (node.nodeType === Node.ELEMENT_NODE && elements.length < 64 &&
                node.matches('a[href],button,select,[role="button"],[role="link"]') && node.getClientRects().length) elements.push(node);
          }
          // Text-entry controls are separately bounded and represented by their label, never their value.
          const inputs = document.createTreeWalker(document.body || document.documentElement, NodeFilter.SHOW_ELEMENT);
          let n = 0;
          while ((node = inputs.nextNode()) && n++ < 3000 && elements.length < 64)
            if (node.matches('input:not([type="password"]):not([type="hidden"]):not([type="file"]),textarea') && node.getClientRects().length) elements.push(node);
          return { text: text.slice(0,12000), bounded: seen >= 3000 || n >= 3000 || elements.length === 64, elements };
        }
        """;

    public static async Task<object> InspectAsync(string tabId, BrowserTab tab)
    {
        await tab.InvalidateAsync();
        await using var root = new AsyncHandle(await tab.Page.EvaluateHandleAsync(Script));
        await using var textHandle = new AsyncHandle(await root.Handle.GetPropertyAsync("text"));
        var rawText = await textHandle.Handle.JsonValueAsync<string>();
        var text = Utf8Clip(rawText, 12000);
        await using var boundedHandle = new AsyncHandle(await root.Handle.GetPropertyAsync("bounded"));
        var bounded = await boundedHandle.Handle.JsonValueAsync<bool>() || text.Length != rawText.Length;
        await using var elements = new AsyncHandle(await root.Handle.GetPropertyAsync("elements"));
        var properties = await elements.Handle.GetPropertiesAsync();
        var rows = new List<object>();
        foreach (var pair in properties.OrderBy(pair => int.TryParse(pair.Key, out var n) ? n : int.MaxValue))
        {
            if (pair.Value.AsElement() is not { } element) { await pair.Value.DisposeAsync(); continue; }
            var info = await element.EvaluateAsync<JsonElement>("""
                el => ({tag:el.tagName.toLowerCase().slice(0,32),type:(el.getAttribute('type')||'').slice(0,32),
                  label:(el.getAttribute('aria-label')||el.labels?.[0]?.textContent||el.textContent||el.getAttribute('placeholder')||'').trim().slice(0,120)})
                """);
            tab.Elements.Add(element); rows.Add(new { id = tab.Elements.Count, description = info });
        }
        tab.Snapshot = Guid.NewGuid().ToString("N");
        return new { tabId, snapshotId = tab.Snapshot, url = PublicUrl(tab.Page.Url),
            title = Clip(await tab.Page.TitleAsync(), 256), text, controls = rows, truncated = bounded };
    }
    public static string PublicUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        ? uri.GetLeftPart(UriPartial.Path) : "";
    private static string Clip(string text, int maximum) => text.Length <= maximum ? text : text[..maximum];
    private static string Utf8Clip(string text, int maximum)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maximum) return text;
        var builder = new StringBuilder(); var count = 0;
        foreach (var rune in text.EnumerateRunes())
        { if (count + rune.Utf8SequenceLength > maximum) break; builder.Append(rune); count += rune.Utf8SequenceLength; }
        return builder.ToString();
    }
    private sealed class AsyncHandle(IJSHandle handle) : IAsyncDisposable
    {
        public IJSHandle Handle { get; } = handle;
        public ValueTask DisposeAsync() => Handle.DisposeAsync();
    }
}
