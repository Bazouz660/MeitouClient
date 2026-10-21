# Progress

- Waves 1-3 merged into `multiplayer`; origin/master 5087ac3a merged (46901342). 634/634 quick tests.
- Wave 4 done on `multiplayer` (N11 ade5705, N12 9b839ac; spec/WAVE4-NOTES.md). 643/643 quick tests on Linux (7 skipped).
- Wave 5 done (T1 317f8d0, G1 e903d93; spec/WAVE5-NOTES.md). 649/649 quick tests on Linux (7 skipped).
- Merge upstream master (VlcekM/MeitouClient) into `multiplayer` before every wave.
- **Two routine runs were active at once (2026-10-21 ~01:10-01:45 UTC):** session 01J8qQzJ pushed wave 4; session ccd542 pushed
  196ff29 (gate rock ids) and b4dff43 (host robustness, see WAVE4-NOTES "Review fixes") and then stood down. Before
  starting a package, `git fetch origin multiplayer` and build on the newest tip; never redo a package already pushed.

## Report (session 01J8qQzJ, 2026-10-21)

**Finished and pushed to `multiplayer`** (each built with warnings as errors and quick-tested green on Linux):

| Package | Commit |
|---|---|
| N11 NetHost (+ EventTapTests, NetHostTests) | ade5705 |
| N12 NetClient + JitterBuffer (+ JitterBufferTests) | 9b839ac |
| (other session) host robustness | b4dff43 |
| T1 SessionHarness + SessionTests | 317f8d0 |
| G1 game net plumbing | e903d93 |
| G2 host mode | 27a1a3f |
| G3 client mode | 7cc1d45 |
| G4 command line, logs (+ MultiplayerOptionsTests) | ba0d9bc |
| NetProtocol.Version 2, client Hello guard | a5ba017 |

Natural-rock gate bound: 196ff29 (other session). As-built notes: spec/WAVE4-NOTES.md, WAVE5-NOTES.md, WAVE6-NOTES.md.

**Tests** (Linux, SDK 10.0.402, no game install, no GPU): baseline on the untouched tip 627 passed / 7 skipped / 0 failed;
final quick run 660 passed / 7 skipped / 0 failed; final full run 738 passed / 157 skipped / 0 failed. `GoldenHashTests`
skips off Windows (unchanged file).

**Upstream**: VlcekM/MeitouClient master stayed at 5087ac3a for the whole run (already merged); checked before waves 5, 6 and at
the end. Nothing new to merge.

**Blockers**: none for code. G1-G4 cannot be run here (no game, no GPU); no `wip/*` branch was needed.

## Wave 7 checklist (a Windows PC with Kenshi and a GPU)

1. Build `multiplayer`; `dotnet test` full run (record GoldenHashTests and the known base failures from CONTRACT §7).
2. Two instances from one folder: `MeitouClient --host --new-game --name Host --windowed --size 1280x720` and
   `MeitouClient --join 127.0.0.1 --name Guest --config meitou.guest.json --windowed --size 1280x720 --no-audio`.
   Check log.txt / log.2.txt both open; the guest's config gets its own `PlayerId`.
3. Host: new-game editor first, then "Hosting on UDP port 40120" notice; title shows `| host, 1 players`.
4. Guest: Multiplayer window states Connecting → Joining → Loading; loading screen; replica appears near the host's squad;
   "Joined Host's game"; the guest's character editor opens on its Wanderer squad; finish creation → characters editable only by
   the guest. Host sees "Guest joined" and the player list (hold the PlayerList key) with colour and ping.
5. Orders: guest moves/attacks/talks/trades with its own squad; ordering the host's squad gives "Not your characters." as an error.
   Host's and guest's selections, inspected character, trade and dialogue windows stay separate.
6. Clock: guest Space pauses both ("Guest paused the game" on both, "You …" on the guest); speeds 1/2/5 from either; then host
   restricts (Multiplayer window "clients control time" off, re-host) → guest gets "Only the host can change the game speed."
7. Visual: interpolation smooth at 1x and 5x (F11 `net` line: delay, starved, snapshot age), no teleport slides; sounds, blood,
   damage floaters on the guest near its camera; weather and time of day match; relations colours match after a fight.
8. Chat both ways (Enter), /players, /kick from the host; kick → guest returns to title with "Kicked by the host: …"; reconnect
   with the same config → same number, squads still owned, no editor.
9. Ban → "You are banned from this game." on reconnect.
10. Host F5 → guests get "The host saved the game."; guest F5/F9 → "Only the host can save or load.". Host F9 (load while
    hosting) → guests reload under their loading screen and keep their squads.
11. Host Esc → Stop hosting → guest DISCONNECTED alert on the title; host continues single-player (modal Esc pause works again).
12. Esc menu screenshots of every multiplayer window in both UI modes (Faithful, Meitou) on host and guest.
13. Log bytes per second (F11 net lines over a busy town) to tune `SendRate`/`MaxBytesPerSecond` (R4); try a link with
    clumsy-style loss/jitter for the jitter buffer.
14. Join while the host is paused: squad appears without the host unpausing.
