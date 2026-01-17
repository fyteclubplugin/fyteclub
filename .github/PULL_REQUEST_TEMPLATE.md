### PR title (short, imperative)
<!-- e.g. `fix(sync): handle null player in detection` or `chore(deps): bump DalamudPackager 13.1.0 → 14.0.1` -->
---

## Summary
- One-line summary of the change:
- Problem / motivation:
- High-level approach / key implementation notes (avoid huge code dumps here):

## Type of change
- [ ] Bugfix
- [ ] Feature
- [ ] Chore
- [ ] Dependency upgrade
- [ ] Documentation
- [ ] Tests
- [ ] Refactor
- [ ] CI/infrastructure
- [ ] Performance

## Related issues / PRs
- Fixes / relates to: # (issue number or link)
- Relevant PRs: (links)

---

## If this is a dependency upgrade, fill out
- Package(s) updated (name — old → new):
  - `Package.Name` — `x.y.z` → `a.b.c` (major/minor/patch)
- Does this include native binaries (e.g. `mrwebrtc.dll`)?
  - [ ] Yes — platforms tested: __________; checksums: __________
  - [ ] No
- Migration required?
  - [ ] No
  - [ ] Yes — short instructions for users to migrate:
    - ...
- Risk assessment (quick): Low / Medium / High — reason:
- Source / changelog link for the upstream release(s):

---

## Implementation notes (developer)
- Key files changed:
  - `plugin/src/...`
  - `...`
- Any notable architectural implications or follow-ups:
  - ...
- If there are behaviour changes that affect other systems, note them and why they are safe.

---

## Verification checklist (required before merge)
Run locally and check the appropriate boxes. Include commands and expected outcomes.

Build & basic checks
- [ ] `dotnet restore` — succeeds
- [ ] `dotnet build Microsoft.MixedReality.WebRTC/Microsoft.MixedReality.WebRTC.csproj -c Release` — succeeds
- [ ] `dotnet build plugin/FyteClub.csproj -c Release` — succeeds (note: requires Dalamud dev environment for full verification)
- [ ] `dotnet list package --outdated --include-transitive` — (for dependency PRs) confirm only expected updates

Automated tests
- [ ] `dotnet test plugin-tests/plugin-tests.csproj --filter "Category!=RealP2P"` — all tests pass
  - (RealP2P tests are environment-dependent and MUST be run manually or on a self-hosted runner.)
- [ ] New/updated unit tests added where applicable
- [ ] Added/updated integration tests (if applicable) and marked appropriately

Manual / in-game verification (when applicable)
- [ ] Plugin loads and does not error in Dalamud/XIVLauncher
- [ ] Create + join syncshell (basic flow)
- [ ] Penumbra integration: create temp collection, apply, and verify appearance
- [ ] Basic P2P flow with a partner client (offer/answer + small mod metadata sync)
- [ ] TURN hosting (if changed): enable, verify relay behavior and metrics
- [ ] Verify no sensitive secrets or keys are printed to logs

Security & data
- [ ] No secrets/keys committed
- [ ] Crypto changes documented and migration plan provided (if applicable)
- [ ] Any new network endpoints/relays reviewed

Docs & changelog
- [ ] CHANGELOG entry included (brief, user-facing)
- [ ] README / docs updated (if public API or config changed)

Release & rollout
- [ ] Release notes prepared (what changed, impact, upgrade steps)
- [ ] Rollout plan if high-risk (canary group, monitor metrics)
- [ ] Rollback plan documented

CI & automation
- [ ] CI job(s) added/updated to cover relevant automation
- [ ] Dependabot/renovate configured (if this is a deps PR)

---

## Testing instructions (copyable)
Commands you can run locally (adjust for your dev environment):

- Restore & build (fast)
  - dotnet restore
  - dotnet build Microsoft.MixedReality.WebRTC/Microsoft.MixedReality.WebRTC.csproj -c Release
  - dotnet build plugin/FyteClub.csproj -c Release

- Run unit tests (skip heavy RealP2P):
  - dotnet test plugin-tests/plugin-tests.csproj --filter "Category!=RealP2P"

- To run an integration/RealP2P test (requires test harness / device):
  - dotnet test plugin-tests/plugin-tests.csproj --filter "Category=RealP2P"

- Check packages (for dependency PRs):
  - dotnet list plugin package --outdated --include-transitive

Manual verification (in-game)
1. Install plugin build into your Dalamud dev env.
2. Open `/fyteclub`, create a syncshell, share invite with a second client, accept and confirm connection.
3. Verify Penumbra / Glamourer mod application (if relevant).
4. If TURN/native updates were included, verify NAT traversal & relay operation.

---

## Rollback plan (if something goes wrong)
- Steps to revert this PR quickly:
  1. Revert merge commit (or `git revert <merge-commit>`).
  2. If dependency upgrade: pin previous package versions and publish a patch release.
  3. If native DLL changed: redeploy previous DLL + validate host connectivity.
- Expected impact of rollback: (brief)
- Contacts / who to notify: @maintainer, @security

---

## Release notes (for changelog)
Provide a short user-facing summary (1–3 lines):
- Summary:
- Upgrade impact (what users/admins must do, if anything):

---

## Reviewer guidance
- Who should review: (pick)
  - WebRTC / TURN: @webrtc-owner
  - ModSystem / Penumbra: @mod-owner
  - Security / Crypto: @security-owner
  - UI / UX: @ui-owner
- Focus areas for review:
  - Security (no secrets leaked)
  - Backwards compatibility (syncshell invites, phonebook)
  - Native/packaging changes (MR‑WebRTC, DLLs)

## Labels (suggested)
- type:dependency / type:bug / type:feature / type:chore
- impact:low / impact:medium / impact:high
- needs:manual-test / needs:security-review / ci:failed

---

## Additional notes / context (optional)
- Anything else reviewers should know (design decisions, alternatives considered, links to upstream PRs/releases, test logs, screenshots).

---

> If this PR updates core runtime, native binaries, or cryptographic behavior, mark it as **Draft** until all smoke tests and at least one manual in‑game verification are completed.