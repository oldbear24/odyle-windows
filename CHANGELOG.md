# Changelog

What changed in each release of Odyle for Windows, newest first, written for players. Every pull request that changes the version adds its section here (AGENTS.md, Versioning). The release notes, the app's **What's new** and the dashboard's What's new show it.

## 0.15.0 · 2026-10-17

### New
- **Open in Odyle**: in the dashboard's Pair Windows app dialog, one click hands the connection link to Odyle, also while it is already running. Odyle names the server the link points at and asks before pairing; copying and pasting the link still works.

## 0.14.2 · 2026-10-17

### Changed
- A game connection that reaches your PC on two network adapters (a virtual switch, a bridge) is read once instead of twice, which saves work while you play.

### Fixed
- A rare game message starting with certain bytes no longer makes the reader resync.

## 0.14.1 · 2026-10-17

### Changed
- Nothing on the card is cut off any more: the header names only a state such as OFFLINE (Settings names your account), the cube count leaves the room name to its tooltip, and the Odyle clock wraps onto a second line.
- Party rows take two lines: the name and its Odyle, then class and level and the cubes. A stale reading says when it was last seen on its second line.

## 0.14.0 · 2026-10-17

### New
- A character the game shows without Odyle (under level 22) is now told to the server, so your rooms on the dashboard say **No Odyle yet** instead of waiting for a value.
- **Quit** asks first while a character is being read: your party would see it offline. **Settings → Window** can turn the question off.

### Fixed
- **No Odyle on … yet** also shows when the game names the character just before it sends its stats, instead of only after the next relog.

## 0.13.0 · 2026-10-17

### New
- The checklist shows the tasks of the character in game next to your account's, now that the dashboard keeps a list per character.

## 0.12.0 · 2026-10-16

### New
- The party list shows each mate's class and level as PLAYNC lists them, such as **Chanter · Lv 45**, once the dashboard has found the character.

## 0.11.0 · 2026-10-16

### New
- Odyle reads while the server is down: the card says **OFFLINE** and shows the value it reads, and the character and its Odyle are sent as soon as the server is back, with no relog.
- The party list says whether it follows live updates or refreshes every 15 seconds.

### Changed
- Settings no longer open by themselves while the PC's pairing is kept and the server is only unreachable.

## 0.10.0 · 2026-10-16

### New
- **What's new**: after an update the card says so once, and Settings → Updates lists what changed in each version.
- The release notes and the dashboard's What's new list the app's changes too.

## 0.9.0 · 2026-10-16

### New
- The Odyle clock uses the natural pool's cap your membership answer on the dashboard settles (840 or 560), and the card points out readings above it.

## 0.8.1 · 2026-10-16

### Fixed
- Odyle updates itself again. Versions 0.7.0 and 0.8.0 never looked for updates; if you still run one of them, install this version once by hand, and it keeps itself up to date from then on.

## 0.8.0 · 2026-10-16

### Changed
- **Compact mode** is the same window in the same place, cut down to your Odyle and your party's, one row each. Switch with the header button, the tray or Ctrl+Alt+O; double-click or ⤢ for the full card.

## 0.7.0 · 2026-10-16

### New
- A compact strip for play, with a shortcut and click-through (reworked in 0.8.0).

### Fixed
- A long room name no longer squeezes the card's value.

### Changed
- Releases carry semantic versions. Earlier releases were numbered 0.6.0 plus a run number; their notes are on GitHub.
