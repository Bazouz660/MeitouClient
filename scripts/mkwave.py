"""Builds a wave workflow script from the wave-2 template: mkwave.py <name> <description> <packages.js> <out.js>"""
import sys

SCRIPTS = r"C:\Users\baz\.claude\projects\D--Github-MeitouClient\c161db95-e19c-4784-9759-b1467629f936\workflows\scripts"
name, description, packages, out = sys.argv[1:5]
src = open(SCRIPTS + r"\mp-wave2-wf_dc3e9265-ca6.js", encoding="utf-8").read()
head = src[:src.index("const PACKAGES = [")]
tail = src[src.index("const pkgModel"):]
old_desc = head[head.index("description: '") + len("description: '"):]
old_desc = old_desc[:old_desc.index("'")]
head = head.replace("name: 'mp-wave2'", f"name: '{name}'").replace(old_desc, description)
head = head.replace(
    "branched from the 'multiplayer' branch (wave 1 merged: player sessions/owners/issuer,\nthe Meitou.Net project with primitives, the engine clock seam).",
    "that should be based on the 'multiplayer' branch HEAD (all earlier waves merged).\n"
    "If 'git merge-base --is-ancestor multiplayer HEAD' fails and your branch has no commits of its own, run 'git reset --hard multiplayer' first.")
head = head.replace(
    "(${SPEC}\\\\D1-sim.md, D2-wire.md, D3-session.md, D4-game.md, D5-ui.md).",
    "(${SPEC}\\\\D1-sim.md, D2-wire.md, D3-session.md, D4-game.md, D5-ui.md), and the as-built notes ${SPEC}\\\\WAVE2-NOTES.md (and WAVE3-NOTES.md etc. when present):\n"
    "several signatures differ from the contract; the as-built code and those notes win over the contract text.")
head = head.replace("15 other engineers are working on the other wave-2 packages", "other engineers are working on the other packages of this wave")
for marker in (name, "WAVE2-NOTES", "reset --hard multiplayer", "other packages of this wave"):
    assert marker in head, marker
open(out, "w", encoding="utf-8").write(head + open(packages, encoding="utf-8").read() + "\n\n" + tail)
print("written", out)
