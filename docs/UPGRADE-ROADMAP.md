# FyteClub — Dependency & Upgrade Roadmap

Status
- CI: newly added (basic Windows job + gated RealP2P manual job) — local commit present.
- Critical fixes staged: Ed25519 correctness + unit tests (needs push & PR).
- No CI previously; Dependabot + PR templates added locally (needs push & PR).
- High‑priority upgrades identified: Dalamud SDK, Penumbra/Glamourer, MR‑WebRTC (native).

Purpose
- Provide a safe, repeatable plan to upgrade platform and third‑party dependencies.
- Minimize user impact and risk by using small, testable PRs + gated rollout.
- Ensure robust automated checks and explicit manual validation for runtime/native changes.

Goals & success criteria
- Primary: plugin compiles and passes unit tests on CI; in‑game smoke flows remain functional.
- Secondary: automated Dependabot PRs, clear PR checklist, and guarded integration testing.
- Acceptance: green CI (unit), documented manual verification procedures, and one canary release with no regressions.

High‑level upgrade order (why / risk)
1. Prepare automation and observability (CI, Dependabot, PR templates) — very low risk.
2. Update test tooling (xUnit, test SDK) and non‑runtime libraries — low risk.
3. Dalamud SDK — high priority (host API); medium risk.
4. Penumbra / Glamourer bindings — medium risk (IPC surfaces).
5. Microsoft.MixedReality.WebRTC (managed + native) — high risk (native DLLs & runtime).
6. Other NuGet libraries (NNostr, crypto libs, System.*) — low/medium risk.
7. Security hardening (PBKDF2, AES‑GCM AAD, logging) — medium risk (requires migration notes).

Quick commands (run locally to reproduce checks)
- list outdated packages:
```/dev/null/commands.sh#L1-1
dotnet list plugin package --outdated --include-transitive
```

- build (vendor project and plugin; plugin may require Dalamud host files):
```/dev/null/commands.sh#L1-4
dotnet build Microsoft.MixedReality.WebRTC/Microsoft.MixedReality.WebRTC.csproj -c Release
dotnet build plugin/FyteClub.csproj -c Release
```

- run unit tests (skip heavy RealP2P):
```/dev/null/commands.sh#L1-2
dotnet test plugin-tests/plugin-tests.csproj --filter "Category!=RealP2P"
```

- basic PR dev flow:
```/dev/null/commands.sh#L1-4
git checkout -b chore/upgrade-dalamud
# make changes
git add -A && git commit -m "chore(deps): bump Dalamud SDK x.y → z.w"
git push --set-upstream origin chore/upgrade-dalamud
# open a draft PR (use gh or GitHub UI)
```

Pre-upgrade checklist (must be completed before bumping a major runtime dependency)
- [ ] CI job that runs unit tests on Windows exists and is required for merges.
- [ ] Dependabot configured to open PRs for NuGet in `/plugin` and `/plugin-tests`.
- [ ] PR template + dependency issue template present to standardize reviews.
- [ ] Baseline: record current `dotnet list package --outdated` output and a successful unit-test run.
- [ ] Add “requires-host” test trait for tests needing Dalamud so CI can reliably filter.

Detailed upgrade plan (per dependency)
- A. Dalamud SDK
  - Scope: update Sdk version, handle API renames, and IPC signature changes.
  - Steps:
    1. Create branch `chore/upgrade/dalamud-<minor>`.
    2. Bump SDK and run `dotnet build`; fix compile errors.
    3. Update IPC subscriber names/signatures; add compatibility shims if needed.
    4. Add unit tests for IPC boundaries and update existing tests.
    5. Manual in‑game validation (load/unload, `/fyteclub` UI, create/join syncshell).
    6. Draft PR + reviewers: @maintainer, @integration-owner.
  - Validation: full unit test pass, in‑game smoke pass.
  - Rollback: revert PR, publish patch. Document migration steps for users.

- B. Penumbra / Glamourer
  - Scope: update project references and IPC usage.
  - Special tests: temporary collection creation, glam application.
  - Risk: medium — ensure temporary collection cleanup is correct.

- C. Microsoft.MixedReality.WebRTC (managed + native)
  - Scope: update managed wrapper and native `mrwebrtc.dll`.
  - Steps:
    1. Test on local dev machine first (native DLLs must match managed binding).
    2. Validate data-channel creation, buffer/backpressure, reconnect logic.
    3. Run high-throughput multi-channel tests (RealP2P).
  - Deployment: require canary rollout to small set of users; monitor reconnect rates and file transfer success.
  - Rollback: redeploy previous `mrwebrtc.dll` and corresponding managed assembly.

- D. Other NuGets (NNostr, crypto libs, test tooling)
  - Do in small PRs; run unit tests and fix API changes.
  - Prefer minor/patch upgrades; major upgrades require explicit PR with migration notes.

Security hardening (must be included with relevant PRs)
- Increase PBKDF2 iterations (or migrate to Argon2); document migration and performance impact.
- Ensure AES‑GCM uses unique nonces and include associated data (file/player metadata).
- Audit logs: redact or mask any secret, encryption key, or TURN password in logs — make them Debug-level only.
- For any key format changes (e.g., Ed25519), provide detection & migration steps — *do not* silently accept old formats.

Ed25519 migration note (important)
- If user configuration stores Ed25519 private keys in an older/incompatible format, those keys will not be usable after a correct Ed25519 implementation is introduced.
- Detection (local): private key length != 32 bytes or `Ed25519Identity.RunSelfTest()` fails.
- Recommendation:
  - Detect & warn users in UI (show brief instructions).
  - Provide an explicit “regenerate identity / reissue invite” flow and document UX steps.
  - For server/phonebook records, require re-signing or re-issuing tokens.

Testing matrix (minimum)
- OS: Windows (primary), Linux (secondary), macOS (spot check)
- .NET SDK: verify on the project's target SDK and one LTS (e.g., 7.x & 9.x as available)
- Test categories:
  - Unit: run on CI (fast)
  - Integration (RealP2P): run on self-hosted runner or developer machine (manual)
  - End‑to‑end (Penumbra apply, syncshell): manual verification in-game

CI & gating rules (must-haves)
- All PRs must pass: `build-and-test` job (unit tests) before merge.
- Any dependency PR that touches Dalamud/MR‑WebRTC must be draft until manual smoke tests pass.
- RealP2P integration tests run on self-hosted runner (workflow_dispatch).
- Dependabot PRs: auto‑label `dependencies`; small upgrades may be auto-merged if tests pass (policy).

Canary / rollout strategy
1. Merge to `main` only when unit tests pass and manual smoke test is green.
2. Publish a canary release to a limited set of trusted users (in repo/release notes).
3. Monitor logs for 72 hours for:
   - Increased connection failures
   - Increased file transfer retries / reconnections
   - Crash reports / exception spikes
4. If stable, proceed to general release.

Rollback plan (fast recovery)
- Revert the merge commit immediately.
- If native binary is the cause, republish previous artifact and advise users to re-install.
- Open an incident and notify maintainers + high‑risk users.

PR & reviewer guidance (short)
- PR title format: `chore(deps): bump <package> <old> → <new>`
- Use small, focused PRs for each dependency.
- Include: test matrix, local verification commands, and any migration steps.
- Required reviewers depending on area:
  - WebRTC/native: `@webrtc-owner`
  - ModSystem/Penumbra: `@mod-owner`
  - Security/crypto: `@security-owner`

Estimated effort (very approximate)
- CI baseline + Dependabot: 0.5–1 day
- Test tooling upgrades: 0.5–1 day
- Dalamud SDK upgrade and fixes: 1–3 days
- Penumbra/Glamourer update + validation: 1–2 days
- MR‑WebRTC native upgrade + end‑to‑end testing: 3–7 days (device testing required)

Immediate next actions I will take (with your approval / credentials)
- Push the local branches and open draft PRs for:
  - Ed25519 fix + tests
  - CI workflow + Dependabot/PR templates
- Create tracked issues for:
  - `upgrade/dalamud-sdk` (with checklist)
  - `upgrade/mr-webrtc-native` (with test plan)
  - `security/log-redaction-and-crypto-hardening`
- Start a targeted, small dependency bump (test tooling) to prove CI + upgrade flow.

Appendix — PR checklist (copy into PR description)
- Build: `dotnet build` (vendor + plugin)
- Unit tests: `dotnet test --filter "Category!=RealP2P"`
- Manual smoke: plugin load, create/join syncshell, basic P2P metadata sync
- Docs: CHANGELOG entry + migration notes (if applicable)
- Security: secrets/logging audit complete

---

If you'd like, I will now:
- Push the local branches and open draft PRs (I attempted to push but do not have remote credentials — I can provide exact commands for you or push if you provide a token).
- Open the dependency issues and assign priorities.
Tell me whether you want me to (A) provide the ready‑to‑paste PR bodies + `gh`/git commands for you to run, or (B) proceed to open the first dependency branch (Dalamud SDK) and attempt a compile & fix cycle locally (I will stage the WIP changes as a branch and PR).