# Progress

- Waves 1-3 merged into `multiplayer`; origin/master 5087ac3a merged (46901342). 634/634 quick tests.
- Wave 4 done on `multiplayer` (N11 ade5705, N12 9b839ac; spec/WAVE4-NOTES.md). 643/643 quick tests on Linux (7 skipped).
- Wave 5 done (T1 317f8d0, G1 e903d93; spec/WAVE5-NOTES.md). 649/649 quick tests on Linux (7 skipped).
- Next: wave 5 (T1, G1), wave 6 (G2, G3, G4), wave 7 smoke run (needs a PC with the game + GPU).
- Merge upstream master (VlcekM/MeitouClient) into `multiplayer` before every wave.
- **Two routine runs were active at once (2026-10-21 ~01:10-01:45 UTC):** session 01J8qQzJ pushed wave 4; session ccd542 pushed
  196ff29 (gate rock ids) and b4dff43 (host robustness, see WAVE4-NOTES "Review fixes") and then stood down. Before
  starting a package, `git fetch origin multiplayer` and build on the newest tip; never redo a package already pushed.
