---
title: Meitou 0.15.1
date: 2026-10-25
author: VlcekM
image: banner.jpg
---

Hey hey everybody,

continuing the rapid cadence of changes: **0.15.1** is out, a follow-up to 0.15.0 with a native Mac renderer, combat much closer to the original, and a
lot of fixes from your reports. Thanks to everyone who tested the latest release!

**macOS on Metal.** Apple silicon Macs now get a native **Metal** renderer. The launcher picks
it for you; you can also choose Metal or Vulkan (via MoltenVK) yourself. This should hopefully improve performance
by about 15-20%, and is the amazing work of @Amatadorro!

We've done a lot of work fine-tuning and reverse engineering (actually, the other way around, first reverse engineering, then fine tuning) the combat and movement side of the original game,
meaning it should theoretically play the same as the original 😎👍.

Slopped changelog below:

**Combat as the original.**

- Fighters pick targets by the original's scoring, and **Passive** and **Hold** behave as in vanilla
- Close-in fighters move with the original's steering: they keep their distance, strafe, side-step and share attack slots
- Squad mates join a fight only when protecting someone nearby, and give up on targets they can't reach
- NPCs bash in doors to get at someone hiding behind them
- Stealth KO XP matches the original

**Also new**

- New games start as in the original: faction relations, start town, discovered towns, start squad and the day-1 clock
- Orders work while paused: move, fight, first aid, doors and cages
- **Pick Up** works on standing squad mates
- Ruined town houses you buy are rebuilt through construction jobs
- The Overview's **DIALOGUE** tab logs what was said
- Meitou's own texts can be translated, starting with **Brazilian Portuguese**
- New **Ground relief** and **Light shafts** quality settings

**Fixes:** chasing squads vanishing when their town unloaded, unreachable Hive huts, stalls under Proton, full screen
on Wayland, flickering buildings near town repairs, dead characters staying in the squad, and the HUD's Toughness XP.

The full list is in the [release notes](https://github.com/VlcekM/MeitouClient/releases).
