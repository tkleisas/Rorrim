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
| `Rorrim.Agent` | Runs inside the user session; captures the desktop (DXGI with GDI fallback), encodes frames, injects input. |
| `Rorrim.Shared` | Proto/gRPC contracts shared by all three. |

## Current status

Working end-to-end: the client connects to the broker, the broker injects the agent into the active
desktop session, and the agent streams real frames back to the client viewer. Input forwarding is
wired.

Known gaps / work in progress:
- **Elevation** — the agent runs at Medium integrity by default; capturing *elevated* windows is not
  yet supported (High-IL injection is blocked by privilege requirements in some contexts).
- **Agent lifecycle** — repeated client reconnects can spawn extra agent processes (cleanup/reuse is
  not yet implemented).
- **Codec** — frames are currently JPEG-per-frame; H.264 (hardware) encoding is planned behind the
  same `IVideoEncoder` seam.

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

The broker installs itself as a **LocalSystem** Windows service (run from an elevated prompt):

```powershell
Rorrim.Server.exe --install-service \
    --agent "C:\path\to\Rorrim.Agent.exe"

sc.exe start "Rorrim Server"
```

Flags:
- `--agent <path>` — full path to `Rorrim.Agent.exe`.
- `--port <n>` — external gRPC/mTLS port (default `50051`).
- `--agent-port <n>` — loopback port agents attach to (default `50052`).
- `--test-mode` — allow clients to connect without a client certificate (development only).
- `--log <file>` — write broker diagnostics to a file.

Uninstall: `Rorrim.Server.exe --uninstall-service`

### 2. Launch the client

```powershell
Rorrim.Client.exe http://localhost:50053
```

The client auto-connects to the given broker address. Use the **Display** dropdown to pick a host
monitor. *(The `--test-mode` endpoint `http://localhost:50053` is a loopback cleartext port; the
production client connects to the mTLS port `https://<host>:50051` with a client certificate.)*

### 3. Agent lifecycle

The broker launches the agent automatically when a client connects. The agent connects back to the
broker, streams the desktop, and injects input.

## Architecture notes

- **Session injection** — the broker pulls the user token via `WTSQueryUserToken`, duplicates it
  (`DuplicateTokenEx`), builds the user environment (`CreateEnvironmentBlock`), and creates the agent
  in the user's desktop (`CreateProcessAsUser` with `lpDesktop="winsta0\default"`). This is the
  reliable path; `CreateProcessWithTokenW` silently fails to start .NET processes.
- **Testable core** — capture and encoding are abstracted behind `IVideoSource`/`IVideoEncoder`; the
  stream orchestration, broker relay, and input mapping are all unit-tested.
- **Contracts** — the gRPC service/messages are generated into `Rorrim.Shared/Generated` and committed
  deterministically (avoids MSBuild `Grpc.Tools` incremental-codegen issues).

## License

TBD
