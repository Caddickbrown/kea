<script>
  import { api } from './api.js';
  import { ago, chapterRange, jobFolder } from './format.js';
  import { app, isActive, notify, openInLibrary, setView } from './store.svelte.js';

  const active = $derived(app.jobs.filter(isActive));
  const finished = $derived(app.jobs.filter((j) => !isActive(j)));

  let busy = $state(new Set());

  async function act(job, fn) {
    busy = new Set([...busy, job.id]);
    try { await fn(); } catch (e) { notify(e.message, 'error'); }
    finally { busy = new Set([...busy].filter((id) => id !== job.id)); }
  }

  const cancel = (job) => act(job, () => api(`/api/jobs/${job.id}/cancel`, { method: 'POST' }));

  const dismiss = (job) => act(job, async () => {
    await api(`/api/jobs/${job.id}`, { method: 'DELETE' });
    app.jobs = app.jobs.filter((j) => j.id !== job.id);
  });

  async function clearFinished() {
    for (const job of finished) await dismiss(job);
  }

  const failures = (job) => (job.chapters || []).filter((c) => c.error);
  const percent = (job) => Math.round((job.progress || 0) * 100);

  function outcome(job) {
    if (job.status === 'completed') return `${job.succeeded} chapter${job.succeeded === 1 ? '' : 's'} saved`;
    if (job.status === 'cancelled') return 'Stopped before it finished';
    if (job.error) return job.error;
    return job.statusText;
  }
</script>

{#snippet row(job)}
  <li class="job" class:failed={job.status === 'failed'}>
    <div class="top">
      <h3>{job.comics.join(', ')}</h3>
      <span class="state {job.status}">{job.status === 'running' ? `${percent(job)}%` : job.status}</span>
    </div>

    <p class="meta faint">{job.format} &middot; {chapterRange(job)} &middot; {ago(job.createdAt, app.tick)}</p>

    {#if isActive(job)}
      <div class="track" role="progressbar" aria-valuenow={percent(job)} aria-valuemin="0" aria-valuemax="100" aria-label="Progress for {job.comics.join(', ')}">
        <div class="fill" style:width="{percent(job)}%"></div>
      </div>
      <p class="live muted">{job.statusText}</p>
    {:else}
      <p class="result" class:bad={job.status === 'failed'}>{outcome(job)}</p>
    {/if}

    {#if failures(job).length}
      <details>
        <summary>{failures(job).length} chapter{failures(job).length === 1 ? '' : 's'} did not save</summary>
        <ul>
          {#each failures(job).slice(0, 50) as f}
            <li><span class="num">Chapter {f.chapterNumber}</span> <span class="muted">{f.error}</span></li>
          {/each}
        </ul>
      </details>
    {/if}

    <div class="actions">
      {#if isActive(job)}
        <button class="btn quiet danger" disabled={busy.has(job.id)} onclick={() => cancel(job)}>Cancel</button>
      {:else}
        {#if job.succeeded > 0}
          <button class="btn quiet" onclick={() => openInLibrary(jobFolder(job))}>Open in library</button>
        {/if}
        <button class="btn quiet" disabled={busy.has(job.id)} onclick={() => dismiss(job)}>Dismiss</button>
      {/if}
    </div>
  </li>
{/snippet}

<div class="queue">
  {#if app.jobs.length === 0}
    <div class="empty">
      <h2>Nothing in the queue.</h2>
      <p class="muted">Add a comic and it will show up here, with its progress.</p>
      <button class="btn" onclick={() => setView('add')}>Add a comic</button>
    </div>
  {/if}

  {#if active.length}
    <section>
      <header><h2>Downloading</h2><span class="faint num">{active.length}</span></header>
      <ul class="jobs">{#each active as job (job.id)}{@render row(job)}{/each}</ul>
    </section>
  {/if}

  {#if finished.length}
    <section>
      <header>
        <h2>Finished</h2><span class="faint num">{finished.length}</span>
        <button class="btn quiet clear" onclick={clearFinished}>Clear all</button>
      </header>
      <ul class="jobs">{#each finished as job (job.id)}{@render row(job)}{/each}</ul>
    </section>
  {/if}
</div>

<style>
  .queue { display: grid; gap: var(--s-6); padding-top: var(--s-5); }

  header { display: flex; align-items: baseline; gap: var(--s-3); padding-bottom: var(--s-2); border-bottom: 1px solid var(--ink); }
  header h2 { font-size: 1.25rem; font-weight: 700; letter-spacing: -0.01em; }
  .clear { margin-left: auto; }

  .jobs { list-style: none; margin: 0; padding: 0; }
  .job { padding: var(--s-4) 0; border-bottom: 1px solid var(--line); display: grid; gap: var(--s-1); }

  .top { display: flex; align-items: baseline; justify-content: space-between; gap: var(--s-3); }
  h3 { font-size: 1.125rem; font-weight: 600; overflow-wrap: anywhere; }
  .state { font-size: .9375rem; color: var(--muted); font-variant-numeric: tabular-nums; }
  .state.running { color: var(--accent); font-weight: 600; }
  .state.failed { color: var(--danger); font-weight: 600; }
  .state.completed { color: var(--muted); }

  .meta { font-size: .9375rem; }

  .track { height: 4px; background: var(--line); border-radius: var(--r-sm); overflow: hidden; margin-top: var(--s-2); }
  .fill { height: 100%; background: var(--accent); border-radius: var(--r-sm); transition: width .4s var(--ease); }
  .live { font-size: .9375rem; overflow-wrap: anywhere; }

  .result { margin-top: var(--s-1); }
  .result.bad { color: var(--danger); }

  details { margin-top: var(--s-1); }
  summary { cursor: pointer; color: var(--muted); width: fit-content; }
  summary:hover { color: var(--ink); }
  details ul { list-style: none; margin: var(--s-2) 0 0; padding: 0; display: grid; gap: var(--s-1); font-size: .9375rem; }

  .actions { display: flex; gap: var(--s-1); margin: var(--s-1) calc(var(--s-2) * -1) 0; }

  .empty { padding-top: var(--s-6); display: grid; gap: var(--s-3); justify-items: start; }
  .empty h2 { font-size: clamp(1.75rem, 5vw, 2.5rem); font-weight: 700; letter-spacing: -0.02em; }
</style>
