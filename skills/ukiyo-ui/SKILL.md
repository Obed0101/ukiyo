---
name: ukiyo-ui
description: Use for in-game UI — HUDs, menus, buttons, health bars, labels, pause and game-over screens — with Ukiyo.UI's immediate-mode GameUi, pixel font, themes and keyboard/pointer focus.
---

# ukiyo game UI

`Ukiyo.UI.GameUi` is immediate mode: declare widgets every tick in `Update`, draw them in `Extract`. It renders as
screen-space sprites on a virtual canvas (for example 320×180) scaled to the window, pixel-perfect (integer scale,
letterboxed) by default. Text uses a built-in 5×8 pixel font (ASCII 32–126). Reference
`src/Ukiyo.UI/Ukiyo.UI.csproj` from each host.

```csharp
using Ukiyo.UI;

// Initialize
_ui = new GameUi(context, 320, 180);                       // theme: UiTheme.Default unless you pass one

// Update
_ui.Begin(tick);
_ui.Label(new Vector2(8, 8), $"SCORE {_score}");
_ui.Bar(new UiRect(8, 20, 60, 6), _health / 100f, label: null);
if (_paused)
{
    var panel = UiRect.Anchored(_ui.Canvas, UiAnchor.Center, new Vector2(140, 80));
    _ui.Panel(panel);
    _ui.Label(new UiRect(panel.X, panel.Y + 8, panel.Width, 10), "PAUSED");
    if (_ui.Button("resume", new UiRect(panel.X + 20, panel.Y + 30, 100, 16), "RESUME")) _paused = false;
    if (_ui.Button("quit", new UiRect(panel.X + 20, panel.Y + 52, 100, 16), "QUIT")) Quit();
}
_ui.End();

// Extract (after world sprites)
_ui.Draw(frame);
```

Widgets: `Panel(rect, color?, border)`, `Rect(rect, color)`, `Label(position, text, color?, scale?, align)`,
`Label(rect, text)` (centered), `Button(id, rect, label) → bool`, `Bar(rect, value01, fill?, label?)`,
`Image(rect, spriteFrame, tint?)`. Layout: `UiRect(x, y, w, h)`, `.Inset(n)`, `.Center`,
`UiRect.Anchored(canvas, UiAnchor.BottomRight, size, margin)`. Measure text with `PixelFont.Measure(text)`
(advance 6 px, line height 10).

## Focus and activation

- `Button` returns true on the tick it activates: mouse released inside after pressing it, or Enter/Space while focused.
- Up / Down / Tab move focus through buttons in declaration order. A pointer focuses only when it moves or clicks, so
  a resting mouse never steals focus from the keyboard.
- When a menu opens, call `_ui.Focus("resume")` so keyboard players can act at once. Focus requested for a button
  that is not declared yet stays pending until it appears.
- `_ui.Focused`, `_ui.Elements` and `_ui.Describe()` expose the current UI as data — put the state in your snapshot
  or log `Describe()` to let an agent see menus without a screenshot.

## Theme

`UiTheme` is a record of linear colors and sizes: `with` it to restyle (`UiTheme.Default with { Accent = UiTheme.Srgb(0x4ECDC4) }`).
Default accent is seal vermilion `#D8432E` on ink panels. Keep text at scale 1 or 2 on a 320×180 canvas.

## Verify

`ukiyo_capture` shows the menu. In tests, drive it with `InputCollector` + `GameUi.CanvasToScreen` (see
`tests/Ukiyo.Tests/UiAndSampleTests.cs`). `samples/LanternRun` has title, HUD, pause and end menus.
