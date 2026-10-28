# Videos on Linux

Valve's Proton can't decode MP4, so the TV and the terminal's moon videos show colour bars. GE-Proton 11 or newer
decodes them. Don't use GE-Proton 10: its video decoder crashes the game when the ship loads.

1. Get the newest GE-Proton11 from [its releases](https://github.com/GloriousEggroll/proton-ge-custom/releases) and
   extract it into `~/.steam/steam/compatibilitytools.d/` (Flatpak Steam:
   `~/.var/app/com.valvesoftware.Steam/data/Steam/compatibilitytools.d/`), or let
   [ProtonUp-Qt](https://github.com/DavidoTek/ProtonUp-Qt) do it. Restart Steam.
2. In Steam, open Lethal Company's Properties, then Compatibility, and pick it under "Select compatibility tool".
3. If you start the game from [Mortar](https://github.com/Rethunk-Tech/mortar) without Steam, pick it under Settings,
   Lethal Company, Proton version too.

WebM videos play on any Proton, so a pack in WebM needs none of this.
