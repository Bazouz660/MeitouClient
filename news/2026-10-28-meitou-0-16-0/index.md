---
title: Meitou 0.16.0
date: 2026-10-28
author: VlcekM
image: banner.jpg
---

Hey hey everyone 😁,

Today we're releasing 0.16 - bringing us within inches of having Meitou play the same as vanilla Kenshi does.

We improved/implemented dozens of mechanics that are in the base game, along with several tens of bug fixes as usual 😎

We put a lot of efforts on combat, animations, and movement to make it feel nigh-indistinguishable from Kenshi. Some gaps remain, but personally, for me it just *feels* like Kenshi. We also added a few **optional** bugfixes - repairing things the base game itself had broken in code.

Our custom navmesh generator also got a bunch of improvements - see attached screenshot to see how it beautifully tracks the geometry 😎

# Slavery is in

And we finally managed to find out and implement the base game's rules around bandit pathing, and they should no longer beat your ass while you hide in populated towns.

And, as usual, **much more!**

Please do keep in mind that performance had a few setbacks and it might stutter a bit right now. We will have a round of bugfixing and optimization before we release v1.0 (feature parity). For those of you struggling with stutters or white/missing textures, we recommend auto detecting graphics settings in the launcher, and/or lowering texture quality for now. I apologize for the performance setback 😓.

Anyway, full changelog:

- Sky arrival: new games and loads fly in from the sky, the hour running forward to the destination's
- Autosave on by default: every 10 min of unpaused play, three rotating slots, with a toggle and slider on the Gameplay page
- Disguises: uniform level, lookers seeing through it, the uniform faction in aggro, panel rows; blown disguises are saved
- Stats window shows every skill's Stats Affected values as the original (work rates, aim, first aid, yield, smithing, dodge and more)
- Inventory: race and lost-limb slot limits drawn red and refusing drops; closed slots also refuse trade, loot and storage equips (#98)
- Stolen items lose their owner mark when handed to the owner's faction; slaves are chained when sold
- Fists reach and hit with the FISTS item's length and multipliers; Fixes setting for the original's weak punch reach (on by default)
- First aid: leg-loss knock-out, splint kits, medics in a fight; kidnappers keep and cage their captives
- Power: generator and battery output scale with health; rain collectors produce with the rain; broken machines read Broken
- Player animals chase, play and race to downed bodies; travelling platoons with slaves ship them to a camp
- Faction tab lists only the factions you have met; Diplomat status entry; faction rename refuses taken names
- Homeless patrols stop outside town gates; shoppers only pick public shops; followers path up to a leader on another storey
- Import: saves older than 0.90.0 disable Reset, Buildings, Research and Dead NPCs
- Launcher 1.4.0: Graphics tabs with every setting, quality presets with hardware Detect; Launch as Kenshi on Steam on first start (Windows)
- Linux: DLSS through NVIDIA's NGX client; camera rotation locks the pointer; Alt+Enter and primary-monitor fixes (#96)
- Graphics: ground parallax and displacement as Faithful/Meitou switches, default ground relief depth 30 cm
- Overview, research, tutorials, faction panel and machine windows laid out as the original
- Fix body parts showing through worn hide parts and mod race editor files in Workshop folders (#93)
- Fix walkers passing through Hub ruin walls, bolts and gibs losing their texture, and the loot window overwriting the player's tooltips
- Fix interior bed selection and the rotation marker showing in the free camera

