# Media Pruner – Jellyfin 12 plugin

Automatically or manually deletes **movies (Radarr)** and **series (Sonarr)** based on configurable criteria, right from the Jellyfin dashboard.

## Features

| Category | Option |
|---|---|
| **Connections** | Radarr and Sonarr (URL + API key), each can be enabled separately, "Test connection" button |
| **Criteria** | Added more than X days ago (Radarr/Sonarr date **or** Jellyfin date) |
| | Do not delete if favorited (by any user), option: a favorited season/episode → series protected |
| | Only delete fully watched items (optional) |
| | Keep if played in the last X days |
| | Keep series that are still airing (Sonarr status `continuing`) |
| | Radarr/Sonarr protected tags (default `keep`) |
| | Manual per-item protection |
| **Mode** | Automatic deletion (scheduled task) or manual |
| | Dry-run mode (the task logs without deleting anything) — **enabled by default** |
| | Deletion limit per run (safeguard, 10 by default) |
| | Delete files, add import exclusion, scan the library after deletion |
| **Jellyfin tag** | Adds a tag (default `Leaving soon`) to movies/series that will be deleted within the warning window, removed automatically when they are no longer pending |
| **Jellyfin collection** | Keeps a `Leaving soon` collection with the same items (added/removed automatically, deleted when the option is turned off) |

### Configuration page (Dashboard → Media Pruner)

1. **Options**: all the settings above.
2. **Pending deletion**: items scheduled for deletion within the next *X* days (poster, date added, deletion date, size, favorites, exclusion reason). Actions: delete an item, delete the selection, delete all eligible items, protect / unprotect. Filters: "show excluded items" and "entire library".
3. **History**: the last 500 deletions (auto, manual, dry run) with their result.

The **"Radarr / Sonarr cleanup"** scheduled task (category *Media Pruner*) runs every day at 3 AM by default; the schedule can be changed in *Dashboard → Scheduled Tasks*.

## Jellyfin ↔ *arr matching

Items are matched using TMDb / IMDb IDs (movies) and TVDB / TMDb / IMDb IDs (series). Favorites and watch status are read for **all** Jellyfin users. An item missing from Jellyfin can still be deleted based on the date criterion.
