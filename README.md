# Television

Plays videos from TV packs on the ship's TV. TVLoader packs still work with it.

- Who needs it: each player who wants to see them. Players without it see the game's own videos.
- Config: shared from the host through [ConfigSync](https://thunderstore.io/c/lethal-company/p/Mortar/ConfigSync/), so
  everyone sees the same thing.
  - `Channel`: on by default. Off starts a video from its beginning each time the TV comes on, like TVLoader.
  - `Include game clips`: keeps the TV's own clips in the schedule. Off by default.
  - `Shuffle`: plays the videos in an order picked from the lobby's seed instead of by file name. On by default.

The videos play back to back like one channel on the lobby's clock, so you see the same moment as everyone else, even
right after joining or turning the TV on. Like the game's own, the TV goes dark while you're in the facility. Players
follow the host's schedule, so a video someone doesn't have, or can't play, stays dark for its slot and nobody drifts.
If none of a player's videos play, the TV shows the game's own clips. Videos you add while the game runs join the
channel once they're measured.

Look at the TV to see what's on, and press Q there to move everyone to the next video.

## Making a pack

Put MP4 or WebM files in a `mortar/tv` folder in your package. The `Television Videos` folder TVLoader uses is still
read. WebM plays everywhere. On Linux, MP4 needs
GE-Proton 11: see [Videos on Linux](docs/linux-video.md).
