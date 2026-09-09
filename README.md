# Rorrim

Remote desktop control for Windows. View and control an existing interactive desktop session
from a cross-platform client over gRPC.

## What it does

Rorrim attaches to the **already-logged-in desktop session** (not a new RDP connection), streams the
screen to a client, and forwards mouse/keyboard input back to the host. It's a three-process
client/server design:

```
[ Client (Avalonia, Win/Linux) ]  <== gRPC == >  [ Broker (Windows Service, LocalSystem) ]
                                                     |  finds session, injects agent
                                                     v
                                        [ Agent (in user session) ]
                                           screen capture + input injection
```

| Piece | Role |
|-------|------|
| `Rorrim.Client` | Cross-platform Avalonia UI: connect, pick a display, watch the desktop, send input. |
| `Rorrim.Server` | Windows service (LocalSystem). Hosts gRPC, owns the CA for mTLS, locates the interactive session, launches the agent. |
| `Rorrim.Agent` | Runs inside the user session; captures the desktop (GDI today, DXGI adapter available), encodes frames, injects input. |
| `Rorrim.Shared` | Proto/gRPC contracts shared by all three. |

## Current status

Working end-to-end: the client connects to the broker, the broker injects the agent into the active
desktop session, and the agent streams real frames back to the client viewer (capped at ~30 fps).
The agent reports its display list on attach, so the client's dropdown lists real monitors; picking
another display switches the capture live (no reconnect). When the host desktop is locked, the
client receives a LOCKED status and the stream resumes automatically on unlock.

Security posture:
- **mTLS** — the mTLS port requires a client certificate chaining to the broker's CA and presents a
  proper TLS server certificate (SAN + serverAuth) that clients can pin to the CA.
- **Provisioning** — `--issue-client` generates client certificates; the CA public certificate is
  exported as PEM for pinning on client machines.
- **Agent loopback** — the agent must present a random per-launch registration token to attach; the
  token is invalidated when the pairing ends.

Known gaps / work in progress:
- **Elevation** — the agent runs at Medium integrity by default; capturing *elevated* windows is not
  yet supported (High-IL injection is blocked by privilege requirements in some contexts).
- **Single viewer per session** — the broker serializes clients per host session: while one client
  is controlling, a second client is told to wait until the first disconnects.
- **Codec** — frames are currently JPEG-per-frame (with change detection so a static desktop stops
  streaming); H.264 (hardware) encoding is planned behind the same `IVideoEncoder` seam.
- **Capture path** — DXGI Desktop Duplication is preferred (GPU path, and it reports desktop-lock
  state so the client sees LOCKED during secure screens) and is validated with a blank-frame probe;
  when duplication is unavailable or delivers black output (observed on some multi-monitor setups),
  the agent automatically falls back to GDI `BitBlt`. `Rorrim.Agent self-test [displayIdx]` reports
  which path was chosen per display.
- **Agent token delivery** — the registration token is passed on the agent's command line. Other
  processes running as the same user in the same session may be able to read process command lines;
  this protects against other sessions/unprivileged local attackers, not malware already running
  as the user.

## Requirements

- Windows 10/11 (Server and Agent)
- .NET 10 SDK
- The broker must run as a **Windows service as `LocalSystem`** so it can inject the agent into a
  user's session (requires the `SeTcbPrivilege` that LocalSystem holds).

## Build

```powershell
dotnet build
dotnet test
```

## Running

### 1. Install and start the broker service

One elevated step installs (or replaces) the service and starts it — no further elevation needed
afterwards. The service runs as **LocalSystem** (required to inject the agent into a user session),
starts automatically at boot, and is restarted after a crash:

```powershell
Rorrim.Server.exe --install-service \
    --agent "C:\path\to\Rorrim.Agent.exe"

# done — the service is running; verify with:
sc.exe query "Rorrim Server"
```

Flags:
- `--agent <path>` — full path to `Rorrim.Agent.exe`.
- `--port <n>` — external gRPC/mTLS port (default `50051`).
- `--agent-port <n>` — loopback port agents attach to (default `50052`).
- `--test-mode` — allow clients to connect without a client certificate, expose a cleartext
  loopback endpoint on `--port + 2`, and permit manually-launched agents (development only).
- `--log <file>` — write broker diagnostics to a file (default `C:\Windows\Temp\rorrim-diag.log`).

Flags accept both `--flag value` and `--flag=value` forms.

Uninstall: `Rorrim.Server.exe --uninstall-service`

The agent process is created with `CREATE_NO_WINDOW` — it runs invisibly in the user session.

### Provisioning a client certificate

For the real mTLS port, issue a client certificate on the broker machine:

```powershell
Rorrim.Server.exe --issue-client my-laptop
```

This prints the PFX path (private key + certificate), the PFX password, and the CA certificate PEM.
Copy the PFX to the client machine and launch the client against the mTLS port:

```powershell
Rorrim.Client.exe https://<host>:50051 --pfx <path-to-pfx> --pfx-password <pw> --ca <path-to-ca.pem>
```

The client presents the PFX for mTLS and validates the broker's server certificate against the
pinned CA (`--ca`). `--flag=value` forms work too. The `--test-mode` loopback endpoint
(`http://localhost:<port+2>`) needs no certificates.

### 2. Launch the client

```powershell
Rorrim.Client.exe http://localhost:50053
```

The client auto-connects to the given broker address (a positional argument; TLS material comes
via `--pfx` / `--pfx-password` / `--ca`). Use the **Display** dropdown to pick a host monitor.

### 3. Agent lifecycle

The broker launches the agent automatically when a client connects (display selection from the
client's dropdown is honored). The agent connects back to the broker, streams the desktop, and
injects input. When the client session ends, the broker stops the agent it launched.

## Architecture notes

- **Session injection** — the broker pulls the user token via `WTSQueryUserToken`, duplicates it
  (`DuplicateTokenEx`), builds the user environment (`CreateEnvironmentBlock`), and creates the agent
  in the user's desktop (`CreateProcessAsUser` with `lpDesktop="winsta0\default"`). This is the
  reliable path; `CreateProcessWithTokenW` silently fails to start .NET processes.
- **Testable core** — capture and encoding are abstracted behind `IVideoSource`/`IVideoEncoder`; the
  stream orchestration, broker relay, and input mapping are all unit-tested.
- **Contracts** — the gRPC service/messages are generated into `Rorrim.Shared/Generated` and committed
  deterministically (avoids MSBuild `Grpc.Tools` incremental-codegen issues). To regenerate after
  editing `rpc.proto` (requires the `Grpc.Tools` package in the NuGet cache):

  ```powershell
  $t = "$env:USERPROFILE\.nuget\packages\grpc.tools\2.83.0\tools\windows_x64"
  & "$t\protoc.exe" --csharp_out=src\Rorrim.Shared\Generated --grpc_out=src\Rorrim.Shared\Generated `
      --plugin=protoc-gen-grpc="$t\grpc_csharp_plugin.exe" -I src\Rorrim.Shared\Contracts rpc.proto
  ```

## License

[MIT](LICENSE)
