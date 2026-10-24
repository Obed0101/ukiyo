import AppKit

/// The standard Mac menu bar. Edit uses the responder chain, so copy and paste reach the web view.
enum MainMenu {
    static func build(target: AppDelegate) -> NSMenu {
        let bar = NSMenu()
        bar.addItem(submenu(appMenu(target)))
        bar.addItem(submenu(editMenu()))
        bar.addItem(submenu(viewMenu(target)))
        let window = windowMenu()
        bar.addItem(submenu(window))
        NSApp.windowsMenu = window
        let help = helpMenu(target)
        bar.addItem(submenu(help))
        NSApp.helpMenu = help
        return bar
    }

    private static func submenu(_ menu: NSMenu) -> NSMenuItem {
        let item = NSMenuItem()
        item.submenu = menu
        return item
    }

    private static func item(_ title: String, _ action: Selector?, _ key: String = "", target: AnyObject? = nil,
                             modifiers: NSEvent.ModifierFlags = .command) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.keyEquivalentModifierMask = key.isEmpty ? [] : modifiers
        item.target = target
        return item
    }

    private static func appMenu(_ target: AppDelegate) -> NSMenu {
        let menu = NSMenu(title: "Ukiyo")
        menu.addItem(item("About Ukiyo", #selector(NSApplication.orderFrontStandardAboutPanel(_:))))
        menu.addItem(.separator())
        menu.addItem(item("Open Config Folder…", #selector(AppDelegate.openConfigFolder(_:)), ",", target: target))
        menu.addItem(.separator())
        let services = NSMenuItem(title: "Services", action: nil, keyEquivalent: "")
        let servicesMenu = NSMenu(title: "Services")
        services.submenu = servicesMenu
        NSApp.servicesMenu = servicesMenu
        menu.addItem(services)
        menu.addItem(.separator())
        menu.addItem(item("Hide Ukiyo", #selector(NSApplication.hide(_:)), "h"))
        menu.addItem(item("Hide Others", #selector(NSApplication.hideOtherApplications(_:)), "h", modifiers: [.command, .option]))
        menu.addItem(item("Show All", #selector(NSApplication.unhideAllApplications(_:))))
        menu.addItem(.separator())
        menu.addItem(item("Quit Ukiyo", #selector(NSApplication.terminate(_:)), "q"))
        return menu
    }

    private static func editMenu() -> NSMenu {
        let menu = NSMenu(title: "Edit")
        menu.addItem(item("Undo", Selector(("undo:")), "z"))
        menu.addItem(item("Redo", Selector(("redo:")), "z", modifiers: [.command, .shift]))
        menu.addItem(.separator())
        menu.addItem(item("Cut", #selector(NSText.cut(_:)), "x"))
        menu.addItem(item("Copy", #selector(NSText.copy(_:)), "c"))
        menu.addItem(item("Paste", #selector(NSText.paste(_:)), "v"))
        menu.addItem(item("Select All", #selector(NSText.selectAll(_:)), "a"))
        return menu
    }

    private static func viewMenu(_ target: AppDelegate) -> NSMenu {
        let menu = NSMenu(title: "View")
        menu.addItem(item("Reload", #selector(AppDelegate.reload(_:)), "r", target: target))
        menu.addItem(.separator())
        menu.addItem(item("Actual Size", #selector(AppDelegate.actualSize(_:)), "0", target: target))
        menu.addItem(item("Zoom In", #selector(AppDelegate.zoomIn(_:)), "=", target: target))
        menu.addItem(item("Zoom Out", #selector(AppDelegate.zoomOut(_:)), "-", target: target))
        menu.addItem(.separator())
        menu.addItem(item("Toggle Full Screen", #selector(NSWindow.toggleFullScreen(_:)), "f", modifiers: [.command, .control]))
        return menu
    }

    private static func windowMenu() -> NSMenu {
        let menu = NSMenu(title: "Window")
        menu.addItem(item("Minimize", #selector(NSWindow.performMiniaturize(_:)), "m"))
        menu.addItem(item("Zoom", #selector(NSWindow.performZoom(_:))))
        menu.addItem(.separator())
        menu.addItem(item("Bring All to Front", #selector(NSApplication.arrangeInFront(_:))))
        return menu
    }

    private static func helpMenu(_ target: AppDelegate) -> NSMenu {
        let menu = NSMenu(title: "Help")
        menu.addItem(item("Studio Guide", #selector(AppDelegate.openGuide(_:)), target: target))
        menu.addItem(item("Show Server Log in Finder", #selector(AppDelegate.showLog(_:)), target: target))
        return menu
    }
}
