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

## TODO

upload metadata
live_chat (complex, need to integrate with clickhousedb)

backup and restore (should be postgres+openbao+...) 

## TubeArchivst Options

Audio download
Podcast mode to serve channel as mp3
Random and repeat controls (#108, #220)
Auto play or play next link (#226)
Multi language support
Show total video downloaded vs total videos available in channel
Download or Ignore videos by keyword (#163)
Custom searchable notes to videos, channels, playlists (#144)
Search comments
Per user videos/channel/playlists
Search download queue [2025-07-31]
Configure shorts, streams and video sizes per channel [2024-07-15]
User created playlists [2024-04-10]
User roles, aka read only user [2023-11-10] (hook into auth framework/APIs)
Add statistics of index [2023-09-03]
Implement Apprise for notifications [2023-08-05]
Download video comments [2022-11-30]
Show similar videos on video page [2022-11-30]
Implement complete offline media file import from json file [2022-08-20]
Filter and query in search form, search by url query [2022-07-23]
Make items in grid row configurable to use more of the screen [2022-06-04]
Add passing browser cookies to yt-dlp [2022-05-08]
Add SponsorBlock integration [2022-04-16]
Implement per channel settings [2022-03-26]
Subtitle download & indexing [2022-02-13]
Fancy advanced unified search interface [2022-01-08]
Auto rescan and auto download on a schedule [2021-12-17]
Optional automatic deletion of watched items after a specified time [2021-12-17]
Create playlists [2021-11-27]
Access control [2021-11-01]
Delete videos and channel [2021-10-16] (global vs a specific storage key?)
Add thumbnail embed option [2021-10-16]
Create a github wiki for user documentation [2021-10-03]
Grid and list view for both channel and video list pages [2021-10-03]
Un-ignore videos [2021-10-03]
Dynamic download queue [2021-09-26]
Backup and restore [2021-09-22]
Scan your file system to index already downloaded videos [2021-09-14]
