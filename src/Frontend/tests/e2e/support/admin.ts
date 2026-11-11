import type { Page, Route } from '@playwright/test';
import { mockApi } from './api';

export const storage = { key: 'archive', description: 'Archive storage', method: 'Local', local: { protocol: 'Local', path: '/media/archive' }, createdAt: '2025-01-01T00:00:00Z', lastUpdated: null };
export const schedule = { id: 1, key: 'nightly', taskType: 'search-reindex', cron: null, intervalSeconds: 3600, timezone: 'UTC', enabled: true, catchupPolicy: 'Coalesce', retentionDays: 30, includeFailed: false, lastAttemptAt: null, lastSuccessAt: null, nextDueAt: null, createdAt: '2025-01-01T00:00:00Z', lastUpdated: null };
export const session = { sessionId: 'session-1', correlationId: 'group-1', status: 'reviewing', sourceKind: 'workerIncoming', sourceRoot: '/incoming', storageKey: 'archive', totalItems: 2, probedItems: 2, readyItems: 1, incompleteItems: 1, excludedItems: 1, approvedItems: 0, importedItems: 0, alreadyImportedItems: 0, failedItems: 0, maxParallelItems: 1, deleteSourceFiles: false, createdAt: '2025-01-01T00:00:00Z', updatedAt: '2025-01-01T00:00:00Z' };
export const item = { itemId: 'item-1', sessionId: 'session-1', relativePath: 'folder/video.mp4', fileName: 'video.mp4', fileSizeBytes: 1048576, title: 'Video title', metadataState: 'incomplete', metadataSource: 'placeholder', metadataFetchState: 'notAttempted', metadataFetchAttempt: 0, hasNfo: false, hasInfoJson: false, excluded: false, status: 'probed', attempt: 0, createdAt: '2025-01-01T00:00:00Z', updatedAt: '2025-01-01T00:00:00Z' };
export async function adminApi(page: Page, handler?: (route: Route, url: URL) => Promise<boolean>) {
  await mockApi(page, async (route, url) => {
    if (handler && await handler(route, url)) return true;
    const path = url.pathname;
    let body: unknown;
    if (path === '/api/storage/list') body = [storage];
    else if (path === '/api/storage/archive') body = storage;
    else if (path === '/api/admin/workers') body = { workers: [{ workerId: 'worker-1', name: 'Worker One', tags: ['gpu'], incomingRoot: '/incoming', lastOnline: '2025-01-01T00:00:00Z' }] };
    else if (path === '/api/global/schedules') body = [schedule];
    else if (path.startsWith('/api/global/access-control/')) body = [];
    else if (path === '/api/global/imports/sessions') body = session;
    else if (path === '/api/global/imports/sessions/session-1') body = session;
    else if (path === '/api/global/imports/sessions/session-1/items') body = { success: true, items: [item], totalCount: 1, nextItemId: null };
    else if (path === '/api/global/imports/sessions/session-1/items/bulk') body = { success: true, affectedCount: 1 };
    else if (path === '/api/global/imports/sessions/session-1/items/item-1') body = { success: true, item };
    else if (path === '/api/global/imports/sessions/session-1/metadata-refresh') body = { success: true, checkedCount: 1, foundCount: 0 };
    else if (path === '/api/global/imports/sessions/session-1/enrich') body = { success: true, queuedCount: 1 };
    else if (path === '/api/global/imports/sessions/session-1/commit') body = { success: true, approvedCount: 1, session };
    else return false;
    await route.fulfill({ json: body }); return true;
  });
}
