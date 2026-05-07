## Description

<!-- What does this PR change and why? -->

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Refactor / code quality
- [ ] Documentation
- [ ] CI / build

## Checklist

- [ ] `dotnet build StorageMaster.sln -c Release` passes
- [ ] `dotnet test StorageMaster.sln -c Release` passes (all tests green)
- [ ] `dotnet format --verify-no-changes` passes (no format drift)
- [ ] `cargo fmt --check` and `cargo test` pass (if Rust changed)
- [ ] Schema migrations are additive and version-stamped atomically
- [ ] Deletions go through `IFileDeleter` and write a `CleanupLog` row
- [ ] New UI controls have `AutomationProperties.Name` / `.HelpText`
- [ ] No `Task.Result` / `.Wait()` introduced
- [ ] Version bump in `Directory.Build.props`, `Cargo.toml`, and `StorageMaster.iss` if releasing

## Breaking changes

<!-- None / describe any breaking changes -->
