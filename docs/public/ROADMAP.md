# StorageMaster — Development Roadmap

> **Current product version:** v2.2.1. Reliability hardening and remaining limits are documented in `RELIABILITY_AUDIT_2026-08-18.md`.

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
v2.1.x  Code hardening, Settings fixes, GitHub infrastructure                    ← SHIPPED
  │
v2.2.0  Quarantine restore, Turbo parity, deletion-safety hardening               ← SHIPPED
  │
v2.2.1  Reliability and data-safety overhaul                                      ← SHIPPED
  │
v2.2.x  Structured logging, hardware lab, expanded UI automation                  ← next
  │
v2.3.x  Accessibility pass, keyboard nav, screen reader support
  │
v3.0.0  [TBD]
```

---

## Phase 0 — Shipped (v1.x – v2.2.1)

All features from the v1.x and v2.x development cycle are shipped. This includes:

- Parallel BFS scanner with bounded concurrency
- Turbo Scanner (Rust/jwalk parallel native enumerator)
- Smart Cleaner with 7 direct junk sources plus 16 registered session-cleanup rules
- Deep scan elevated CLI worker
- Recycle Bin integration, quarantine, and audit trail
- Scan history and Delta Insights
- Interactive Space Map treemap
- Duplicate analysis (exact SHA-256, text/image/video pHash)
- CLI/headless interface and Windows Task Scheduler integration
- System tray with low-disk notifications
- Drive Health & Storage Sentinel (WMI, introduced with schema v7, per-drive alerts)
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

- Drive Health ViewModel/UI tests once a supported WinUI test-host strategy is adopted
- Additional scheduler/task-service integration tests (core disabled-job execution policy is covered)
- UI error state tests for Settings, Scan, and Cleanup pages
- Scan Workspace integration tests

**Blocker:** the current non-UI test assembly deliberately does not reference `StorageMaster.UI`; WinUI lifecycle tests need a separate supported host rather than a production UI reference copied into the xUnit output.

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
| **Interfaces at platform/persistence boundaries** | Core owns the contracts; UI composes concrete host/scanner services where backend selection requires them |
| **No deletion without authorization** | Interactive UI uses review/dialog intent, CLI needs `--confirm`, and enabled unattended cleanup needs current versioned consent |
| **Versioned transactional schema migrations** | Writer reservation, in-transaction version re-read, atomic version stamps, regression tests |
| **Bound critical waits** | External-process and SQLite paths use explicit cancellation/time limits where a hang could block safety or shutdown; UI work remains asynchronous |
| **Structured diagnostics** | Current code uses `ILogger` fields plus a startup crash log; a durable rolling structured sink remains M-1.1 |
| **Test before merge** | CI runs `dotnet test` on every pull request |
| **Audit failures are visible** | Cleanup returns warning/partial status when a log row cannot be written; deletion is never repeated to recreate audit |
| **Accessibility as a release target** | Add automation metadata with new controls; complete keyboard/Narrator/high-contrast validation in Phase 2 |
| **Measure before optimizing** | Benchmark first; no speculative performance work |

---

## Compatibility matrix

| Windows version | Build | Status (v2.2) |
|----------------|-------|--------------|
| Windows 10 1809 | 17763 | Configured minimum; not yet lab-validated |
| Windows 10 21H2 | 19044 | Not yet lab-tested |
| Windows 10 22H2 | 19045 | Not yet lab-tested |
| Windows 11 22H2 | 22621 | Not yet lab-tested |
| Windows 11 23H2 | 22631 | Not yet lab-tested |
| Windows 11 24H2 | 26100 | Not yet lab-tested; `windows-latest` CI image is not hardware compatibility evidence |
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
