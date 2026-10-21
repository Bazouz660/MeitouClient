# Wave 3 as built (merged into multiplayer).

## N10

### Summary
N10 is done and committed on top of multiplayer 711c1003. The worktree had been cut from master with no commits of its own, so I ran `git reset --hard multiplayer` first. All new code is in src/Meitou.Net/Replication/Delta/ (namespace Meitou.Net.Replication):

- ClientFrame.cs: the ClientFrame record (MaxAcks 8, MaxResync 32) and a static ClientFrameCodec. Layout: 0x11, Epoch, FrameSeq, acks as (Seq, 64-bit part mask), Eye and Focus as Q64 zigzag, DrawDistance u16, resync ints. The writer cuts lists to their limits and clamps the draw distance.
- InterestSet.cs (internal): every IsPlayer character, plus everything the viewer's UI names (selected, inspected, trade, conversation, dialogue request, squad members). Also characters within R = min(draw, 8000) + 300 of the eye on X/Z; they leave only beyond R + 300. One step of carry closure. Weights 1 / 0.5 / 0.25.
- ClientReplicator.cs:
  - Per send: interest, then look definitions (the sent-look set is kept for the connection), then views, then parts.
  - Each part is self-contained. The header is `0x10 | Epoch | Seq | Tick | ViewSeq | PartIndex | PartCount`, then removals, projectiles (removals and records), then characters. A record is slot gap, length, back (0 means a full record and the generation follows), mask, groups.
  - Baselines are acked states within BaselineWindow, or older when nothing was sent for that entity since its ack.
  - Order: full records first, then by accumulated weight, until BytesPerSecond / SendHz is spent (doubled for the first 2 s). A record bigger than a whole part leaves out its Ragdoll group, and is skipped if still too big. At most 64 parts.
  - Removals ride in part 0 until acked. A removal cancelled before its ack leads to a full record.
  - A ring of History parts turns acks into baselines, finished removals and string-table confirmations. Resync requests force full records.
  - Quantized wires are cached per CharacterSnapshot object and shared across clients of one LookRegistry.
  - Codec ArgumentExceptions and messages over MaxReliableMessage are rolled back and logged once through `Log`; nothing is thrown out of Send.
  - `LastSend` (ReplicatorSendStats) gives per-send bytes and starved counts for NetPeerStats.
- ViewSender.cs / ViewReceiver.cs (internal): one N5/N6 sync per view kind (25 kinds) on the Views channel, with change detection by reference, then bytes. The receiver decodes each update on arrival, queues it with its Tick, and commits it when the assembled snapshot reaches that Tick. Updates of another epoch are decoded into throwaway decoders so their string definitions are not lost. LookDefinitions go to the ILookResolver.
- ClientMirror.cs:
  - Keeps a Seq-ordered history per entity and decodes records onto the baselines it holds.
  - A record it cannot decode is skipped by its length, its slot is asked for in full, and its part is not acked.
  - Parts may be duplicated or reordered; a part that does not parse is dropped.
  - Removals and slot reuse are handled.
  - TryAssemble takes the newest ready Seq (all parts arrived, or a newer Seq has started). It builds characters in slot order and projectiles in id order, merges the detail views, and resolves looks; a look not built yet keeps the character's last one.
  - Also TickOf and WriteFrame (acks of the newest 8 Seqs plus resync requests), and counters HasNewParts, DroppedParts and SkippedRecords.
- SnapshotEncoderOptions.cs: SnapshotEncoderOptions and ReplicaCatalog.

docs/net.md: the "Pending (package N10)" line is replaced by the "Interest, delta, replicator and mirror" section, with Verified, Observed and Unknown marks. No other file outside the package was touched.

### Deviations
Where the as-built code differs from the contract or D2 text, and why:
1. **Part layout.** Projectile removals sit inside the projectiles section (removal count, ids, then record count and records), because the contract's layout had no way to remove a projectile.
2. **Resync list.** It carries projectile ids encoded as -1 - id next to character slots, so projectile records can be resynced too.
3. **Revisions.** ViewUpdate bodies for Buildings, GroundItems and Machines have the revision or version appended (zigzag), because the N5 bodies don't carry BuildingsRevision, GroundItemsRevision or MachinesVersion. A Buildings or GroundItems revision bump with no list change sends an empty keyed body.
4. **Baseline window.** It applies only when something was sent for the entity after its ack. Otherwise the acked state is the client's newest and is always kept, so a static entity is not resent in full every 60 sends.
5. **When parts go out.** A header-only part is sent at KeepAliveHz, and also whenever views were sent, so views can commit. The cost is that the mirror's newest Tick can lag the host's by up to SendHz/KeepAliveHz - 1 ticks while nothing changes; the test allows this.
6. **Undecodable records.** A part holding one is applied but not acked, so the host keeps its baselines and redefines that part's strings. Acking it would let the host confirm string definitions the client skipped.
7. **Stale views.** ViewUpdates of another epoch are decoded into throwaway decoders before being dropped. Dropping them unread would lose string-table definitions, which the contract keeps for the whole connection.
8. **Additions beyond the contract signatures:**
   - ClientFrameCodec (static Write/Read, including the 0x11 type byte).
   - ReplicatorSendStats and ClientReplicator.LastSend / Log.
   - ClientMirror.Log, HasNewParts, DroppedParts, SkippedRecords, NewestSeq, ViewSeq.
   - SnapshotEncoderOptions and ReplicaCatalog are created here. Both N11 and N12 need ReplicaCatalog and no package owned it, so creating it in one place avoids a wave-4 merge conflict.
9. **Projectile interest.** Projectiles use the character radius and hysteresis; D2 does not say how projectiles are filtered.
10. **ViewSeq in the part header.** It is informational only; views commit by Tick, as the contract says.

### Review remaining
- Edge, not fixed (N1 semantics): ViewSender.ResetKind after an over-MaxReliableMessage view resets a KeyedViewSync mid-epoch; if that list later becomes empty, the reset sync writes no order change, so the client keeps its stale items. Only happens after an oversize view, which is already logged.
- MachinesVersion only goes out with a machines body; that is enough because MachineRegistry.Touch bumps Version on every published change.
- No Slow or full test run was done; nothing here touches game data or simulation code.

## U2

### Summary
U2 done. The title's MULTIPLAYER entry now opens a Multiplayer window, which works in both UI modes. The window is a MyGUI layout parsed with MyGuiReader.ParseLayout. It uses only installed skins and fonts, and it was checked in screenshots of both Host and Join tabs, in both UI modes.

- **Title:** MULTIPLAYER sits between LOAD GAME and OPTIONS in the Meitou stack. The vignette behind the stack is now 520 tall.
- **Host tab:** new game, a save from the load list, or the running game. It has port, players 2-8, password, clients control time and allow cheats. New game closes the window, opens the new-game panel and raises HostGame when Begin is pressed. A save raises HostGame with its folder, behind a confirm when a game is running.
- **Join tab:** address via JoinAddress, password and a recent games list, with a connecting status and Cancel.
- **Shared parts:** the player name is shared and persisted in UserConfig. Errors show in the status line.
- **Join progress:** the window follows the session phases and sends CancelJoin if Esc closes it while connecting. Once Playing, the game goes into RecentGames.
- **Esc menu:** OpenMultiplayer(true) shows only the Host tab for the running game, with the button reading OPEN.
- **Docs:** docs/meitou-ui.md "Title entry and Multiplayer window" replaces the placeholder.

### Deviations
1. I added `MyGuiScreenHost.SetSelected(id, widget, index)` in src/Meitou.Ui/MyGuiScreenHost.cs, outside my two files. Without it a reopened games list or recent list shows its previous row highlighted while mpGame is reset, so the selection and the stored game disagree. It is 2 lines.
2. The worktree started on master, so I ran `git reset --hard multiplayer` first. The branch had no commits of its own.
3. I followed the contract over D5. The window uses MultiplayerEvent, RosterEntry, NetDisconnect and NetPhase (no Downloading phase), and the default port is 40120. The "Loading the world..." status has no progress figure.
4. After the Playing phase, MultiplayerScreenFrame also calls RememberGame and Config.Save itself. G3 does the same, which is harmless because RememberGame is unique by address.
5. Layout tweaks from the screenshots:
   - The "Max players" label became "Players" because it was truncated.
   - The number between the arrows uses Kenshi_GenericTextBoxFlat instead of Kenshi_TextboxStandardText, which rendered too dim.
   - The status line gets explicit colours (light for normal, light red for errors) because the panel is dark.
   - The active tab text is white.
6. The MULTIPLAYER icon (Kenshi_CharacterSpeedSet, per spec) is dark and reads dim on the Meitou plate.
7. A screenshot-only edit to GameHost.Screenshot.cs was reverted and is not in the commit.

### Review remaining
- Not checked against G3: after a failed join the Ended phase may linger, so a following Connect could show the old error for a frame if the session is not reset by the JoinGame handler.

## U3

### Summary
U3 Chat: PlayerInterface.Chat.cs implements the non-modal chat entry (OpenChat opens and focuses it, Enter/SEND sends via Net.SendChat, Esc keeps a draft, Up/Down recall the last 20 sent lines, Tab swallowed), the fading bottom-left history with player-coloured names, and system lines for Joined/Left/TimedOut/Kicked/Banned/Paused/Resumed/Speed ("You" for own changes). Host commands /help /players /kick /ban run through RunChatCommand. Multiplayer/ChatCommands.cs holds the pure Parse and Resolve. Added the parser and resolver tests to MultiplayerUiTests.cs and wrote the "Chat" subsection of docs/meitou-ui.md.

### Deviations
ChatCommands.Resolve takes IReadOnlyList<RosterEntry> and returns RosterEntry? because no NetPlayer type exists. ChatVerb, ChatCommand and ChatCommands are public (the test method's signature needs ChatVerb public). The Wrap-based history puts the name prefix on the first visual row. A /kick or /ban with no target gives a Usage error (not in the spec). Self/host check uses LocalPlayer or player 0. Session lines also go to LogMessage. Switch cases use Meitou.Engine.Input.Key because PlayerInterface has a Key() method that shadows the enum.

### Review remaining
- Minor and left as is: the backing widget gets Alpha and a colour alpha, which may square the fade; the spec asks for both. Name colouring falls back to the whole line if wrapping splits the name prefix, which a 24-char name makes very unlikely.

## U4

### Summary
U4 done. Players.cs: the held player list, a Kenshi_FloatingPanelSkin panel with title, header, and rows of colour chip, name, HOST plate, joining text, squads and coloured ping. Stealth.cs: a teammate's tag shows its name and the owner's name in the owner's colour; own tags in multiplayer get a 2px colour bar. Map.cs: own markers use the player's colour in multiplayer, other players get per-character markers (zoomed in) or per-squad markers (least zoom), and nests are revealed by any player. docs/meitou-ui.md U4 placeholder replaced.

### Deviations
1) The worktree started on master, so I ran git reset --hard multiplayer first (711c1003). 2) The list excludes roster entries with State Away, following the IMultiplayerView.Players doc ("lists show the entries whose state is not Away"), though the task mentioned an Away state; there is no AFK field in RosterEntry, so no AFK text. Only "joining…" for Loading is shown. 3) Ping helpers are named ListPing* to avoid a clash with U5's own indicator helpers. 4) Own-character map markers also use the player's colour only for character markers; the camera marker keeps PlayerColour. 5) No tests added, as instructed.

### Review remaining
- Visual layout not checked in a running game (long names are not truncated in the list or tags)
- A character with IsPlayer and Owner -1 would show as 'Player -1' (edge case, not expected from D1)

## U5

### Summary
U5 is done. NetStatus: connection indicator (client bars plus ping, host "HOSTING · n/max"), starving plate, "{name} paused the game" under PausedPanel, 3 s clock banner for other players' events, TimeSpeedButton disabling, ShowDisconnected/OpenPendingAlert. NetMenu: Esc menu rows per role (the four added decorated buttons, panel resized about its centre from the verified layout geometry), NetMenuAction with Stop hosting, Disconnect and the client/host Exit confirmations, and the players window (MultiListBox, coloured names, Kick/Ban with confirmation, closes when the session ends). Docs: the meitou-ui.md U5 subsection replaces its placeholder. No tests were added.

### Deviations
1. The paused line reads "{name} paused the game" ("You paused the game"), as the task and CONTRACT line 649 say, not D5's "by {name}". 2. No NetPlayer/NetEnd types exist, so the players window reads RosterEntry. The Status column shows "Joining" for State.Loading and blank otherwise, because there is no AFK state. The ping is shown as a dash for the host and for yourself. 3. Ping thresholds (C1 in the contract does not define any) are my choice: up to 100 ms is 3 bars, up to 200 ms is 2, above is 1. 4. The starving plate text uses "..." instead of the ellipsis character, to be safe with the font. 5. The player colour is applied to the row text (TextColour on the first column's row buttons), not as a separate chip, because chips would overlap the text. 6. The players-window selection is kept as a player id (playersSelected). The highlight uses the host's index-based selection, so it can lag one frame if the roster reorders. 7. HUD speed buttons are disabled as the task says, so the "Only the host can change the game speed." notice no longer shows on a mouse click and only appears for keyboard attempts, which the host refuses. 8. The panel/row constants are named Esc* because PlayerInterface.TitleMenu.cs already defines MenuBottom and MenuButtons. 9. NetMenu includes RegisterPlayersScreen, BindPlayersScreen, AddPlayersContent and PlayersAction, since the U1 stubs for them were in NetMenu.cs.

### Review remaining
- The new UI has not been checked visually in a running game.
- In Meitou UI mode the HUD time buttons are drawn by uiPainter and are not disabled; only the faithful HUD disables them. PressButton still refuses and shows the notice there.

## F3

### Summary
F3 done, after resetting the branch to 'multiplayer'. (1) FcsRecordCodec.Write throws ArgumentException for a null string, a name, key or id over MaxName, a string value over MaxString (checked in UTF-8 bytes), and a non-finite float, Vector3, Vector4 or Quaternion. (2) PlayerColours.Assign forwards to PlayerColourRule.Assign. (3) ProtocolCodec clamps a Reject reason to the new const MaxRejectReason (MaxDropPayload - 4 = 996 bytes) and throws ArgumentException for non-finite clock and weather doubles. (4) EventCodec writes the SoundKind, BloodMarkKind, FloaterKind and NoticeKind values through a WriteKind helper that throws ArgumentException when the value is not a defined member. docs/net.md has one-line additions for each in the FCS records, Events and ProtocolCodec paragraphs.

### Deviations
Beyond the brief: (a) ProtocolCodec also refuses non-finite weather floats (strength, wind, fog), because the reader's ReadFinite would refuse them too. (b) The existing ProtocolCodecTests case built a NaN ClockState through the writer; the writer now refuses that, so I changed it to patch the NaN into valid bytes so the reader check is still covered. The Reject clamp assertion now expects MaxRejectReason instead of MaxMessageText. (c) PlayerColours.Assign keeps its old default: preferred < 0 is passed to the rule as 0, because PlayerColourRule maps -1 to colour 7. An out-of-range preferred value is now reduced modulo 8, as the rule and the old doc comment say, rather than falling to the lowest free colour. U1's cases still pass. (d) MaxRejectReason (public const) and the 'using Meitou.Net.Transport' import in ProtocolCodec and its tests are new. (e) I added no EventCodec test, because the task lists none and the contract's approval covers only the Fcs and Protocol cases.

### Review remaining

