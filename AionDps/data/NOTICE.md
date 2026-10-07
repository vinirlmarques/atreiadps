# Game data tables

The lookup tables in this folder are extracted from AION 2 game data (© NCSOFT) and were taken from
open-source meters licensed under GPL-3.0, which is why AionMeter as a whole is distributed under GPL-3.0
(see `../LICENSE`).

| File | Source |
|---|---|
| `skills/*.json`, `npcs/*.json`, `dungeons/*.json`, `dot_skill_ids.json`, `servers.json`, `skill_icons.json` | [taengu/A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter) `src/data`, commit `e32dc5e` (2026-10-05), GPL-3.0 |
| `healing_skill_ids.json` | [Kuroukihime/AIon2-Dps-Meter](https://github.com/Kuroukihime/AIon2-Dps-Meter) `GameData/Assets`, commit `2e8d67d` (2026-10-01), GPL-3.0 |
| `src/AionMeter.App/Assets/Classes/*.png` (class emblems; brawler downscaled to 128 px) | same repository, `AionDpsMeter.UI/Assets/Classes`, commit `2e8d67d`, GPL-3.0 |
| `boss_portraits.json` (NPC code → portrait file) | built from [MetaBot.GG's boss list](https://metabot.gg/en/aion-2/bosses) (2026-10-05): each dungeon boss's portrait file, matched to every NPC code with the same English name in `npcs/en.json`. The portraits themselves are not bundled: the app downloads them from MetaBot.GG at runtime (credit: [MetaBot.GG](https://metabot.gg)) |

Formats AionMeter reads (every file is optional):

- `skills/<lang>.json` — `{ "16040000": "Skill name", … }`
- `npcs/<lang>.json` — `{ "2000002": { "name": "…", "isBoss": false, "isDummy": false }, … }`
- `dungeons/<lang>.json` — `{ "600021": { "name": "Fire Temple" }, … }` (map id → name)
- `servers.json` — `{ "servers": { "2305": { "en": "…", "ru": "…" } } }`
- `dot_skill_ids.json`, `healing_skill_ids.json` — arrays of skill codes
- `field_boss_maps.json` — `{ "maps": { "1110": { "block": 2400, "en": "Altgard", "ru": "Альтгард" } } }`: a map's field boss
  list (opcode 01 91) holds its bosses in NPC-code order, all from one block of a thousand codes. Established on the
  2026-10-06 EU capture against the in-game list (24 of 24 slots).
- `boss_portraits.json` — `{ "portraits": { "2300471": "2300471", "2300104": "f/mob_berc_01", … } }` (file name on
  metabot.gg/web/aion2/npcs/ without `.webp`; NPCs not listed are tried under their own code)

Missing languages fall back to `en`.
