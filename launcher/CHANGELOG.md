# Launcher changelog

Released with tags `launcher-v<version>` by `.github/workflows/launcher-release.yml`; each release needs its `## v<version>` section.

## Unreleased

- macOS Apple silicon: Settings → Renderer chooses native **Metal** or **Vulkan (MoltenVK)**. Vulkan stays the default;
  Metal needs no Homebrew Vulkan loader and is started without `DYLD_LIBRARY_PATH`.

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
