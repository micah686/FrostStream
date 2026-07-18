// State model and (de)serialization for the GUI option-preset editor.
//
// The wire format is the JSON-serialized C# YtDlpOptions record: nested camelCase
// groups (videoFormat, postProcessing, subtitle, ...) with PascalCase enum strings.
// GET responses come back fully materialized (every group present, bools false,
// nullables null), so `false`/`null`/''/[] must all be treated as "unset".

export type TriState = 'default' | 'on' | 'off';

export interface SelectItem {
  value: string;
  name: string;
}

export interface PresetOptionsState {
  // Video quality & format
  resolution: string; // '' = default, height cap ('2160'...'360'), or 'custom'
  customFormat: string;
  container: string;

  // Audio
  audioOnly: boolean;
  audioFormat: string;
  audioQuality: string;

  // Subtitles
  writeSubs: TriState;
  writeAutoSubs: TriState;
  subLangs: string;
  subFormat: string;
  embedSubs: TriState;

  // Thumbnails & metadata
  writeThumbnail: TriState;
  embedThumbnail: TriState;
  embedMetadata: TriState;
  embedChapters: TriState;
  writeDescription: TriState;
  writeInfoJson: TriState;
  embedInfoJson: TriState;

  // SponsorBlock
  sponsorBlockDisabled: boolean;
  sponsorBlockMark: string[];
  sponsorBlockRemove: string[];
  sponsorBlockChapterTitle: string;
  sponsorBlockApi: string;

  // Comments & live streams
  fetchComments: TriState;
  liveFromStart: TriState;
  waitForVideo: string; // poll interval, e.g. "60" seconds; blank = don't wait

  // Download behavior & limits
  limitRate: string;
  concurrentFragments: number | undefined;
  retries: string;
  playlistItems: string;
  maxFilesize: string;
  minFilesize: string;
  date: string; // YYYY-MM-DD for the date input; other yt-dlp date expressions pass through
  dateAfter: string; // YYYY-MM-DD for the date input; other yt-dlp date expressions pass through
  dateBefore: string;
  throttledRate: string;
  retrySleep: string; // one "[type:]EXPR" per line, passed through verbatim
  bufferSize: string;
  resizeBuffer: TriState;
  httpChunkSize: string;

  // Network
  proxy: string;

  // Authentication
  username: string;
  password: string;
  twoFactor: string;
  videoPassword: string;

  // Workarounds
  noCheckCertificates: boolean;
  legacyServerConnect: boolean;
  sleepRequests: number | undefined;
  sleepInterval: number | undefined;
  maxSleepInterval: number | undefined;
  sleepSubtitles: number | undefined;
  addHeaders: string; // one "Key: Value" per line
}

export const triStateItems: SelectItem[] = [
  { value: 'default', name: 'Default' },
  { value: 'on', name: 'On' },
  { value: 'off', name: 'Off' }
];

export const resolutionItems: SelectItem[] = [
  { value: '', name: 'Best available (default)' },
  { value: '2160', name: '4K (2160p)' },
  { value: '1440', name: '1440p' },
  { value: '1080', name: '1080p' },
  { value: '720', name: '720p' },
  { value: '480', name: '480p' },
  { value: '360', name: '360p' },
  { value: 'custom', name: 'Custom format string…' }
];

export const containerItems: SelectItem[] = [
  { value: '', name: 'Default' },
  { value: 'Mp4', name: 'MP4' },
  { value: 'Mkv', name: 'MKV' },
  { value: 'Webm', name: 'WebM' },
  { value: 'Mov', name: 'MOV' },
  { value: 'Flv', name: 'FLV' },
  { value: 'Avi', name: 'AVI' }
];

export const audioFormatItems: SelectItem[] = [
  { value: '', name: 'Default' },
  { value: 'Best', name: 'Best' },
  { value: 'Aac', name: 'AAC' },
  { value: 'Alac', name: 'ALAC' },
  { value: 'Flac', name: 'FLAC' },
  { value: 'M4a', name: 'M4A' },
  { value: 'Mp3', name: 'MP3' },
  { value: 'Opus', name: 'Opus' },
  { value: 'Vorbis', name: 'Vorbis' },
  { value: 'Wav', name: 'WAV' }
];

export const audioQualityItems: SelectItem[] = [
  { value: '', name: 'Default' },
  { value: '0', name: 'Best (0)' },
  { value: '2', name: 'High (2)' },
  { value: '5', name: 'Medium (5)' },
  { value: '7', name: 'Low (7)' }
];

export const subtitleFormatItems: SelectItem[] = [
  { value: '', name: 'Default' },
  { value: 'Best', name: 'Best' },
  { value: 'Srt', name: 'SRT' },
  { value: 'Vtt', name: 'VTT' },
  { value: 'Ass', name: 'ASS' },
  { value: 'Lrc', name: 'LRC' }
];

/** Categories valid for both marking and removal. */
export const sponsorBlockCategories: SelectItem[] = [
  { value: 'sponsor', name: 'Sponsor' },
  { value: 'selfpromo', name: 'Self-promotion' },
  { value: 'interaction', name: 'Interaction reminder' },
  { value: 'intro', name: 'Intro' },
  { value: 'outro', name: 'Outro' },
  { value: 'preview', name: 'Preview/recap' },
  { value: 'music_offtopic', name: 'Non-music section' },
  { value: 'filler', name: 'Filler tangent' }
];

/** Categories that can only be marked as chapters, never removed. */
export const sponsorBlockMarkOnlyCategories: SelectItem[] = [
  { value: 'poi_highlight', name: 'Highlight' },
  { value: 'chapter', name: 'Chapters' }
];

function formatForResolution(height: string): string {
  return `bestvideo[height<=${height}]+bestaudio/best`;
}

const resolutionByFormat = new Map(
  resolutionItems
    .filter((item) => item.value && item.value !== 'custom')
    .map((item) => [formatForResolution(item.value), item.value])
);

function group(options: Record<string, unknown>, name: string): Record<string, unknown> {
  const value = options[name];
  return value && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};
}

function triState(source: Record<string, unknown>, onKey: string, offKey: string): TriState {
  return source[onKey] === true ? 'on' : source[offKey] === true ? 'off' : 'default';
}

function text(source: Record<string, unknown>, key: string): string {
  const value = source[key];
  return typeof value === 'string' ? value : '';
}

function numeric(source: Record<string, unknown>, key: string): number | undefined {
  const value = source[key];
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function csvList(source: Record<string, unknown>, key: string): string[] {
  return text(source, key)
    .split(',')
    .map((entry) => entry.trim())
    .filter(Boolean);
}

function lineList(source: Record<string, unknown>, key: string): string {
  const value = source[key];
  return Array.isArray(value) ? value.filter((entry) => typeof entry === 'string').join('\n') : '';
}

/** Wire dates are YYYYMMDD; the date input needs YYYY-MM-DD. Other yt-dlp
 *  expressions (e.g. "today-1week") are kept verbatim and round-trip unchanged. */
function dateFromWire(value: string): string {
  return /^\d{8}$/.test(value)
    ? `${value.slice(0, 4)}-${value.slice(4, 6)}-${value.slice(6, 8)}`
    : value;
}

function dateToWire(value: string): string {
  return /^\d{4}-\d{2}-\d{2}$/.test(value) ? value.replaceAll('-', '') : value;
}

export function stateFromOptions(options: Record<string, unknown>): PresetOptionsState {
  const videoFormat = group(options, 'videoFormat');
  const postProcessing = group(options, 'postProcessing');
  const subtitle = group(options, 'subtitle');
  const thumbnail = group(options, 'thumbnail');
  const filesystem = group(options, 'filesystem');
  const sponsorBlock = group(options, 'sponsorBlock');
  const download = group(options, 'download');
  const videoSelection = group(options, 'videoSelection');
  const network = group(options, 'network');
  const authentication = group(options, 'authentication');
  const workarounds = group(options, 'workarounds');
  const general = group(options, 'general');

  const format = text(videoFormat, 'format');
  const resolution = format ? (resolutionByFormat.get(format) ?? 'custom') : '';

  return {
    resolution,
    customFormat: resolution === 'custom' ? format : '',
    container: text(videoFormat, 'mergeOutputFormat'),

    audioOnly: postProcessing.extractAudio === true,
    audioFormat: text(postProcessing, 'audioFormat'),
    audioQuality: text(postProcessing, 'audioQuality'),

    writeSubs: triState(subtitle, 'writeSubs', 'noWriteSubs'),
    writeAutoSubs: triState(subtitle, 'writeAutoSubs', 'noWriteAutoSubs'),
    subLangs: text(subtitle, 'subLangs'),
    subFormat: text(subtitle, 'subFormat'),
    embedSubs: triState(postProcessing, 'embedSubs', 'noEmbedSubs'),

    writeThumbnail: triState(thumbnail, 'writeThumbnail', 'noWriteThumbnail'),
    embedThumbnail: triState(postProcessing, 'embedThumbnail', 'noEmbedThumbnail'),
    embedMetadata: triState(postProcessing, 'embedMetadata', 'noEmbedMetadata'),
    embedChapters: triState(postProcessing, 'embedChapters', 'noEmbedChapters'),
    writeDescription: triState(filesystem, 'writeDescription', 'noWriteDescription'),
    writeInfoJson: triState(filesystem, 'writeInfoJson', 'noWriteInfoJson'),
    embedInfoJson: triState(postProcessing, 'embedInfoJson', 'noEmbedInfoJson'),

    sponsorBlockDisabled: sponsorBlock.noSponsorblock === true,
    sponsorBlockMark: csvList(sponsorBlock, 'sponsorblockMark'),
    sponsorBlockRemove: csvList(sponsorBlock, 'sponsorblockRemove'),
    sponsorBlockChapterTitle: text(sponsorBlock, 'sponsorblockChapterTitle'),
    sponsorBlockApi: text(sponsorBlock, 'sponsorblockApi'),

    fetchComments: triState(filesystem, 'writeComments', 'noWriteComments'),
    liveFromStart: triState(general, 'liveFromStart', 'noLiveFromStart'),
    waitForVideo: text(general, 'waitForVideo'),

    limitRate: text(download, 'limitRate'),
    concurrentFragments: numeric(download, 'concurrentFragments'),
    retries: text(download, 'retries'),
    playlistItems: text(videoSelection, 'playlistItems'),
    maxFilesize: text(videoSelection, 'maxFilesize'),
    minFilesize: text(videoSelection, 'minFilesize'),
    date: dateFromWire(text(videoSelection, 'date')),
    dateAfter: dateFromWire(text(videoSelection, 'dateafter')),
    dateBefore: dateFromWire(text(videoSelection, 'datebefore')),
    throttledRate: text(download, 'throttledRate'),
    retrySleep: lineList(download, 'retrySleep'),
    bufferSize: text(download, 'bufferSize'),
    resizeBuffer: triState(download, 'resizeBuffer', 'noResizeBuffer'),
    httpChunkSize: text(download, 'httpChunkSize'),

    proxy: text(network, 'proxy'),

    username: text(authentication, 'username'),
    password: text(authentication, 'password'),
    twoFactor: text(authentication, 'twofactor'),
    videoPassword: text(authentication, 'videoPassword'),

    noCheckCertificates: workarounds.noCheckCertificates === true,
    legacyServerConnect: workarounds.legacyServerConnect === true,
    sleepRequests: numeric(workarounds, 'sleepRequests'),
    sleepInterval: numeric(workarounds, 'sleepInterval'),
    maxSleepInterval: numeric(workarounds, 'maxSleepInterval'),
    sleepSubtitles: numeric(workarounds, 'sleepSubtitles'),
    addHeaders: lineList(workarounds, 'addHeaders')
  };
}

export function emptyState(): PresetOptionsState {
  return stateFromOptions({});
}

/** Removes values that are indistinguishable from "unset" after a round-trip
 *  through the C# model: null, false, '', empty arrays, and empty objects.
 *  Numbers (including 0) always survive. */
function pruneDefaults(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.length > 0 ? value.map(pruneDefaults) : undefined;
  }
  if (value && typeof value === 'object') {
    const result: Record<string, unknown> = {};
    for (const [key, entry] of Object.entries(value)) {
      const pruned = pruneDefaults(entry);
      if (pruned !== undefined) {
        result[key] = pruned;
      }
    }
    return Object.keys(result).length > 0 ? result : undefined;
  }
  if (value === null || value === false || value === '' || value === undefined) {
    return undefined;
  }
  return value;
}

function setOrDelete(target: Record<string, unknown>, key: string, value: unknown) {
  if (value === undefined || value === null || value === '' || value === false) {
    delete target[key];
  } else {
    target[key] = value;
  }
}

function applyTriState(
  target: Record<string, unknown>,
  onKey: string,
  offKey: string,
  value: TriState
) {
  delete target[onKey];
  delete target[offKey];
  if (value === 'on') {
    target[onKey] = true;
  } else if (value === 'off') {
    target[offKey] = true;
  }
}

function parseHeaderLines(value: string): string[] {
  return value
    .split('\n')
    .map((line) => line.trim())
    .filter(Boolean);
}

/**
 * Applies the GUI state onto a copy of the stored options. Options the GUI does
 * not manage (e.g. a previously configured download.httpChunkSize) are preserved;
 * GUI-managed keys are set when the control has a value and removed when it is
 * back at its default, so an untouched new preset serializes to {}.
 */
export function applyStateToOptions(
  state: PresetOptionsState,
  base: Record<string, unknown>
): Record<string, unknown> {
  const result = (pruneDefaults(structuredClone(base)) as Record<string, unknown> | undefined) ?? {};

  const groups = {
    videoFormat: group(result, 'videoFormat'),
    postProcessing: group(result, 'postProcessing'),
    subtitle: group(result, 'subtitle'),
    thumbnail: group(result, 'thumbnail'),
    filesystem: group(result, 'filesystem'),
    sponsorBlock: group(result, 'sponsorBlock'),
    download: group(result, 'download'),
    videoSelection: group(result, 'videoSelection'),
    network: group(result, 'network'),
    authentication: group(result, 'authentication'),
    workarounds: group(result, 'workarounds'),
    general: group(result, 'general')
  };

  const format =
    state.resolution === 'custom'
      ? state.customFormat.trim()
      : state.resolution
        ? formatForResolution(state.resolution)
        : '';
  setOrDelete(groups.videoFormat, 'format', format);
  setOrDelete(groups.videoFormat, 'mergeOutputFormat', state.container);

  // yt-dlp rejects audio format/quality without --extract-audio, so they only
  // serialize while audio-only is enabled.
  setOrDelete(groups.postProcessing, 'extractAudio', state.audioOnly);
  setOrDelete(groups.postProcessing, 'audioFormat', state.audioOnly ? state.audioFormat : '');
  setOrDelete(groups.postProcessing, 'audioQuality', state.audioOnly ? state.audioQuality : '');

  applyTriState(groups.subtitle, 'writeSubs', 'noWriteSubs', state.writeSubs);
  applyTriState(groups.subtitle, 'writeAutoSubs', 'noWriteAutoSubs', state.writeAutoSubs);
  setOrDelete(groups.subtitle, 'subLangs', state.subLangs.trim());
  setOrDelete(groups.subtitle, 'subFormat', state.subFormat);
  applyTriState(groups.postProcessing, 'embedSubs', 'noEmbedSubs', state.embedSubs);

  applyTriState(groups.thumbnail, 'writeThumbnail', 'noWriteThumbnail', state.writeThumbnail);
  applyTriState(groups.postProcessing, 'embedThumbnail', 'noEmbedThumbnail', state.embedThumbnail);
  applyTriState(groups.postProcessing, 'embedMetadata', 'noEmbedMetadata', state.embedMetadata);
  applyTriState(groups.postProcessing, 'embedChapters', 'noEmbedChapters', state.embedChapters);
  applyTriState(groups.filesystem, 'writeDescription', 'noWriteDescription', state.writeDescription);
  applyTriState(groups.filesystem, 'writeInfoJson', 'noWriteInfoJson', state.writeInfoJson);
  applyTriState(groups.postProcessing, 'embedInfoJson', 'noEmbedInfoJson', state.embedInfoJson);

  setOrDelete(groups.sponsorBlock, 'noSponsorblock', state.sponsorBlockDisabled);
  setOrDelete(groups.sponsorBlock, 'sponsorblockMark', state.sponsorBlockMark.join(','));
  setOrDelete(groups.sponsorBlock, 'sponsorblockRemove', state.sponsorBlockRemove.join(','));
  setOrDelete(groups.sponsorBlock, 'sponsorblockChapterTitle', state.sponsorBlockChapterTitle.trim());
  setOrDelete(groups.sponsorBlock, 'sponsorblockApi', state.sponsorBlockApi.trim());

  applyTriState(groups.filesystem, 'writeComments', 'noWriteComments', state.fetchComments);
  applyTriState(groups.general, 'liveFromStart', 'noLiveFromStart', state.liveFromStart);
  setOrDelete(groups.general, 'waitForVideo', state.waitForVideo.trim());

  setOrDelete(groups.download, 'limitRate', state.limitRate.trim());
  setOrDelete(groups.download, 'concurrentFragments', state.concurrentFragments);
  setOrDelete(groups.download, 'retries', state.retries.trim());
  setOrDelete(groups.videoSelection, 'playlistItems', state.playlistItems.trim());
  setOrDelete(groups.videoSelection, 'maxFilesize', state.maxFilesize.trim());
  setOrDelete(groups.videoSelection, 'minFilesize', state.minFilesize.trim());
  setOrDelete(groups.videoSelection, 'date', dateToWire(state.date.trim()));
  setOrDelete(groups.videoSelection, 'dateafter', dateToWire(state.dateAfter.trim()));
  setOrDelete(groups.videoSelection, 'datebefore', dateToWire(state.dateBefore.trim()));
  setOrDelete(groups.download, 'throttledRate', state.throttledRate.trim());
  const retrySleepLines = parseHeaderLines(state.retrySleep);
  if (retrySleepLines.length > 0) {
    groups.download.retrySleep = retrySleepLines;
  } else {
    delete groups.download.retrySleep;
  }
  setOrDelete(groups.download, 'bufferSize', state.bufferSize.trim());
  applyTriState(groups.download, 'resizeBuffer', 'noResizeBuffer', state.resizeBuffer);
  setOrDelete(groups.download, 'httpChunkSize', state.httpChunkSize.trim());

  setOrDelete(groups.network, 'proxy', state.proxy.trim());

  setOrDelete(groups.authentication, 'username', state.username.trim());
  setOrDelete(groups.authentication, 'password', state.password);
  setOrDelete(groups.authentication, 'twofactor', state.twoFactor.trim());
  setOrDelete(groups.authentication, 'videoPassword', state.videoPassword);

  setOrDelete(groups.workarounds, 'noCheckCertificates', state.noCheckCertificates);
  setOrDelete(groups.workarounds, 'legacyServerConnect', state.legacyServerConnect);
  setOrDelete(groups.workarounds, 'sleepRequests', state.sleepRequests);
  setOrDelete(groups.workarounds, 'sleepInterval', state.sleepInterval);
  setOrDelete(groups.workarounds, 'maxSleepInterval', state.maxSleepInterval);
  setOrDelete(groups.workarounds, 'sleepSubtitles', state.sleepSubtitles);
  const headers = parseHeaderLines(state.addHeaders);
  if (headers.length > 0) {
    groups.workarounds.addHeaders = headers;
  } else {
    delete groups.workarounds.addHeaders;
  }

  for (const [name, groupValue] of Object.entries(groups)) {
    if (Object.keys(groupValue).length > 0) {
      result[name] = groupValue;
    } else {
      delete result[name];
    }
  }

  return result;
}
