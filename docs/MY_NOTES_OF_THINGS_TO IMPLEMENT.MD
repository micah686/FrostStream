implement the forgotten code of the progress for a download (probably websockets)
fix the orphan data



Add cast support



Implement some settings from the settings page(#528)

Save video progress per user (#179)(websocket?)
Timestamp seeking

adjust orphan code: maybe new endpoint for orphan scan, and set a policy for both missing metadata(file exists on fs, no db entry) and missing file (db entries exist, no media file). For missing media, probably a clear after 1 week, 1 month, 90 days, never). For missing metadata, probably a clear after 1 week, 1 month, 90 days, 180 days. Maybe also have a CREATE_SKELETON_ENTRY(very basic mediaid so froststream can query it. probably mark as orphaned)
