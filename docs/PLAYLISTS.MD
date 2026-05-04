hort version: add a parent PlaylistArchiveFlow that fans out one child DownloadArchiveFlow per entry, sharing a          
  CorrelationId. The existing flow already says it's designed for this — IFlowMessage.CorrelationId doc literally calls out 
  "playlist ingestion that fans out into one job per video."                              
                                                                                                                            
  The shape that fits the current architecture:                                                                             
                                                                                                                            
  1. WebAPI publishes a new root event PlaylistRequested (PlaylistJobId, CorrelationId, SourceUrl).                         
  2. DataBridge has a PlaylistArchiveFlow keyed off PlaylistJobId.ToString("N"). Inside it:
    - Send a new FetchPlaylistEntriesCommand to Worker. Worker runs yt-dlp --flat-playlist (cheap, metadata-only) and       
  replies with PlaylistEntriesFetched { Entries: [{ SourceUrl, Index }, ...] }.                                             
    - For each entry, Capture(...) and publish a DownloadRequested with a fresh child JobId, the parent CorrelationId, and  
  CausationId = PlaylistEntriesFetched.MessageId. Each one independently spawns its own DownloadArchiveFlow exactly as      
  today.          
    - Done. Don't try to await N children inside the parent flow — fire-and-forget is much simpler and you get aggregate    
  status by querying download_jobs grouped by CorrelationId.                                                                
  
  Why this over the alternatives:                                                                                           
  - Don't fan out in WebAPI directly — long playlists would block the HTTP request, and a WebAPI crash mid-enumeration leaks
   half-published jobs. The whole point of putting Cleipnir between WebAPI and Worker is durable orchestration; fan-out is  
  exactly the kind of thing that benefits from it.                                                                        
  - Don't overload DownloadArchiveFlow to detect "oh this URL is a playlist" — it's currently nice and single-purpose (one  
  source → one media item → one row), and you'd lose that.                                                                
                                                                                                                            
  Two decisions worth making explicitly before you code:
                                                                                                                            
  1. Dedup scope — ForceDownload is per-DownloadRequested. A playlist re-run will hit MarkAlreadyDownloadedAsync for        
  previously archived entries and only download new ones, which is probably what you want. Confirm before building.         
  2. Parent terminal state — does PlaylistArchiveFlow complete after publishing N DownloadRequesteds, or do you want it to  
  track child outcomes? Fire-and-forget is the smaller change. If you ever want a "playlist X: 47/50 succeeded" rollup, that
   becomes a query over download_jobs WHERE correlation_id = ?, not parent-flow state.
