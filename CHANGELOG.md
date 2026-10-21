# Changelog

What changed in each release of Odyle for Windows, newest first, written for players. Every pull request that changes the version adds its section here (AGENTS.md, Versioning). The release notes, the app's **What's new** and the dashboard's What's new show it.

## 0.24.0 · 2026-10-21

### New
- **A character near its Odyle cap stands out**: on the card the clock line and the Odyle value turn orange two days before the natural pool is full (**Nearly full**), and red once it is full or fills within the hour (**Full within the hour**). In **Your characters** those rows are coloured the same way.

## 0.23.0 · 2026-10-18

### New
- **Fade the card while the game is in front** (Settings → Window): while you play, the card turns see-through so the game shows through it; point at it to see it fully.
- **Show the card with the game, hide it after** (Settings → Window): the card comes up when the game starts, without taking the focus from it, and goes to the tray when the game closes.

## 0.22.0 · 2026-10-18

### New
- **The tray icon shows the reader**: a dot in its corner in the card's colours, green while it reads, amber while it waits or its reading is old, red when it cannot read; none while the reader is stopped.
- **Your characters** on the card: with two or more characters, open the list under the reading to see each one's Odyle, cubes and when it was read, the fullest first. It also opens by itself while the game switches characters.

## 0.21.0 · 2026-10-17

### New
- **Wishlist invites**: when someone plans a run from the wishlist for a dungeon you want, the tray tells you ("Lumina invites you to Fire Temple"). It follows your planned-run notices on the dashboard. Needs a dashboard server from 0.33.0.

## 0.20.0 · 2026-10-17

### New
- **A richer tray menu**: it starts with what the reader is doing, offers **Start reader** or **Stop reader** and **Show tasks**, and lists your **Characters**, the fullest first, each with its Odyle, cubes and when it was read (a row opens the card).

## 0.19.0 · 2026-10-17

### New
- **Party ready**: from an hour before a run you are going to, once everyone going plays live with enough Odyle for a cube, the tray says so once ("Fire Temple: the party is ready"). Turn it off on the dashboard under **Account & data → Notifications**. Needs a dashboard server from 0.29.0.

## 0.18.0 · 2026-10-17

### New
- **Safer updates.** The server's operators can hold back a release that misbehaves: Odyle never installs it, drops a copy it already downloaded, and if it already runs that version it goes back to the latest good release by itself (within a minute, with a restart). Needs a paired dashboard server from 0.26.0; unpaired apps follow GitHub as before.

## 0.17.2 · 2026-10-17

### Fixed
- A message from the server's operators that you hid on the card **stays hidden after a restart**, until its text changes.

## 0.17.1 · 2026-10-17

### Fixed
- **Screen readers** name the card's icon buttons (pin, tasks, compact mode, settings, the ✕ that hides a message) by what they do, and announce when the reader's state or the status line changes.

## 0.17.0 · 2026-10-17

### New
- **Tasks still open before a reset.** Two hours before the daily and the weekly reset, the tray lists the tasks of that checklist you have not ticked yet ("Daily reset in 2h · 2 tasks open · Still open: Sanctuary ×2"), once per reset, and never when everything is done. Turn it off on the dashboard under **Account & data → Notifications**; quiet hours apply.
- **A reader problem while Odyle sits in the tray** (Npcap missing or restricted, a capture that failed) shows one notification, so a minimised card no longer hides that nothing is read.

## 0.16.0 · 2026-10-17

### New
- **NA-East and NA-West are told apart.** Both have servers of the same names, so a character on NA-West Siel and one of the same name on NA-East Siel used to become one. The card shows NA-E or NA-W. Needs a dashboard server from 0.21.0; with an older one, NA-West characters are saved as NA as before.

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
