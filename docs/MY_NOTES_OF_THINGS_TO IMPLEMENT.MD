add tags to workers, so jobs can be assigned to a worker with that tag (otherwise, a S3 worker might not be able to write to the storage for a local NAS)

Make sure you can set a configuration set for playlist or channel downloads
make info.json optional, write a .meta file with title, hash, guid, original url (for correlating data back if storage gets moved around)
make sure you can manually download a channel
setup download priorities (so you can force one video to start over another)
cancel a download (nicely)
implement the forgotten code of the progress for a download (probably websockets)
fix the orphan data
