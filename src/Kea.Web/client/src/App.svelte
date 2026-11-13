<script>
  import { onMount } from 'svelte';
  import { api } from './lib/api.js';
  import Mark from './lib/Mark.svelte';
  import Login from './lib/Login.svelte';
  import Add from './lib/Add.svelte';
  import Queue from './lib/Queue.svelte';
  import Library from './lib/Library.svelte';
  import Toasts from './lib/Toasts.svelte';
  import {
    app, theme, applyTheme, setView, viewFromUrl, loadJobs, connectEvents, isActive,
  } from './lib/store.svelte.js';

  let ready = $state(false);
  let needsLogin = $state(false);

  const running = $derived(app.jobs.filter(isActive).length);

  onMount(async () => {
    applyTheme();
    try {
      app.config = await api('/api/config');
    } catch (e) {
      app.connection = 'down';
      ready = true;
      return;
    }

    if (app.config.tokenRequired && !app.config.authenticated) {
      needsLogin = true;
      ready = true;
      return;
    }

    await loadJobs();
    connectEvents();
    setView(viewFromUrl(), { push: false });
    ready = true;
  });

  async function signOut() {
    try { await api('/api/auth/logout', { method: 'POST' }); } catch { /* the cookie goes either way */ }
    window.location.reload();
  }

  const tabs = [
    { id: 'add', label: 'Add' },
    { id: 'queue', label: 'Queue' },
    { id: 'library', label: 'Library' },
  ];

  function onKey(event) {
    if (event.ctrlKey || event.metaKey || event.altKey) return;
    const tag = document.activeElement?.tagName;
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
    const hit = { '1': 'add', '2': 'queue', '3': 'library' }[event.key];
    if (hit) setView(hit);
  }

  const modes = [['auto', 'Auto'], ['light', 'Light'], ['dark', 'Dark']];
  const tints = [['olive', 'Olive'], ['flash', 'Flash'], ['slate', 'Slate']];
</script>

<svelte:window onkeydown={onKey} />

{#if !ready}
  <p class="boot muted">Waking up&hellip;</p>
{:else if needsLogin}
  <Login />
{:else if !app.config}
  <main class="page down">
    <h1><Mark size={28} /> Kea</h1>
    <p>The server did not answer. Check that it is running, then reload.</p>
    <button class="btn" onclick={() => window.location.reload()}>Reload</button>
  </main>
{:else}
  <div class="shell">
    <header class="page bar">
      <a class="brand" href="/" aria-label="Kea"><Mark /><span>Kea</span></a>

      <nav class="tabs" aria-label="Sections">
        {#each tabs as tab}
          <button class="tab" aria-current={app.view === tab.id ? 'page' : undefined} onclick={() => setView(tab.id)}>
            {tab.label}{#if tab.id === 'queue' && running}<span class="count num">{running}</span>{/if}
          </button>
        {/each}
      </nav>

      <span class="conn" class:down={app.connection !== 'live'} title="Live updates from the server">
        <span class="dot" aria-hidden="true"></span>{app.connection === 'live' ? 'Live' : app.connection}
      </span>
    </header>

    <main class="page">
      {#if app.view === 'add'}<Add />
      {:else if app.view === 'queue'}<Queue />
      {:else}<Library />{/if}
    </main>

    <footer class="page foot">
      <p class="faint">Downloads are for personal, offline reading.</p>

      <div class="prefs">
        <div role="group" aria-label="Colour scheme">
          {#each modes as [id, label]}
            <button class="btn quiet" aria-pressed={theme.mode === id} onclick={() => { theme.mode = id; applyTheme(); }}>{label}</button>
          {/each}
        </div>
        <div role="group" aria-label="Accent colour">
          {#each tints as [id, label]}
            <button class="btn quiet" aria-pressed={theme.tint === id} onclick={() => { theme.tint = id; applyTheme(); }}>{label}</button>
          {/each}
        </div>
        {#if app.config.tokenRequired}<button class="btn quiet" onclick={signOut}>Sign out</button>{/if}
      </div>
    </footer>
  </div>
  <Toasts />
{/if}

<style>
  .boot { padding: var(--s-6) var(--s-4); }
  .down { padding-top: var(--s-6); display: grid; gap: var(--s-3); justify-items: start; }
  .down h1 { display: flex; align-items: center; gap: var(--s-2); font-size: 2rem; }

  .shell { min-height: 100dvh; display: grid; grid-template-rows: auto 1fr auto; }

  .bar {
    display: flex; align-items: center; gap: var(--s-5);
    padding-top: calc(var(--s-4) + env(safe-area-inset-top)); padding-bottom: var(--s-3);
    border-bottom: 1px solid var(--line);
  }
  .brand { display: inline-flex; align-items: center; gap: var(--s-2); text-decoration: none; font-size: 1.375rem; font-weight: 700; letter-spacing: -0.02em; }

  .tabs { display: flex; gap: var(--s-1); }
  .tab {
    position: relative; background: none; border: 0; cursor: pointer; padding: var(--s-2) var(--s-3);
    border-radius: var(--r-md); color: var(--muted); font-weight: 500;
    transition: color .15s var(--ease), background-color .15s var(--ease);
  }
  .tab:hover { color: var(--ink); background: var(--accent-soft); }
  .tab[aria-current='page'] { color: var(--ink); font-weight: 700; }
  .tab[aria-current='page']::after {
    content: ''; position: absolute; left: var(--s-3); right: var(--s-3); bottom: -13px; height: 2px; background: var(--accent);
  }
  .count { margin-left: var(--s-2); padding: 0 var(--s-2); border-radius: var(--r-sm); background: var(--accent); color: var(--accent-ink); font-size: .8125rem; font-weight: 600; }

  .conn { margin-left: auto; display: inline-flex; align-items: center; gap: var(--s-2); font-size: .9375rem; color: var(--muted); }
  .dot { width: 8px; height: 8px; border-radius: var(--r-lg); background: var(--accent); }
  .conn.down .dot { background: var(--danger); }

  main.page { padding-bottom: var(--s-7); }

  .foot {
    display: flex; flex-wrap: wrap; justify-content: space-between; gap: var(--s-3) var(--s-5);
    padding-top: var(--s-4); padding-bottom: calc(var(--s-5) + env(safe-area-inset-bottom));
    border-top: 1px solid var(--line); font-size: .9375rem;
  }
  .prefs { display: flex; flex-wrap: wrap; gap: var(--s-1) var(--s-4); }
  .prefs > div { display: flex; }
  .prefs :global(.btn[aria-pressed='true']) { color: var(--ink); background: var(--accent-soft); }

  @media (max-width: 34rem) {
    .bar { gap: var(--s-3); flex-wrap: wrap; }
    .brand span { display: none; }
    .conn { margin-left: auto; }
  }
</style>
