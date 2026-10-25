# Launcher changelog

Released with tags `launcher-v<version>` by `.github/workflows/launcher-release.yml`; each release needs its `## v<version>` section.

## Unreleased

- **Report to the roadmap** under the launcher version: tell the roadmap a system works or is broken, with the version,
  platform and mods, and optionally the end of that version's log.txt. Shown once the reports service is live.
- macOS Apple silicon: first start and Settings → Renderer offer **Auto**, native **Metal** and **Vulkan (MoltenVK)**. Auto
  uses Vulkan when its loader exists and Metal otherwise, so a new user does not need Homebrew; Metal starts without
  `DYLD_LIBRARY_PATH`.
- macOS Vulkan now gets a narrow `vulkan/` loader folder through `DYLD_LIBRARY_PATH` instead of all of Homebrew's library
  folder, so Homebrew `libjpeg`/`libpng` cannot replace AppKit/ImageIO's copies. Settings has the Homebrew command and a
  loader check.
- First start explains that Kenshi is required and links to a macOS acquisition guide; Find now searches macOS Steam,
  CrossOver and Whisky locations.
- The launcher reads the release's `settings-schema.json` `renderers` list, so a Vulkan-only release does not claim Metal
  support.
- macOS Metal has a **memory budget** in Settings: Safe 5120 MB, Balanced 6144 MB, High 8120 MB on 16 GB-or-larger Macs.
  It is passed to the game as a soft allocation guard; the launcher also clears the experimental GPU-cull environment
  switch so users cannot turn on the path that caused GPU progress timeouts.

## v1.1.2

- When there are two nightly builds on the same day, the Versions list marks the second one "#2" and shows them in order.
  Before, the two looked the same and could appear in the wrong order.

## v1.1.1

- Nightly builds now come twice a day; the afternoon build shows its date in the Versions list like the morning one.

## v1.1.0

- The launcher updates itself: at startup it installs a new launcher version and restarts into it. Turn this off in Settings
  with "Update the launcher automatically"; the update button stays for doing it by hand. Updating to 1.1.0 itself still
  takes one click on that button.

## v1.0.4

- macOS: Play starts the game again instead of it exiting at once because the Vulkan loader wasn't found. The launcher now
  hands the game Homebrew's library folder and the MoltenVK setting it needs (#20).
- macOS: without the Vulkan loader installed, Play says how to install it instead of starting a game that closes at once.
- When the game stops with an error, the status line says so and where its log is.
- While the game is running, Play says so instead of starting a second copy that would overwrite its settings and mod list.

## v1.0.3

- A new launcher version shows an update button in the title bar, on every page; the status line no longer loses the notice.

## v1.0.2

- macOS: the launcher starts again instead of crashing at launch (#16).
- Linux: the AppImage runs on older distributions (glibc 2.35 and newer, e.g. Ubuntu 22.04).
- A Ko-fi button in the News bar for supporting the project.

## v1.0.1

- Reads every page of GitHub releases, not only the newest 100: old game versions stay installable and the launcher's own
  updates are always found.

## v1.0.0

- First release: installs game releases from GitHub (stable or with pre-releases), with a cancellable download and a SHA-256
  check; starts the game with the chosen Kenshi folder.
- Mod list like Kenshi's launcher (enable, drag to reorder, `mods.cfg`), mod presets, RE_Kenshi plugin mods marked.
- Looks like Meitou's own settings: plates, Exo 2 from the Kenshi install, its own title bar.
- Windows Setup, Linux AppImage and macOS packages that update themselves from new launcher releases.
- One Native AOT executable per system: no .NET runtime or DLLs beside it.
