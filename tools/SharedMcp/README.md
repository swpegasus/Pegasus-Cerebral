# Shared MCP development framework

`SharedMcp` is a product-neutral, embeddable .NET 10 library. Cortex or Cerebral supplies an `IMcpDispatcher`; the library owns HTTP transport, bounded independent sessions, cancellation, teardown, expiry and correlation. It owns no product tool catalog, database, credential store, execution grant or context-transfer route. Optional injected authentication and session lifecycle contracts let the product supply verified connection identities. The library issues no credentials and implements no OAuth/login or approval policy.

The library itself defaults to **127.0.0.1**, port 47831, with only loopback peers admitted. LAN and VPN access are disabled by default. `BindAddress` must be a specific loopback/private address, and `AllowedPeerCidrs` explicitly controls actual TCP peers. Both must be deliberately configured to enable another network. Private RFC1918, IPv6 ULA and CGNAT/VPN addresses are supported; wildcard/public networks and noncanonical CIDRs are rejected. Inherited Kestrel/appsettings/environment configuration cannot add listeners. Proxy headers are ignored. Host and browser Origin are checked; no CORS is enabled. No firewall or installed-service settings are changed.

## Components

- `SharedMcp/`: library and injected dispatcher contract; only the installed ASP.NET shared framework is referenced, with no added NuGet dependency.
- `SharedMcp.Stdio/`: optional integration bridge. One lazily started existing STDIO backend per product serves every session. It rewrites request/cancellation IDs, negotiates Cortex's existing multiplex extension and preserves the product's own admission/privilege boundary. It never rebuilds a catalog or product composition.
- `CerebralBackend/`: small presentation of the existing `OperatorMcpHost`, linked from its owning source, and the existing `OperatorMcpRegistry`. It exposes Cerebral control through the existing typed transports and does not start Main, Configuration, Worker, models or business work.
- `DevHost/`: optional development executable hosting separately configured product backends.
- `Tests/`: bounded executable checks using real loopback HTTP and deterministic child backends. `--live` reads tool discovery/status from the already-running installed Cortex host and inspects the real Cerebral compiled catalog. `--live --cerebral-health` additionally attempts the separate captured service-health read; this is not required for transport acceptance.

The direct embedding seam is intentionally small:

```csharp
// ProductDispatcher implements IMcpDispatcher using the owning product's existing catalog/handler.
await using var dispatcher = new ProductDispatcher(existingProductHandler);
await using var listener = await SharedMcpServer.StartAsync(
    new McpServerOptions(),
    new Dictionary<string, IMcpDispatcher> { ["cerebral"] = dispatcher });
// The host keeps listener alive for its normal application lifetime.
```

`InitializeAsync` returns an InitializeResult object. `DispatchAsync` returns one JSON-RPC result/error envelope using the original typed request ID and must honor its cancellation token. The host owns dispatcher disposal. A dispatcher that ignores cancellation retains its pending reservation until it actually finishes, bounding abandoned work. The library never interprets `TaskId`, `SessionId` or request metadata as authority. The embedding product remains responsible for operation-level admission and its existing OS/UAC/PAO boundaries.

## Optional authenticated connection identity and session lifecycle

`McpServerOptions.Authenticator` accepts an optional product-owned `IMcpRequestAuthenticator`. A null authenticator preserves the existing unauthenticated profile; it grants no additional product authority. After the existing peer, Host, Origin and TLS-qualification guards, the server calls `AuthenticateAsync` on **every** MCP HTTP request before looking up or allocating a session, including notifications, GET and DELETE. The immutable input contains product, method, the configured resource URL, actual TCP peer and a header snapshot. Headers may contain credentials and must never be logged or persisted. No forwarded peer, `clientInfo`, chat label or session ID proves identity.

The verifier returns `McpAuthenticationResult.Anonymous`, `Authenticated(identity)`, or `Reject(401|403, optionalChallenge)`. A rejected credential or verifier exception cannot fall back to anonymous admission. Verifier failures return a bounded 503; rejected credentials return 401/403 and, if provided, the product's bounded `WWW-Authenticate` challenge. With a verifier installed, even an erroneous Anonymous result is refused when an `Authorization` header was presented. Products using other credential headers must enforce the same presented-invalid rule themselves. Verification calls are bounded in number by `MaximumPendingTotal`, receive disconnect/shutdown/timeout cancellation, and retain their reservations until the verifier actually returns. A verifier must implement its own bounded I/O and never replay an operation.

`McpAuthenticatedIdentity(Authority, Subject, Generation)` contains immutable, case-sensitive, bounded **public opaque identifiers**, derived only by the verifier. Authority qualifies the product/issuer; Subject is the durable registered connection; Generation identifies its active credential family/version. These are not bearer tokens, private keys, OS identities or Full control grants. The session binds all three values at initialization. A later request must freshly authenticate to the same exact identity and generation; cross-principal reuse and anonymous/authenticated transitions return 403 and require new initialization. Reconnecting with the same valid registered credential creates a new transport session/stream while preserving the product-owned persistent identity. A shared credential proves its holder's connection, not one particular chat.

The existing five-argument `McpRequestContext` constructor and deconstruction remain available. Its added init-only `Identity` is populated from the verified session; `TaskId` remains an untrusted observed chat label. Products own credential custody, registration, readable labels/source, bounded observed chat IDs, full-ID copy, revocation UI and persistence. They must check the active connection **and grant generation at operation admission and on retained privileged pipes**, since authentication before reading an HTTP body cannot eliminate revocation races. Full control stays off unless the product explicitly approves it. The neutral library neither persists approval nor turns authentication, metadata or a service relay into SYSTEM execution. Ordinary tool admission and Cerebral's existing independently authenticated app-session fallback remain product policy.

A dispatcher may additionally implement `IMcpSessionLifecycle`. `SessionOpenedAsync(McpSessionContext, CancellationToken)` runs once before its existing `InitializeAsync`, so it receives the verified identity and explicit product/session/stream/protocol without an ambient current-client singleton. `SessionClosedAsync(context, reason)` pairs even with a failed/partially completed open and runs exactly once **after open, initialize and every admitted dispatch have actually settled**. Reasons are `Deleted`, `IdleExpired`, `InitializationFailed` and `ServerShutdown`; the first close wins. Closed sessions reject new requests immediately and remain counted against `MaximumSessions` through drain and close cleanup. DELETE acknowledges initiation with 204; it does not assert that an already-dispatched effect was undone. Close failures emit only the bounded `session-close-failed` trace event and cannot close a sibling session.

The server owns and awaits each session's cleanup task. Listener disposal requests cancellation, drains verifier and session callbacks, awaits close cleanup, and leaves dispatcher disposal to the embedding host. Repeated disposal waits for the same completion. A noncooperative callback can therefore delay disposal: cancellation is not proof of completion, and product cleanup must remain bounded. The product must keep borrowed dispatchers, certificates and dependent resources alive until listener disposal completes. Decorator dispatchers must explicitly forward the optional lifecycle interface when their wrapped dispatcher uses it. Old stateless `IMcpDispatcher` implementations need no new methods. STDIO bridge adapters do not forward authenticated authority to a backend; a product-owned adapter must explicitly admit and carry any such authority through its existing authenticated boundary.

These identity/lifecycle additions are currently **source-only and unverified**. The focused `Tests --identity` selector defines isolated loopback HTTP checks with deterministic verifiers/dispatchers for fresh verification, same-identity reconnect, same-ID routing, identity/generation mismatch, forged metadata, failed credentials, revocation, close/drain/capacity, failure cleanup and stateless compatibility. No product identity store, approval UI, OAuth flow, real credentials, installed clients or Full control behavior is exercised by those fixtures. Build and execution are intentionally held for the coordinated verification pass; earlier test results below apply to the preceding source snapshot.

For the current development bridge, direct hosting of Cortex's internal composition was rejected because it would create a second persistence/execution composition. Its existing installed STDIO adapter already validates the resident host and immutable selection, uses existing local registration internally, and supports multiplexing. The library permits a future direct dispatcher without changing HTTP/session machinery. Cerebral retains its separate catalog and control transport.

## Build and run locally

From `C:\Development\Pegasus-Cerebral`:

```powershell
dotnet build tools/SharedMcp/SharedMcp.slnx
dotnet tools/SharedMcp/Tests/bin/Debug/net10.0/Tests.dll
dotnet tools/SharedMcp/Tests/bin/Debug/net10.0/Tests.dll --tls
dotnet tools/SharedMcp/Tests/bin/Debug/net10.0/Tests.dll --live
dotnet tools/SharedMcp/DevHost/bin/Debug/net10.0/DevHost.dll tools/SharedMcp/devhost.example.json
```

The supplied example uses the current source workspace and existing stable Cortex launcher. It is explicit machine-local development configuration, not portable installation metadata. Start the host once; stop it with Ctrl+C. Backends start only on connection. Shutdown closes only the bridge's own newly created adapter children; it never stops the resident Cortex host. No service, startup task, registration, package, signing or installation is performed. Library/backend/test outputs stay in their normal `bin`/`obj` folders.

Use the setup labels **Pegasus Cortex MCP** and **Pegasus Cerebral MCP**. Their configuration identifiers are `Cortex` and `Cerebral`; the examples add no display-name field.

Concrete Codex example, to apply separately when desired:

```toml
[mcp_servers.Cortex]
url = "http://127.0.0.1:47831/mcp/cortex"
startup_timeout_sec = 20
tool_timeout_sec = 60

[mcp_servers.Cerebral]
url = "http://127.0.0.1:47831/mcp/cerebral"
startup_timeout_sec = 20
tool_timeout_sec = 60
```

This is an example only; global/project Codex MCP registration is unchanged. Each product has a distinct URL and catalog while sharing the implementation and, in DevHost, a single HTTP process. A product may instead embed the same library in its own process. Existing installed per-chat adapters are unaffected until registration is deliberately switched later.

Optional synthetic network example: a host deliberately listening on a verified local interface address `192.168.50.10` can set `AllowedPeerCidrs = ["192.168.50.0/24", "100.100.2.3/32"]` for that LAN and one VPN peer. These addresses are examples, not discovered/approved networks. The library will not listen on a wildcard or configure a router, proxy or firewall. It does not implement cross-computer chat/context synchronization.

## Optional HTTPS and host-owned connection qualification

HTTP remains the unchanged loopback development default. HTTPS is explicit: an embedding product supplies `McpServerOptions.ServerCertificate` and optionally `ServerName` (otherwise the bind IP is the required SAN identity). The certificate is borrowed, must have a usable private-key handle, and must remain alive until listener disposal. The listener rejects expired/not-yet-valid, missing-key, wrong-name, CN-only and non-serverAuth certificates before use. Names are exact DNS/IP SANs; wildcard and Common Name fallback are refused. TLS 1.2/1.3 and an offline certificate context are used, without downloading certificate material.

TLS product routes return 503 until `QualifyLoopbackAsync(selectedCa)` succeeds. Supplying the borrowed `QualificationAuthority` option makes `StartAsync` run this qualification before returning; `LastTlsQualification` holds its bounded result. Failure stops startup without admitting a product operation. The real loopback client verifies the server name/SAN, current validity, explicit serverAuth EKU, chain against only the selected self-issued root CA, and the exact configured leaf DER. Certificate downloads and revocation retrieval are disabled. This is offline identity/chain/time/usage qualification, not a claim of revocation checking.

The connection check belongs to the shared MCP host/plugin. Certificate creation, key binding, public export, installation and trust belong to the existing PAO certificate domain. No duplicate certificate service or toolkit network probe is introduced. For an explicitly private-bound listener, the same selected certificate is qualified on a temporary loopback listener whose product routes stay blocked; only then is the configured listener enabled. This proves local certificate/HTTPS behavior, not LAN/VPN reachability. LAN/VPN remain off by default and have not been exercised as network tests.

DevHost accepts this optional `https` object alongside the existing loopback/product settings:

```json
{
  "https": {
    "serverName": "localhost",
    "serverCertificate": {
      "location": "CurrentUser",
      "store": "My",
      "certificateId": "<observed lowercase SHA256 server certificateId>"
    },
    "certificateAuthority": {
      "location": "LocalMachine",
      "store": "Root",
      "certificateId": "<observed lowercase SHA256 CA certificateId>"
    }
  }
}
```

References require actual 64-character lowercase SHA-256 DER identifiers; placeholders are not runnable pins. The server private-key hook remains strictly `CurrentUser/My`, matching the PAO binding contract and using the existing handle without export. This machine's shared MCP CA trust is explicitly **system-wide in `LocalMachine/Root`**, so LocalService, other services and other users can use the public CA trust. DevHost may select that public CA read-only by exact SHA-256. It cannot select a machine private key or another LocalMachine store. Existing explicit public references to `My`, `Root`, or `CertificateAuthority` in `CurrentUser` remain compatible; they are not the chosen trust scope for this machine. Store access is read-only, exact and fail-closed; the host does not install trust, change key ACLs, or silently choose another identity/store. A missing/inaccessible key or bad reference produces a bounded startup failure. Existing endpoints stay `/mcp/cortex` and `/mcp/cerebral`; deliberately configured HTTPS examples use `https://localhost:47831` with the same `Cortex`/`Cerebral` configuration identifiers and **Pegasus Cortex MCP**/**Pegasus Cerebral MCP** setup labels.

The result distinguishes `validUnderSelectedCA` from `ClientTrustObservation`. An ephemeral selected CA can qualify successfully while reporting `selected_ca_not_normally_trusted_here`. The ordinary observation is read-only system chain evaluation in this process, with downloads/revocation disabled; it does not prove that Codex or another client account/environment trusts the CA. DevHost always reports `codexConnectionVerified=false`. Actual client trust and connection adoption remain separate. Trust is per client environment, not per chat.

Use the existing PAO actions for the separately authorized certificate workflow; the host never executes them automatically:

- `certificate_export_public`: version 1 `reference`, exact `certificateId`, and `format:"pem"` or `"der"`; returns public certificate content without private keys or an implicit destination write.
- `certificate_issue_server`: the current Cortex development source adds pinned-issuer TLS leaf issuance with explicit `dnsNames` and `ipAddresses`, a fixed serverAuth purpose, and no trust installation.
- `certificate_store_bind_server`: the same development source adds explicit fixed CurrentUser/My private-key binding. Its live `binding` locator can populate `https.serverCertificate` directly; private material is not returned.
- `certificate_store_admin_trust`: this machine's chosen CA trust action, using an explicit RID with `scope:"local_machine"`, `store:"root"` through the existing same-owner one-shot UAC path owned by the coordinator. No service fallback or host-side installation is added.
- `certificate_store_trust`: its existing explicit `scope:"current_user"` API remains available for compatibility; it is not the shared MCP trust setup selected for this machine.

Verified installed state on 2026-11-21: Cortex `2.2026.11.21.02.57.A` is native-ready. The coordinator's installed PAO workflow created `pegasus-local-mcp-ca` and `pegasus-local-mcp-server`, bound the server private key in `CurrentUser/My`, and installed the public CA in `LocalMachine/Root`. The earlier UAC helper failure happened before a store action; the corrected installation and trust operation are separate coordinator evidence. One subsequent read-only configured check independently resolved both exact SHA-256 store references, passed the shared host's automatic selected-CA HTTPS qualification, observed ordinary system trust in the test process, and completed real HTTPS MCP initialization, discovery of nine Cortex tools, and `cortex_status` through one existing installed STDIO backend. Its owned listener and backend were disposed before success. Original PAO test receipt: `4f6359bbe0b44fa3878eafad8e813fae`, exit 0, with no timeout, partial result or output truncation. Actual Codex HTTPS connection/adoption and other service-account connections remain unverified. This lane did not create/export certificates, install trust, change key ACLs or registration, start Cerebral/Worker/models, or retain a listener.

The installed setup uses the normal PAO workflow: create or select a pinned CA, issue a server leaf for the selected DNS/IP names, bind the leaf into CurrentUser/My, install the public CA system-wide into LocalMachine/Root through the coordinator-owned PAO/UAC action, and supply the returned leaf locator plus that exact machine public CA locator to DevHost. The user already chose machine-wide trust; this setup does not ask again or default to per-user trust. DevHost then performs its own automatic HTTPS qualification. Certificate preparation and trust are PAO operations; the connection check belongs to the MCP host. This compatibility correction performs no installation or store mutation itself.

The existing Tests executable also has one opt-in selector: `--configured-tls <absolute-config-path>`. It consumes the same `https` certificate references and exactly one `products.cortex` entry for the stable installed Cortex executable with `arguments:["stdio"]` and `cortexMultiplex:true`. It requires `CurrentUser/My` for the server and `LocalMachine/Root` for the public CA, binds only loopback on a fresh ephemeral port instead of the configured service port, and requires ordinary system trust for its HTTPS MCP requests. It performs initialize/initialized, tools/list, `cortex_status`, session DELETE and owned disposal, then emits bounded public status and certificate pins. It does not run the broader fixture/live lanes, perform trust setup, or leave DevHost running. The configured qualification above used this selector once after explicit machine-trust confirmation; no fixture pass is substituted for its installed-certificate evidence.

HTTPS verification uses the platform [custom chain policy](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslclientauthenticationoptions.certificatechainpolicy?view=net-10.0), [SAN-only hostname matching](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509certificate2.matcheshostname?view=net-10.0), and [offline server certificate context](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstreamcertificatecontext.create?view=net-10.0). There is no accept-all callback, name-error bypass or implicit Windows trust installation.

## Protocol, limits and uncertainty

This development profile implements legacy MCP Streamable HTTP revisions 2025-03-26, 2025-06-18 and 2025-11-25: initialize/initialized, JSON POST replies, GET 405, explicit `Mcp-Session-Id`, protocol-version validation, DELETE teardown, and 404 for expired/terminated sessions followed by fresh initialization. It does not claim the newer 2026-07-28 stateless protocol. Server-initiated requests, subscription/list-change notifications, SSE replay, sampling, roots and elicitation are unavailable. Their capabilities are not advertised. Product backend initialization happens once per child connection, not per HTTP session.

Defaults: 64 sessions, 8 pending per session, 32 total, 256 KiB incoming JSON, 2.3 MB outgoing JSON, five-minute request timeout and thirty-minute idle-session expiry. STDIO bridge pending is separately capped at 32. Duplicate IDs within one active session and overload are refused; identical IDs across independent sessions are mapped safely. Each Cortex logical session gets its existing multiplex stream metadata; its existing stream FIFO/scheduler remains in force.

The optional trace callback receives event and explicit request context: product, HTTP session, stream, optional Codex `params._meta.threadId`, typed frontend request ID and, for authenticated dispatch, the verifier-derived public identity. Initialization/discovery may have no chat label; unknown remains null. Labels are caller assertions, not authentication. Tool bodies, arguments, headers, credentials and backend diagnostic bodies are not logged. DevHost's opt-in trace prints correlation fields to its console without creating a persistent log.

An individual HTTP disconnect does not cancel an admitted operation. Explicit `notifications/cancelled`, session DELETE/expiry, shutdown or request timeout requests cancellation. A cancelled or disconnected dispatched effect may remain unknown: inspect the product's original durable request/receipt before retrying. There is no automatic replay. Backend EOF fails pending requests and only a new request can reconnect; the old child must first be observed exited. One product failure or one session teardown does not close other sessions/products. There is no retry/resumption of a lost JSON response.

Cerebral's default backend binds its existing installed **service** enrollment. A separate desktop-user Worker is not silently adopted. Main/Configuration require the existing explicitly pinned desktop bindings; see `CerebralBackend/README.md`. A compiled catalog or fixture control pass is not live Worker, renderer, model or business-work acceptance.

## Verified development result, 2026-11-21

The affected solution built with zero warnings/errors. The isolated executable completed 45 focused checks using real loopback HTTP, shared child processes, deterministic backends, the actual Cerebral operator registry with a deterministic owner, and direct in-process injection. Checks cover concurrent same-ID correlation, product and Cortex-stream routing, cancellation/DELETE isolation, overload, EOF/reconnection, ambient-listener refusal and throwing/noncooperative dispatcher cleanup. These are fixture/integration transport checks, not live business-work evidence. The separate existing Cerebral operator suite passed 63 checks, including the standard `_meta` compatibility correction and rejection cases.

The final live lane completed 17 checks: two frontend Cortex sessions attached through one shared adapter to the already-running installed Cortex `2.2026.11.20.23.21.A`, discovered its actual tools and completed `cortex_status`. The real Cerebral backend initialized and returned its compiled operator catalog. A separate one-time Cerebral service-health attempt exceeded the 20-second HTTP client deadline; no successful live Worker health result is claimed and that health request was not replayed. Routine `--live` now performs catalog inspection only for Cerebral; the service-health read is explicitly separate. Test listeners and their owned children were closed naturally after each lane. No installed host was stopped or replaced.

Prototype files remain under `tools/SharedMcp/`. The only existing source changes are the narrow standard metadata admission in `src/Cerebral.Worker/OperatorMcpHost.cs` and its regressions in `tests/Cerebral.OperatorMcp.Tests/Program.cs`. No package/sign/release/service/startup/firewall/permission/registration change ran. Installed Codex chats still use their existing registration until a separately chosen adoption step. The shared library is functional development source; direct product embedding and an actual Codex-chat connection to the new URL remain integration/adoption work.

The optional HTTPS extension subsequently built with zero warnings/errors, passed 18 focused real loopback TLS/HTTPS checks including an MCP initialize against the injected fixture dispatcher, and retained the 45-check HTTP/session regression pass. Wrong CA/name/leaf pin, expiry, wrong EKU, CN-only and wildcard refusal use certificate-specific result codes. Product dispatch stays blocked before qualification. Windows Schannel rejected purely ephemeral server keys, so this fixture uses six unique nonexportable temporary user CNG key containers; all six were explicitly deleted, confirmed absent, and refused reopening before the success report. Certificates remained in memory, no certificate/trust store was changed, and no private-key export, PFX or password was used. No installed product/PAO certificate pipeline, live business work or actual Codex HTTPS adoption was exercised. Tests close their owned listeners.

## Contract evidence

Installed Codex inspected during development: `0.171.0-alpha.13`. The upstream contract was checked at [openai/codex commit 1145028](https://github.com/openai/codex/tree/1145028ae47e8119955323d38ce34122d418f951). Its [protocol mode](https://github.com/openai/codex/blob/1145028ae47e8119955323d38ce34122d418f951/codex-rs/rmcp-client/src/protocol_mode.rs) defaults to legacy 2025-06-18; its [tool-call path](https://github.com/openai/codex/blob/1145028ae47e8119955323d38ce34122d418f951/codex-rs/core/src/mcp_tool_call.rs) supplies thread/session/call metadata. This source pin is not proof of byte identity with the installed build.

[OpenAI's MCP configuration reference](https://developers.openai.com/codex/mcp) documents address-based `url` configuration and unauthenticated connection when no credential source resolves. The [MCP 2025-11-25 transport contract](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports) permits the implemented JSON-response profile and defines cancellation/session behavior. The executable tests verify this library's HTTP/backend behavior; an actual Codex-chat connection to the new URL remains a separate adoption check.
