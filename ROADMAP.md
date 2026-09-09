# Rorrim Roadmap

Working notes on planned features, ordered by milestone. Checked items are done; see the README for
the current state and known limitations.

## v0.0.x — done

- [x] End-to-end remote control over the LocalSystem service path (streaming, input, display
  switching, reconnect)
- [x] mTLS with CA-chained client certs, `--issue-client` provisioning, client `--pfx/--ca` pinning
- [x] Agent registration tokens; hidden agent (`CREATE_NO_WINDOW`); lifecycle cleanup
- [x] DXGI-first capture with blank-probe + GDI fallback, self-recovery, cursor compositing
- [x] H.264 (OpenH264) default codec with JPEG fallback; per-frame codec negotiation
- [x] CI (build + test on windows-latest) and per-tag Release with self-contained packages
  (server/agent win-x64; client win-x64, linux-x64, osx-x64, osx-arm64)

## M1 — Linux broker + Linux agent on X11 (next)

Feasibility assessed: broker port is the easy ~20%; the agent is the real work, and the difficulty
is concentrated in Wayland (deferred to M2/M3). Single-machine scope: a Linux broker manages agents
on that same machine's desktop sessions only (session injection is local, as on Windows).

- [ ] Retarget `Rorrim.Server` to `net10.0` with runtime guards; move Windows specifics
      (WTS, SCM installer, `C:\Windows\Temp` log paths) behind the existing
      `ISessionProvider` / `IAgentProcessLauncher` seams
- [ ] Linux session discovery via `loginctl` (active graphical session on seat0 -> uid,
      `DISPLAY` / `WAYLAND_DISPLAY` / `XDG_RUNTIME_DIR`) replacing `WtsSessionProvider`
- [ ] Linux agent launcher: root broker starts the agent in the user's session via
      `systemd-run --uid=<user> --machine=<user>@.host` (Linux analogue of
      `WTSQueryUserToken` + `CreateProcessAsUser`); same registration-token handshake
- [ ] systemd hosting for the broker (`Microsoft.Extensions.Hosting.Systemd`), unit file +
      install command replacing `ServiceInstaller`; config under `/etc/rorrim`, state under
      `/var/lib/rorrim`
- [ ] X11 agent capture: XShm/XDamage-based `IVideoSource`, cursor via XFixes
- [ ] X11 input injection: XTest-based injector (keyboard + pointer, mapped onto the captured
      display like the Windows path)
- [ ] Linux agent DPI/multi-monitor enumeration (XRandR) wired into the display list
- [ ] Linux E2E validation (Linux desktop VM or WSLg for partial X11 testing); add linux-x64
      server/agent packages to the release workflow

## M2 — Wayland view-only

- [ ] Capture via `xdg-desktop-portal` ScreenCast (works on GNOME/KDE/wlroots) — requires D-Bus +
      PipeWire stream handling; expect a user consent prompt per session (no silent capture)
- [ ] Cursor compositing from the portal/PipeWire metadata

## M3 — Wayland input injection (per-compositor, documented as such)

- [ ] wlroots compositors (Sway/Hyprland): `wlr-virtual-pointer-unstable-v1`
- [ ] KDE Plasma 6: libei
- [ ] GNOME: not supported (no injection path) — client shows view-only status
- [ ] Capability negotiation: broker/agent advertise what the compositor allows; client surfaces it

## M4 — Codec & performance

- [ ] Hardware H.264 encode behind the existing `IVideoEncoder` seam (Media Foundation on Windows,
      VAAPI/NVENC on Linux) — current OpenH264 software encode is ~10-20 ms/frame at 1080p
- [ ] macOS H.264: H264Sharp ships no osx native — either an osx build of OpenH264 for the client,
      or VideoToolbox decode; until then macOS clients negotiate JPEG
- [ ] Adaptive bitrate / quality based on measured client throughput
- [ ] Revisit frame pacing: GDI change-detection + OpenH264 rate-control interplay on tiny
      cursor-only deltas (observed ~1 fps under synthetic 3 px jiggles)

## M5 — Security hardening

- [ ] Agent registration token currently rides on the process command line (readable by same-user
      processes in the same session); move to an inherited handle, stdin handshake, or file with
      restrictive ACL
- [ ] Elevated (High-IL) agent injection on Windows — currently blocked by privilege requirements
      in some contexts; agent runs at Medium IL
- [ ] DXGI cursor path (duplication PointerInfo) so lock state + cursor are correct on the GPU
      capture path
- [ ] Mutual TLS on the agent loopback channel (currently localhost + token only)

## M6 — Product

- [ ] Multiple concurrent viewers per session (fan-out the agent stream; currently serialized)
- [ ] File transfer / clipboard sync
- [ ] Installer experience (signed binaries, .deb/.rpm for Linux client, winget/MSI for Windows)
