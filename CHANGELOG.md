# Changelog
## 0.10.3
- Fixed: resupply stopped working as soon as an *Ammo box* (or another container without an access point) was on the map — every check failed with an error. Such containers now work as ammo sources too.
- Fixed: playable KAR / BOTE-type vehicles bought with the mod (they are aircraft-based internally) were never rearmed. They are now rearmed when standing still near an ammo source.
- Units whose weapon stations the game's own rearm routine can't handle are refilled directly.
- Diagnostics in `BepInEx/LogOutput.log`: a resupply summary once a minute, and the list of units hidden from the purchase menu (other faction's equipment / mission event content) with the reason.
## 0.10.2
- Fixed: units did not rearm from purchased ammo containers, depots and supply trucks and just stood still with empty magazines. Now any stopped friendly vehicle or ship that is short of ammo is rearmed by the nearest friendly ammo source within **150 m** (configurable), using the game's own rearm rules.
- Fixed: supply trucks bought *for me* spawned in hold position, which switched off the game's supply AI. They now drive to friendly units that need ammo on their own, like in the base game.
- New settings in *4. Behaviour*: *Resupply from nearby ammo sources*, *Resupply radius, m*, *Supply trucks work on their own*.
## 0.10.1
- Fixed: in 0.10.0 the mod stopped loading after the language setting (the F6 menu did nothing and F1 showed only the language). Two setting names contained an apostrophe, which BepInEx does not allow. All your settings are kept.
## 0.10.0
- **English interface** (default) with a **Russian** option: all windows, buttons, tips, messages, unit groups and stratagems are translated. Switch with the *Language* button in the purchase menu or F1 → *0. Language* (applies instantly).
- F1 settings now have English names (Russian names are shown when the language is Russian, after a restart). Your existing values are carried over automatically.
## 0.9.4
- Rebuilt for game version 0.34.6. No gameplay changes.
## 0.9.3
- Stratagems: choose the number of shells / bombs / missiles per salvo and the number of salvos (1–10) for artillery, carpet bombing and missile strikes. The price scales with the total amount. New setting: interval between salvos (default 4 s).
## 0.9.2
- Unit limits for yourself and for the faction can now be set right in the purchase menu (sliders + −10/−/+/+10 buttons), up to **150** each.
## 0.9.1
- Renamed to **ORTIZ Ops - FieldCommand** (DLL `ORTIZ-Ops-FieldCommand.dll`). Settings are kept.
## 0.9.0
- Renamed to **ORTIZ Field Command** (GUID `ortiz.fieldcommand`). Settings of older builds are migrated automatically.
- First public release.
