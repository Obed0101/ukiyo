# Ukiyo.app

The Studio as a native Mac app: Swift + AppKit + WKWebView, built with the system `swiftc` (no Xcode project, no
dependencies). It is a window onto `studio/`; the UI stays files in `studio/`.

```sh
scripts/mac-app.sh               # build artifacts/mac/Ukiyo.app, install to /Applications/Ukiyo.app
scripts/mac-app.sh --no-install  # build only
```

- **Launch**: reuses a running Studio (`~/.ukiyo/studio/<pid>.json`), otherwise starts `bun <repo>/studio/src/server.ts`
  on 127.0.0.1, waits until it answers and loads it. The server it started is stopped on quit. If it fails, the window
  shows the reason, the log tail and Retry. Log: `~/Library/Logs/Ukiyo/studio.log`.
- **Repo**: the checkout that built the app is baked in. Override with `~/.config/ukiyo/app.json`:
  `{"repo": "/path/to/ukiyo", "reuse": false}` (`reuse: false` always starts its own server).
- **bun**: `/opt/homebrew/bin/bun`, `~/.bun/bin/bun`, `/usr/local/bin/bun`, then `PATH`.
- **Menus**: Cmd+R reload, Cmd+, opens `~/.config/ukiyo/studio` in Finder, zoom, full screen, standard Edit menu.
  No web inspector. Links outside the Studio open in the browser.
- **Window**: transparent titlebar, the page draws behind it. The app sets `data-host="mac"` on `<html>`
  (`studio/web/studio.css` uses it to clear the window buttons) and lets the top bar drag the window.
- Ad-hoc signed, host architecture only, macOS 13+. Rebuild after moving or removing the checkout.
