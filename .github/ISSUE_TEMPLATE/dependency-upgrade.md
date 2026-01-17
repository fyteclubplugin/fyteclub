---
name: Dependency upgrade
about: Request and track a dependency upgrade (NuGet or native). Include validation steps, rollback plan, and risk assessment.
title: "deps: <package> — <old> → <new>"
labels: ["dependencies", "needs-triage"]
assignees: []
---

## Summary
- **Package**: `package-name`
- **Current version**: `x.y.z`
- **Target version**: `a.b.c`
- **Scope** (which project(s)): e.g. `plugin`, `plugin-tests`, `Microsoft.MixedReality.WebRTC` (native)

## Motivation
- Why upgrade? (security / bugfix / performance / feature / maintenance)
- Linked upstream release notes / PRs:
  - <link-to-upstream-release>

## Risk assessment (select)
- [ ] Low — patch/non‑breaking
- [ ] Medium — minor with possible API changes
- [ ] High — major, native binaries, or runtime/ABI changes

If **High**, include additional rollout/monitoring instructions below.

---

## Upgrade plan (step-by-step)
1. Create a dedicated branch: `chore/upgrade/<package>-to-<version>`
2. Update package reference(s) and commit with a clear message.
3. Run CI (unit tests + static checks).
4. Address compile/test failures and update call sites (if API changed).
5. Perform manual/in‑game smoke tests (see Validation section).
6. Publish PR as **Draft** until all checks (automated + manual) pass.
7. Merge and perform canary rollout (if high risk).
8. Monitor for regressions for 48–72 hours; roll back if necessary.

## Validation / Verification checklist (required)
- Build & restore
  - [ ] `dotnet restore`
  - [ ] `dotnet build Microsoft.MixedReality.WebRTC/Microsoft.MixedReality.WebRTC.csproj -c Release` (vendor project)
  - [ ] `dotnet build plugin/FyteClub.csproj -c Release` (note: may require Dalamud dev files)
- Automated tests
  - [ ] `dotnet test plugin-tests/plugin-tests.csproj --filter "Category!=RealP2P"` — all passing
  - [ ] Add/update unit tests for any behavioral changes
- Manual / integration tests (always run for medium/high risk)
  - [ ] Plugin loads/unloads in Dalamud dev environment
  - [ ] Create + join a syncshell (happy path)
  - [ ] Penumbra integration: temporary collection apply/revert
  - [ ] Basic P2P handshake + metadata sync (small payload)
  - [ ] If TURN/native changed: verify NAT traversal end‑to‑end
- Security / data integrity
  - [ ] No secrets printed in logs
  - [ ] Crypto-related changes reviewed by security owner
- Observability
  - [ ] Metrics/logging sanity checks (no new error spikes)
  - [ ] Add lightweight telemetry or log markers for the first rollout

## Testing commands (copy/paste)
- Check outdated packages (local)
  - `dotnet list plugin package --outdated --include-transitive`
- Local quick build & tests
  - `dotnet restore`
  - `dotnet build plugin/FyteClub.csproj -c Release`
  - `dotnet test plugin-tests/plugin-tests.csproj --filter "Category!=RealP2P"`
- Manual in-game smoke (developer machine)
  - Build & copy plugin to Dalamud dev folder, start XIVLauncher, verify flows above

## Rollback plan (must be documented before merge)
- Quick revert: `git revert <merge-commit>` and publish a patch release
- If native DLL introduced a regression: replace with prior DLL + restart
- Notify users/maintainers and revert canary group immediately

## Acceptance criteria (for PR)
- [ ] All non-RealP2P unit tests pass in CI
- [ ] No new critical/exception errors in smoke tests
- [ ] CHANGELOG entry and migration notes added (if applicable)
- [ ] Rollout & rollback plan documented
- [ ] PR has reviewer from relevant teams:
  - WebRTC / native: @webrtc-owner
  - ModSystem / Penumbra: @mod-owner
  - Security / Crypto: @security-owner

## Post-merge monitoring
- Monitor logs and metrics for 72 hours:
  - Error rate
  - P2P connection failures / TIMEOUTs
  - TURN relay failures (if applicable)
- Escalation contacts:
  - Primary maintainer: @maintainer
  - Security: @security-owner

## Notes / context (add links & rationale)
- Upstream changelog: <link>
- Related issues/PRs: #...
- If this upgrade is a major bump, list all affected public APIs and user-visible changes here.

---