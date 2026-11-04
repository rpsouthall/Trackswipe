# Anchor Dock — Privacy Policy

**Last updated:** 28 July 2026
**Applies to:** Anchor Dock for Windows (Microsoft Store package and portable release), published by
**ajaykontham**.

Anchor Dock is a floating application dock for Windows. It runs entirely on your PC. There are **no
accounts, no sign-in, no telemetry, no analytics, no advertising and no tracking of any kind**.

This document is the privacy policy referenced from the app's **Settings ▸ About** page and from the
Microsoft Store listing.

---

## 1. Personal information we collect

**None.** Anchor Dock does not collect, transmit, sell, share or otherwise disclose any personal
information to the developer or to any third party. There is no server operated by the developer for
this app, and no data of any kind is sent to one.

## 2. Data the app stores on your device

Everything Anchor Dock saves stays on your own PC, in your own user profile. Nothing in this list
leaves your device.

| What | Where | Why |
|---|---|---|
| Your docks, pinned items, layout, keyboard shortcuts and settings | `dock.json` in the app's per-user data folder | So your dock is the same next time you sign in |
| Cached website icons (favicons) for pinned web links | An `IconCache` folder alongside `dock.json` | So a site's icon is fetched at most once and still shows offline |
| A diagnostic log (`anchor.log` in your temp folder, overwritten on each start) | Your Windows temp folder | Local troubleshooting only — it records app events such as which pinned item was launched and any unexpected error |

The data folder is `%AppData%\Anchor` for the portable release. For the Microsoft Store package,
Windows redirects it into the app's own per-user package storage, which Windows deletes when you
uninstall the app.

Pinned items are things **you** chose to add, so `dock.json` contains the file paths, folder paths
and web addresses you pinned, and any custom names or icons you gave them. Anchor Dock does not read
the *contents* of your files: launching a pinned item hands it to Windows exactly as double-clicking
it in File Explorer would, and a folder fly-out lists a folder's entry *names* only.

You can export this data at any time (**Settings ▸ General ▸ Backup**) and delete it at any time by
removing the data folder or uninstalling the app.

## 3. Network access

Anchor Dock makes network requests in exactly two situations, and in no others.

1. **Website icons for pinned web links.** When you pin a web link, the app requests that site's
   icon — first directly from the site itself (`https://<site>/favicon.ico`). Only if that fails
   does it fall back to DuckDuckGo's public icon service (`https://icons.duckduckgo.com/ip3/<site>.ico`),
   which receives only the site's domain name and no information about you. The result is cached
   locally so the request happens at most once per site. **If you never pin a web link, the app makes
   no network requests at all.**

2. **An optional update check — portable release only, and off by default.** In the portable
   (non-Store) release, **Settings ▸ General ▸ Check for updates** can be switched on. When it is,
   the app asks the public GitHub releases API once at startup whether a newer version exists. No
   account, token or identifier is sent beyond the HTTP request itself, nothing is uploaded, and
   nothing is ever downloaded or installed automatically. **This feature does not exist in the
   Microsoft Store version**, which is updated by the Microsoft Store itself.

Launching a pinned web link opens it in your default browser; from that point onward your browser's
own privacy policy applies.

## 4. Children's privacy

Anchor Dock is a general-purpose desktop utility. It is not directed at children, and since it
collects no personal information from anyone, it collects none from children.

## 5. Permissions the app requests

The Microsoft Store package declares one restricted capability, **`runFullTrust`**. Anchor Dock is a
launcher: it needs full trust to start the applications, files, folders and links you pin, and to
read their icons from Windows, which a sandboxed app cannot do. It declares no other capability, and
it requires no administrator rights.

## 6. Security

Because no personal information is collected or transmitted, there is none held by the developer to
secure. The data described in section 2 is stored in your Windows user profile and protected by your
Windows account. Network requests described in section 3 use HTTPS.

Note that `dock.json` describes programs that can be started, so treat it as you would any file that
can launch software on your PC.

## 7. Changes to this policy

If this policy changes, the updated version will be published at this URL and the "Last updated"
date above will change. Material changes will also be noted in the app's
[CHANGELOG](../CHANGELOG.md).

## 8. Contact

Questions or privacy concerns:

- **Issues:** <https://github.com/framedparadox/anchor-dock/issues>
- **Source code:** <https://github.com/framedparadox/anchor-dock>

---

*Anchor Dock is free and open source software released under the [MIT License](../LICENSE). This
policy covers the app as distributed by the publisher named above; a third party redistributing a
modified build is responsible for its own privacy practices.*
