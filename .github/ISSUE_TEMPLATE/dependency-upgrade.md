---
name: Dependency upgrade
about: Bump a NuGet or native dependency
title: "deps: <package> <old> -> <new>"
labels: ["dependencies"]
---

**Package:** 
**Current -> target version:** 
**Why upgrade:** (security / bugfix / feature / just staying current)

**Anything that could break:** (native binaries, API changes, etc.)

**Checklist**
- [ ] `dotnet build` passes
- [ ] `dotnet test plugin-tests/plugin-tests.csproj --filter "Category!=RealP2P"` passes
- [ ] Tested in-game
