# StorageMaster v2.0.0 UI/UX Overhaul

Date: 2026-05-07

## Current UI Problems Found

- Shell navigation was flat, with all major workflows presented as peers.
- Card padding, corner radius, helper text opacity, and status colors were repeated page by page.
- Dashboard needed clearer command-center hierarchy and stronger drive status cards.
- Drive Health looked like raw telemetry rather than a monitoring view.
- Scan flow lacked step framing and runtime metrics beyond basic counters.
- Space Map tiles were generated as ad-hoc buttons in code-behind.
- Settings already had a category hub, but no reusable SettingsCard-style primitive.
- Cleanup and Duplicates safety behavior existed, but page-level copy did not make review-first deletion prominent enough.

## Target Direction Implemented

- Shared visual tokens and styles under `src/StorageMaster.UI/Styles/`.
- Reusable UI primitives under `src/StorageMaster.UI/Controls/`.
- Grouped Windows 11-style navigation with a global status strip.
- Mica Alt backdrop with runtime fallback.
- Dashboard and Drive Health cards with gauges and severity badges.
- Guided Scan page with mode cards, elapsed time, speed, errors, and workspace handoff.
- New Scan Workspace route for persisted scan context.
- Dedicated Space Map tile control with centralized tooltip, focus, hover, automation, and color logic.
- Safety banners on Duplicates and Cleanup.

## Affected Views

- Shell: `MainWindow`
- Dashboard: `DashboardPage`, `DashboardViewModel`
- Scan: `ScanPage`, `ScanViewModel`
- Scan Workspace: `ScanWorkspacePage`, `ScanWorkspaceViewModel`
- Space Map: `SpaceMapPage`, `TreemapTileControl`
- Drive Health: `DriveHealthPage`
- Cleanup, Duplicates, Settings: shared safety/card polish

## QA Checklist

- Build Release solution.
- Run test suite.
- Manually verify Dashboard, Scan, Scan Workspace, Space Map, Drive Health, Cleanup, Duplicates, and Settings in light/dark mode.
- Verify Windows 10 fallback when Mica is unavailable.
- Verify high contrast text on badges and status chips.
- Verify reduced-motion setting before adding more animation.
- Verify large scan sessions keep Results and workspace lists paged.

## Open Visual QA

Rendered screenshot capture is still required before release. Source audit and build validation are complete, but final visual QA needs the running WinUI shell on target Windows 10/11 machines.
