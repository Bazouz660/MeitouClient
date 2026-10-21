# Launcher changelog

Released with tags `launcher-v<version>` by `.github/workflows/launcher-release.yml`; each release needs its `## v<version>` section.

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
