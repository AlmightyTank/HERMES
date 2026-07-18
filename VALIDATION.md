# Static validation summary

This source was reviewed as the `0.1.0-rc.1` baseline.

## Result

- 84 release-source files
- 71 C# files and 43,643 C# lines
- 29 registered server routes
- 61 shared client/server response models
- 33 static checks passed and 0 failed

## Confirmed

- All project and props XML parses successfully.
- C# delimiters are balanced after ignoring comments and string literals.
- Client and server response models expose matching property sets for every shared model.
- Every client HTTP request enters through `HermesRequestBroker`.
- No continuous client watch entry point remains.
- Every client route has a registered server route or matching route prefix.
- Route registration order protects specific summary routes from broader prefixes.
- The HERMES server dependency graph has no detected DI cycle.
- Every explicit client assembly reference has a pre-build existence check and no reference is duplicated.
- Every `HermesClientSettings` configuration entry is declared and bound exactly once.
- No source TODO, FIXME, `NotImplementedException`, stale Alpha release label, or generated build directory remains.
- The first-load HERMES header repair spans the full settling window and exits when a real HERMES transition begins.
- Readiness string normalization uses explicit `new string(...)` construction.
- The embedded Ask HERMES icon is a valid 128×128 RGBA PNG.

## Release-candidate cleanup

- One shared `Version.props` controls project and package versions.
- Runtime logs, server status, server metadata, and diagnostics resolve the assembly informational version.
- Automatic deployment is opt-in, and Debug packages cannot overwrite the Release package name.
- `SPT_ROOT` and `SptRoot` can override the development installation path.
- Release builds exclude the unused Ragfair UI dump implementation.
- Release-source packaging excludes `bin`, `obj`, and `dist`.
- README, changelog, MIT license, release audit, and runtime checklist are included.

## Not proven in this environment

The container does not contain the .NET SDK or the local SPT 4.0.13 managed assemblies. The RC was not rebuilt or run here. Final `0.1.0` approval requires the local Visual Studio Release build and every applicable check in `RELEASE_CHECKLIST.md`.
