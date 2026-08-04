# The process table: why there is no TreeDataGrid dependency

## Decision

The process tree is built on core Avalonia primitives, not on `Avalonia.Controls.TreeDataGrid`.

## Why

Two independent reasons.

### 1. TreeDataGrid is now a commercial product

As of the Avalonia 12.x line, `Avalonia.Controls.TreeDataGrid` is part of Avalonia Accelerate and
requires a purchased licence key. Building against it without one fails:

```
AvaloniaUI.Licensing error AVLIC0001: No valid AvaloniaUI license keys found
for required commercial products: "Avalonia.Controls.TreeDataGrid".
```

This is not avoidable by pinning an older version — current 11.3.x builds enforce the same gate, and
the 11.3.2 nuspec carries no `license` element at all. Only long-abandoned 11.0.x-era builds predate
the change, which is not a sound foundation.

Core Avalonia itself (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`,
`Avalonia.Fonts.Inter`) remains freely licensed and is unaffected.

### 2. It would not have done the job anyway

The macOS implementation does _not_ use a stock table either. `NSOutlineView` could not produce the
Process Explorer layout, so `App/MainWindow/ProcessOutlineView.swift` is ~1,800 lines of bespoke
AppKit implementing:

- a frozen process-name pane beside independently horizontally-scrolling metric columns,
  synchronised through a custom `NSClipView`
- live column resize and drag-reorder with persistence
- per-row background colouring driven by `ProcessFlags`
- immediate (non-delayed) tooltips for truncated cells
- type-to-select

TreeDataGrid does not offer the frozen-pane split-scroll, which is the defining visual
characteristic of the layout. Adopting it would have meant fighting the control for the one feature
that matters most, then hand-writing the rest.

## Consequence

We write a virtualised tree-table over Avalonia primitives. This is real work, and it is the single
largest UI risk in the project — it should be prototyped before the rest of the UI is committed to.

If the project later acquires an Avalonia Accelerate licence, this decision is worth revisiting only
for the non-frozen secondary tables (lower pane, threads list, TCP/IP list), where the split-scroll
requirement does not apply.
