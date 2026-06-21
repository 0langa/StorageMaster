# StorageMaster — Development Roadmap

> **Current repository version:** v2.1.4 — hardening, repository context cleanup, and versioned release packaging.

---

## How to read this document

- **Phase** = a coherent release milestone delivering real user value
- **Milestone** = a specific deliverable inside a phase
- **Complexity** = estimated engineering effort: S (hours–1 day) | M (2–5 days) | L (1–2 weeks) | XL (2–4 weeks)
- **Impact** = `Low | Medium | High | Critical`
- ✅ = shipped

---

## Phase overview

```
v1.x    Scanner, Cleanup, CLI, Duplicates, Space Map, Scheduling, Drive Health ← SHIPPED
  │
v2.0.0  Full UI/UX overhaul, Drive Health & Storage Sentinel, release hardening ← SHIPPED
  │
v2.0.1  Responsive layout, scan ETA, loading fixes, Drive Health layout          ← SHIPPED
  │
v2.1.0  Code hardening, Settings fixes, GitHub infrastructure                    ← SHIPPED
  │
v2.2.x  Structured logging, ARM64, Drive Health hardware lab, test expansion     ← next
  │
v2.3.x  Accessibility pass, keyboard nav, screen reader support
  │
v3.0.0  [TBD]
```

---

## Phase 0 — Shipped (v1.x – v2.1.4)

All features from the v1.x and v2.x development cycle are shipped. This includes:

- Parallel BFS scanner with bounded concurrency
- Turbo Scanner (Rust/jwalk — up to 4× faster on SSDs)
- Smart Cleaner with 17 cleanup rules
- Deep scan elevated CLI worker
- Recycle Bin integration, quarantine, and audit trail
- Scan history and Delta Insights
- Interactive Space Map treemap
- Duplicate analysis (exact SHA-256, text/image/video pHash)
- CLI/headless interface and Windows Task Scheduler integration
- System tray with low-disk notifications
- Drive Health & Storage Sentinel (WMI, schema v7, per-drive alerts)
- GitHub auto-updater with digest/signature verification
- GitHub issue templates and PR template

---

## Phase 1 — v2.2.x: Observability & Platform Expansion

### M-1.1 — Structured logging (Serilog rolling file sink)

**Complexity:** S | **Impact:** High (diagnosability)

Replace `Microsoft.Extensions.Logging.Debug` with a Serilog rolling file sink:
- Path: `%LOCALAPPDATA%\StorageMaster\logs\sm-{Date}.log`
- Max 5 files × 5 MB; `Information` in release, `Debug` in debug builds
- Startup errors already land in `startup-errors.log`; structured sink unifies the rest

**NuGet:** `Serilog.Extensions.Hosting`, `Serilog.Sinks.File`

---

### M-1.2 — ARM64 support

**Complexity:** M | **Impact:** Medium

Add `aarch64-pc-windows-msvc` Rust target to CI. Update Inno Setup script for ARM64 arch.

**Files:** `.github/workflows/release.yml`, `installer/StorageMaster.iss`

---

### M-1.3 — Drive Health hardware-lab validation

**Complexity:** M | **Impact:** High (reliability)

Validate Drive Health WMI telemetry across:
- NVMe (PCIe 4.0 and 3.0)
- SATA SSD and HDD
- Removable USB drives
- Network drives (SMB)

Document explicit `Unsupported` / `Unknown` behavior per drive class. Update compatibility table.

---

### M-1.4 — ViewModel and CLI test expansion

**Complexity:** M | **Impact:** Medium (correctness coverage)

- Drive Health ViewModel unit tests (once WinUI test-host output-copy issue is resolved)
- Scheduler command tests (`--cli jobs run`, `--cli jobs list`)
- UI error state tests for Settings, Scan, and Cleanup pages
- Scan Workspace integration tests

**Blocker:** WinUI test-host `.exe` / DLL output copying not yet stable in CI.

---

### M-1.5 — Windows 10 compatibility matrix test

**Complexity:** M | **Impact:** Critical

Test on builds 17763 (1809) through 26100 (24H2). Document regressions in the table below. Focus areas: `DisplayArea.GetFromWindowId`, `SHGetKnownFolderPath`, WinUI 3 control availability, Turbo Scanner MSVC CRT requirements.

---

## Phase 2 — v2.3.x: Accessibility & Polish

### M-2.1 — Keyboard navigation & focus management

**Complexity:** M | **Impact:** High (accessibility)

Full keyboard operability: Tab/Shift+Tab, arrow keys in lists, Enter activates, Escape closes dialogs, focus lands on the first interactive element after navigation.

---

### M-2.2 — Screen reader support (MSAA / UIA)

**Complexity:** M | **Impact:** High (accessibility)

Verify and complete `AutomationProperties.Name` / `.HelpText` on all interactive controls. Scan-complete announces via `LiveSetting="Assertive"`. Validate with Windows Narrator.

---

### M-2.3 — High contrast mode

**Complexity:** S | **Impact:** Medium (accessibility)

Ensure all custom colors use `ThemeResource` tokens. Verify with Windows "High Contrast Black" and "High Contrast White".

---

### M-2.4 — Text scaling (large font)

**Complexity:** S | **Impact:** Medium (accessibility)

Test at 125%, 150%, 200% text scaling. No clipped text, no overlapping elements. 44×44 px minimum touch targets.

---

### M-2.5 — In-app what's-new panel

**Complexity:** S | **Impact:** Medium

On first launch after update: slide-in InfoBar listing new features. Dismissed once; never shown again for the same version.

---

## Engineering principles

| Principle | Enforcement |
|-----------|-------------|
| **Interfaces before implementations** | No concrete class referenced across project boundaries |
| **No deletion without confirmation** | CLI needs `--confirm`; UI needs ContentDialog |
| **Additive-only schema migrations** | Migration runner enforces; PR review gate |
| **All async, no blocking** | No `Task.Result` or `.Wait()` in application code |
| **Structured logging** | Every non-trivial operation logs with structured fields |
| **Test before merge** | CI runs `dotnet test` on every pull request |
| **Audit trail always** | Every deletion → CleanupLog row, even on error |
| **Accessible from day one** | AutomationProperties added with each new control |
| **Measure before optimizing** | Benchmark first; no speculative performance work |

---

## Compatibility matrix

| Windows version | Build | Status (v2.1) |
|----------------|-------|--------------|
| Windows 10 1809 | 17763 | Minimum supported (not yet lab-tested) |
| Windows 10 21H2 | 19044 | Not yet lab-tested |
| Windows 10 22H2 | 19045 | Primary dev target |
| Windows 11 22H2 | 22621 | Not yet lab-tested |
| Windows 11 23H2 | 22631 | Not yet lab-tested |
| Windows 11 24H2 | 26100 | CI build target |
| ARM64 (any) | — | Planned (M-1.2) |

---

## Accessibility compliance targets

By v2.3.x, StorageMaster should meet:

| Standard | Target |
|----------|--------|
| WCAG 2.1 AA | Color contrast, text alternatives, keyboard access |
| Windows UI Guidelines | Touch targets ≥ 44×44 px; font scaling; focus indicators |
| Narrator | Full navigation without mouse |
| High Contrast | All visual elements remain distinguishable |
