<script lang="ts">
  import type { PageData } from './$types';

  let { data }: { data: PageData } = $props();

  let probePath = $state('auth/config');
  let probeMethod = $state('GET');
  let probeBody = $state('');
  let probeBusy = $state(false);
  let probeResult = $state('Run a request through the BFF proxy to inspect API auth behavior.');
  const bodyPlaceholder = '{"example": true}';

  const configJson = $derived(JSON.stringify(data.config, null, 2));
  const statusJson = $derived(JSON.stringify(data.status, null, 2));
  const loggedIn = $derived(data.status?.singleUserMode || data.status?.hasSession);
  const mode = $derived(data.status?.singleUserMode ? 'single-user' : 'multi-user');

  async function runProbe() {
    probeBusy = true;
    const path = probePath.replace(/^\/+/, '');
    const init: RequestInit = { method: probeMethod };
    if (!['GET', 'HEAD'].includes(probeMethod) && probeBody.trim()) {
      init.body = probeBody;
      init.headers = { 'content-type': 'application/json' };
    }

    try {
      const response = await fetch(`/api/${path}`, init);
      const body = await response.text();
      probeResult = [
        `${response.status} ${response.statusText}`,
        '',
        body || '<empty response>'
      ].join('\n');
    } catch (error) {
      probeResult = error instanceof Error ? error.message : String(error);
    } finally {
      probeBusy = false;
    }
  }
</script>

<svelte:head>
  <title>FrostStream Auth Tester</title>
  <meta name="description" content="Minimal Authentik integration tester for FrostStream." />
</svelte:head>

<main class="shell">
  <section class="hero">
    <div>
      <p class="eyebrow">FrostStream helper app</p>
      <h1>AuthTester</h1>
      <p class="summary">
        A small BFF surface for validating Authentik login, callback cookies, and authenticated API
        proxying without loading the full frontend.
      </p>
    </div>

    <div class="actions" aria-label="Authentication actions">
      {#if data.status?.singleUserMode}
        <span class="pill">Single-user mode</span>
      {:else if data.status?.hasSession}
        <span class="pill success">Session cookie present</span>
        <a class="button secondary" href="/auth/logout">Logout</a>
      {:else}
        <span class="pill warn">No session</span>
        <a class="button" href="/auth/login">Login with Authentik</a>
      {/if}
    </div>
  </section>

  <section class="status-grid" aria-label="Auth status">
    <article>
      <span>Mode</span>
      <strong>{mode}</strong>
    </article>
    <article>
      <span>Usable session</span>
      <strong>{loggedIn ? 'yes' : 'no'}</strong>
    </article>
    <article>
      <span>API authority</span>
      <strong>{String(data.config?.authority ?? 'not reported')}</strong>
    </article>
  </section>

  <section class="panel">
    <div class="panel-title">
      <h2>API Probe</h2>
      <button type="button" onclick={runProbe} disabled={probeBusy}>
        {probeBusy ? 'Running...' : 'Send'}
      </button>
    </div>

    <div class="probe-form">
      <label>
        <span>Method</span>
        <select bind:value={probeMethod}>
          <option>GET</option>
          <option>POST</option>
          <option>PUT</option>
          <option>PATCH</option>
          <option>DELETE</option>
        </select>
      </label>
      <label>
        <span>Path under /api</span>
        <input bind:value={probePath} placeholder="auth/config" />
      </label>
    </div>

    {#if !['GET', 'HEAD'].includes(probeMethod)}
      <label class="body-field">
        <span>JSON body</span>
        <textarea bind:value={probeBody} rows="5" placeholder={bodyPlaceholder}></textarea>
      </label>
    {/if}

    <pre>{probeResult}</pre>
  </section>

  <section class="details">
    <article class="panel">
      <h2>/api/auth/config</h2>
      <pre>{configJson}</pre>
    </article>
    <article class="panel">
      <h2>Local session status</h2>
      <pre>{statusJson}</pre>
    </article>
  </section>
</main>

<style>
  .shell {
    width: min(1120px, calc(100% - 32px));
    margin: 0 auto;
    padding: 32px 0;
  }

  .hero {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    gap: 24px;
    align-items: end;
    padding: 20px 0 28px;
    border-bottom: 1px solid rgb(148 163 184 / 18%);
  }

  .eyebrow {
    margin: 0 0 8px;
    color: #67e8f9;
    font-size: 0.78rem;
    font-weight: 700;
    letter-spacing: 0.08em;
    text-transform: uppercase;
  }

  h1,
  h2,
  p {
    margin: 0;
  }

  h1 {
    font-size: clamp(2.4rem, 7vw, 5rem);
    line-height: 0.95;
  }

  h2 {
    font-size: 1rem;
  }

  .summary {
    max-width: 720px;
    margin-top: 16px;
    color: #b8c2d1;
    line-height: 1.6;
  }

  .actions {
    display: flex;
    flex-wrap: wrap;
    gap: 10px;
    justify-content: flex-end;
    align-items: center;
  }

  .button,
  button {
    min-height: 40px;
    border: 0;
    border-radius: 6px;
    padding: 0 14px;
    background: #22d3ee;
    color: #071116;
    font-weight: 800;
    text-decoration: none;
    cursor: pointer;
  }

  .button {
    display: inline-flex;
    align-items: center;
  }

  .button.secondary {
    background: #2b3342;
    color: #f4f7fb;
  }

  button:disabled {
    opacity: 0.65;
    cursor: wait;
  }

  .pill {
    display: inline-flex;
    min-height: 32px;
    align-items: center;
    border-radius: 999px;
    padding: 0 12px;
    background: rgb(103 232 249 / 12%);
    color: #a5f3fc;
    font-size: 0.85rem;
    font-weight: 700;
  }

  .pill.success {
    background: rgb(52 211 153 / 14%);
    color: #86efac;
  }

  .pill.warn {
    background: rgb(251 191 36 / 14%);
    color: #fde68a;
  }

  .status-grid,
  .details {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: 14px;
    margin-top: 20px;
  }

  .details {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .status-grid article,
  .panel {
    border: 1px solid rgb(148 163 184 / 18%);
    border-radius: 8px;
    background: rgb(17 24 39 / 72%);
  }

  .status-grid article {
    padding: 16px;
  }

  .status-grid span {
    display: block;
    color: #8f9bad;
    font-size: 0.78rem;
    font-weight: 700;
    text-transform: uppercase;
  }

  .status-grid strong {
    display: block;
    overflow-wrap: anywhere;
    margin-top: 8px;
    font-size: 1.05rem;
  }

  .panel {
    margin-top: 20px;
    padding: 16px;
  }

  .panel-title {
    display: flex;
    gap: 12px;
    align-items: center;
    justify-content: space-between;
  }

  .probe-form {
    display: grid;
    grid-template-columns: 140px minmax(0, 1fr);
    gap: 12px;
    margin-top: 14px;
  }

  label span {
    display: block;
    margin-bottom: 6px;
    color: #9ca8ba;
    font-size: 0.82rem;
    font-weight: 700;
  }

  input,
  select,
  textarea {
    box-sizing: border-box;
    width: 100%;
    border: 1px solid rgb(148 163 184 / 24%);
    border-radius: 6px;
    padding: 10px 11px;
    background: #0d1119;
    color: #f4f7fb;
  }

  .body-field {
    display: block;
    margin-top: 12px;
  }

  pre {
    overflow: auto;
    max-height: 420px;
    margin: 14px 0 0;
    border-radius: 6px;
    padding: 12px;
    background: #090d13;
    color: #d7e0ee;
    font-size: 0.86rem;
    line-height: 1.55;
  }

  @media (max-width: 760px) {
    .hero,
    .status-grid,
    .details,
    .probe-form {
      grid-template-columns: 1fr;
    }

    .actions {
      justify-content: flex-start;
    }
  }
</style>
