## DONE

[X]Playlists
[X]audio downloads
[X]options passed through via api
[X]background download channel assets
[!]Channel downloads (Partial, on a schedule, not on demand)

[X]add tags to workers, so jobs can be assigned to a worker with that tag (otherwise, a S3 worker might not be able to write to the storage for a local NAS)

[X]make info.json optional, write a .meta file with title, hash, media guid, original url (for correlating data back if storage gets moved around)

[X]setup download priorities (so you can force one video to start over another)

[X]batch local import (need to figure out if JUST local filesystem, or use IFluentStorage)
 Implement complete offline media file import from json file

[X]backup and restore (should be postgres+openbao+...)

[X]cancel a download (nicely)

[X] User created playlists [2024-04-10]

[X]Create playlists [2021-11-27]

[!]Per user videos/channel/playlists (playlists only implemented)

[X]Add SponsorBlock integration [2022-04-16]

[X]make sure you can manually download a channel
[X] Added Proof-of-Origin support sidecar/proxy

[X]Download video comments [2022-11-30]

[X]Add thumbnail embed option [2021-10-16] (backend only)

[X]Subtitle download & indexing [2022-02-13]

[X]Delete videos and channel [2021-10-16] (global vs a specific storage key?)

[X]Audio download
[X]M3U8 Playlist mode to serve channel as aac/opus (no podcast mode)

[X]Make sure you can create and set a configuration set for playlist or channel downloads.

[X]Custom searchable notes to videos, channels, playlists (#144)

[X]Download or Ignore videos by keyword (#163)

[X]Search comments

[X] Youtube bot detection

~~Scan your file system to index already downloaded videos [2021-09-14]~~ (not applicable to design)

[X]Auto rescan and auto download on a schedule [2021-12-17]

[X]Add passing browser cookies to yt-dlp [2022-05-08]

[X]Optional automatic deletion of watched items after a specified time [2021-12-17]

[X]Add page size overrides per channel (#702)(allow scanning of videos/lives/shorts)

[X]Configure shorts, streams and video sizes per channel [2024-07-15]

[?]Filter and query in search form, search by url query [2022-07-23]

[X]Per user, have mark watched/unwatched(#547)

[X]Show total video downloaded vs total videos available in channel

[X]Add statistics of index [2023-09-03]

[X]Add a metrics/settings dashboard(#577)

[X]Implement ~~~~Apprise~~~ `recurpixel/notify` for notifications [2023-08-05]



## TODO

upload metadata
live_chat (complex, need to integrate with clickhousedb)

## TubeArchivst Options

Multi language support

Search download queue [2025-07-31]



Implement per channel settings [2022-03-26]




User roles, aka read only user [2023-11-10] (hook into auth framework/APIs)

Access control [2021-11-01]

Un-ignore videos [2021-10-03]
Dynamic download queue [2021-09-26]


## Frontend Tasks

Grid and list view for both channel and video list pages [2021-10-03]

Make items in grid row configurable to use more of the screen [2022-06-04]

Random and repeat controls (#108, #220)
Auto play or play next link (#226)

Show similar videos on video page [2022-11-30]

Fancy advanced unified search interface [2022-01-08] (should use typesense framework)
