# Localisation

How the original chooses its language and what a language brings, and how Meitou does the same
(`src/Meitou.Core/Localisation/`, `GameDatabase.Load`). Install layout: `locale/<code>/` holds
`LC_MESSAGES/main.mo` (compiled gettext, plus `main.po`/`main.pot` text sources), `<code>.translation`
(FCS record layer), fonts and other resource files, `tips.txt`, `names*.txt`. Codes seen: de_DE, es_ES,
fr_FR, it_IT, ja_JP, ko_KR, pt_BR, ru_RU, zh_CN; `en_GB` has only the `.pot`. Message format: [gettext](gettext.md).

## Language selection

Addresses are `kenshi_x64.exe` (1.0.68).

- **Verified** (`FUN_140175a90`): a fixed table maps the ten Steam names (english, german, french, russian, italian, portuguese,
  spanish, chinese, japanese, korean) to codes. A non-empty `language` in `settings.cfg` wins when it is one of the table's codes or a
  `locale/` folder (not `_`-prefixed); when empty, `steam_language` decides; anything unknown is `en_GB`. With Steam running a changed Steam
  language overrides `language` and the choice is written back to `settings.cfg` (**Observed**; Meitou does neither).
  Related: `FUN_1401753e0`, `FUN_1401751d0`, `FUN_1401750a0`, `FUN_140174c00`, `FUN_140174d50`; launcher language message `FUN_140124650`.
- **Verified** (`Ogre_RegisterResourceLocations` `140816d90`): for a non-English language its `locale/<code>` is registered as a resource
  folder after the base game's and before the mods' (the last registered location wins a name), so fonts, layouts, images and sounds
  placed there override the base. `en_GB` adds nothing.
- **Verified** (`Mods_LocateModTranslation` `14086f240`): per loaded mod, the first sorted sub-folder X of `<mod>/locale` that holds
  `LC_MESSAGES/main.mo` or `X.translation`. The first mod in load order that has one replaces the active language with X
  (messages from that mod's folder; the user's core translation is not applied). The check of a flag on the mod entry at the start is **Unknown**.
- **Verified** (`Fcs_GameLoadSequence` `140870ae0`, `Fcs_LoadTranslationFile` `1406c29a0`): `.translation` files load with the mod loader.
  Order: core files, the user's `locale/<code>/<code>.translation`, mods, then the translation of the **first** mod that has one (not all
  mods' translations). **Observed** (en_GB vs de/ja/ru dumps): a translation record changes the name (and text fields) of the record with
  the same key; records of unloaded mods it names match nothing.
- **Verified** (tips `FUN_14091a280`, names `FUN_140641d80`): `tips.txt` and the `names*.txt` pools are resource lookups, so the language
  folder's copy replaces `data/`'s.
- **Observed**: `factionNames.txt`, `tips_DE.txt` and `translated.txt` are not read by the game.

## Meitou implementation

- `GameLanguage.Detect` applies the rules above (reads only `settings.cfg`; no new setting, no Steam). The Meitou settings menu has no
  language option and the menu itself is not translated. `Loc.Use` runs at start after `UserConfig.Load()`.
- **Catalog** (`GettextCatalog`): `main.mo` (LE/BE, plural splits, `\x04` context), else `main.po` (fuzzy and obsolete skipped). Plural rule
  (`PluralRule`) is parsed from the header `Plural-Forms`; Germanic when absent. Verified for all eight locales: `.mo` and `.po` give the same messages.
- **Placeholders**: `{N}`, `{N,num}`, `{N,num=fix,p=K}` (1-based, boost-format). `num` uses the language's culture. Translations may reorder them.
- **API**: `Loc.Tr(msgid[, args])`, `Loc.Plural(singular, plural, n, args)`. A msgid with no translation, and any text that is not a msgid
  (Meitou-only), returns unchanged, so lookups are exact and English stays English.
- **Record names**: a translation changes `GameRecord.Name`; logic that matched English names uses `GameRecord.BaseName` (the name before
  translation): GLOBAL CONSTANTS, the money item, "Nameless", the blank squad, default empire, faction/start/appearance finds.
  Translation records that match nothing are dropped after loading (the original may keep them: **Unknown**).
- **UI strings**: strings that exist as msgids in `main.pot` go through the catalog at central sinks: UI bindings (`UiBindings.Coded`, captions and
  combo items), HUD `AddLines`, context-menu verbs, `Notice`, `Confirm`/`UpperTr`, `PlaceTooltip` (keys and values; bracketed headings by their
  inside), stat help (`StatHelpCatalog`), the loading tip, the day text, and the interpolated simulation messages (relations, bounties, research,
  campaigns, construction, fixtures, discoveries). Layout captions the original leaves English stay English.
- **Fonts, tips, names**: through the overlay folder (`OgreScriptResources.ForInstall`) and `Loc.FindTextFile`.
- All text files are read and written as UTF-8.

## Unknown / not done

- Steam-language override and the `settings.cfg` write-back.
- The mod-entry flag gate of `Mods_LocateModTranslation`.
- The exact lookup behaviour of `FUN_14017c3f0` (**Observed** only).
- Whether ghost records from translations of unloaded mods persist in the original.
- A translated Meitou settings menu and language option (needs a decision).
