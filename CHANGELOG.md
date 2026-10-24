# Changelog

## 0.1.0
- Initial release.
- Per-station size sliders (Configuration Manager), auto-discovered for every smelter, kiln, refinery, crafting station, station upgrade, cooking station and fermenter — including modded ones.
- Smelter, Blast Furnace, Charcoal Kiln, Eitr Refinery, Frost Foundry and Frigid Kiln default to 70%.
- Minimum size limits: 40% for smelters/kilns/refineries, 30% for everything else.
- "Keep Smoke Exit At Vanilla Height" (on by default) so shrunk chimneys don't choke.
- Optional "Smokeless Stations" toggle.
- Custom Stations: add any piece by prefab name (with an optional starting size) and get a slider for it; helper toggle logs every buildable piece's prefab name.
- Server-authoritative settings sync; live updates from Configuration Manager or .cfg edits.
