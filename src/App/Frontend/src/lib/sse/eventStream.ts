export interface SseEvent {
  event: string;
  data: string;
  id?: string;
}

export interface EventStreamHandlers {
  onEvent: (event: SseEvent) => void;
  onOpen?: () => void;
}

export class EventStreamError extends Error {
  constructor(
    public readonly status: number,
    message: string
  ) {
    super(message);
    this.name = 'EventStreamError';
  }
}

export function createSseParser() {
  let buffer = '';

  return {
    push(chunk: string): SseEvent[] {
      buffer = (buffer + chunk).replace(/\r\n/g, '\n');
      const events: SseEvent[] = [];

      let separator: number;
      while ((separator = buffer.indexOf('\n\n')) !== -1) {
        const frame = buffer.slice(0, separator);
        buffer = buffer.slice(separator + 2);
        const parsed = parseFrame(frame);
        if (parsed) {
          events.push(parsed);
        }
      }

      return events;
    }
  };
}

export async function readEventStream(
  url: string,
  handlers: EventStreamHandlers,
  signal: AbortSignal,
  fetchImpl: typeof fetch = fetch
): Promise<void> {
  const response = await fetchImpl(url, {
    headers: { accept: 'text/event-stream' },
    credentials: 'same-origin',
    signal
  });

  if (!response.ok || !response.body) {
    throw new EventStreamError(response.status, `SSE request to ${url} failed with status ${response.status}.`);
  }

  handlers.onOpen?.();

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  const parser = createSseParser();

  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) {
        break;
      }
      for (const event of parser.push(decoder.decode(value, { stream: true }))) {
        handlers.onEvent(event);
      }
    }
  } finally {
    reader.releaseLock();
  }
}

function parseFrame(frame: string): SseEvent | null {
  let event = 'message';
  let id: string | undefined;
  const dataLines: string[] = [];

  for (const line of frame.split('\n')) {
    if (line === '' || line.startsWith(':')) {
      continue;
    }

    const colon = line.indexOf(':');
    const field = colon === -1 ? line : line.slice(0, colon);
    let value = colon === -1 ? '' : line.slice(colon + 1);
    if (value.startsWith(' ')) {
      value = value.slice(1);
    }

    if (field === 'event') {
      event = value;
    } else if (field === 'data') {
      dataLines.push(value);
    } else if (field === 'id') {
      id = value;
    }
  }

  if (dataLines.length === 0) {
    return null;
  }
  return { event, data: dataLines.join('\n'), id };
}
