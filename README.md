# FrostStream

### Services:
- Agent
- CoreAPI
- DataBridge
- MessageBus

### Agent:
Runs the yt-dlp tool, ffmpeg, extracts emotes, metadata.  
May be multiple Agents, are managed by a scheduler/poller  
## Core API:
Main logic/core of the app  
Acts as the main gateway  
does auth/routing/CORS/rate limiting/health checks,...  
Does checks for profiles/account management
does optional emails/notifications

## DataBridge:
Manages any storage and DB access.
Caching and so forth happens here

## MessageBus
Main broker for messages.
Also handles queueing jobs

## Models:
Contains the DTOs of the project. no actual logic here


---
What we need to implement from TA:
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
Implemented:  
Search download queue [2025-07-31]  
Configure shorts, streams and video sizes per channel [2024-07-15]  
User created playlists [2024-04-10]  
User roles, aka read only user [2023-11-10]  
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
Delete videos and channel [2021-10-16]  
Add thumbnail embed option [2021-10-16]  
Create a github wiki for user documentation [2021-10-03]  
Grid and list view for both channel and video list pages [2021-10-03]  
Un-ignore videos [2021-10-03]  
Dynamic download queue [2021-09-26]  
Backup and restore [2021-09-22]  
Scan your file system to index already downloaded videos [2021-09-14]  
