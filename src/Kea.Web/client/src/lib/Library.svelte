<script>
  import { api } from './api.js';
  import { ago, size } from './format.js';
  import { app, notify } from './store.svelte.js';

  let listing = $state(null);
  let filter = $state('');
  let sort = $state('name');
  let confirming = $state('');
  let loading = $state(true);

  async function load(path) {
    loading = true;
    try {
      listing = await api(`/api/library?path=${encodeURIComponent(path || '')}`);
      app.libraryPath = listing.path;
      filter = '';
      confirming = '';
    } catch (e) {
      // A folder can vanish between listing and clicking; fall back to the top.
      if (path) { app.libraryPath = ''; return load(''); }
      notify(e.message, 'error');
    } finally {
      loading = false;
    }
  }

  // Loads on arrival, and again whenever a download finishes.
  $effect(() => {
    app.libraryTick;
    load(app.libraryPath);
  });

  const entries = $derived.by(() => {
    const q = filter.trim().toLowerCase();
    const items = (listing?.entries ?? []).filter((e) => !q || e.name.toLowerCase().includes(q));
    const byName = (a, b) => a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });
    const byNew = (a, b) => new Date(b.modified) - new Date(a.modified);
    return items.sort((a, b) => (b.isDirectory - a.isDirectory) || (sort === 'new' ? byNew(a, b) : byName(a, b)));
  });

  const cbzCount = $derived((listing?.entries ?? []).filter((e) =>
    !e.isDirectory && /\.cbz$/i.test(e.name) && e.name.toLowerCase() !== `${listing.breadcrumbs.at(-1)?.name}.cbz`.toLowerCase()).length);
  const intervals = [[0, 'Never'], [1, 'Every day'], [7, 'Every week'], [30, 'Every month']];

  async function recheck() {
    try {
      await api(`/api/library/recheck?path=${encodeURIComponent(listing.path)}`, { method: 'POST' });
      notify('Looking for new chapters. It is in the queue.');
    } catch (e) { notify(e.message, 'error'); }
  }

  async function schedule(days) {
    try {
      await api(`/api/library/schedule?path=${encodeURIComponent(listing.path)}`, { method: 'POST', body: JSON.stringify({ intervalDays: Number(days) }) });
      listing.recheckIntervalDays = Number(days);
    } catch (e) { notify(e.message, 'error'); }
  }

  const extension = (name) => (name.includes('.') ? name.slice(name.lastIndexOf('.') + 1) : '');
  const info = (entry) => [
    entry.isDirectory ? '' : extension(entry.name),
    entry.isDirectory ? '' : size(entry.size),
    ago(entry.modified, app.tick),
  ].filter(Boolean).join(' \u00b7 ');
  const href = (entry) => `/api/library/file?path=${encodeURIComponent(entry.path)}`;

  async function remove(entry) {
    try {
      await api(`/api/library?path=${encodeURIComponent(entry.path)}`, { method: 'DELETE' });
      notify(`Deleted ${entry.name}.`);
      await load(listing.path);
    } catch (e) {
      notify(e.message, 'error');
    }
  }

  let search = $state();
  function onKey(event) {
    if (event.key === '/' && document.activeElement?.tagName !== 'INPUT') { event.preventDefault(); search?.focus(); }
  }
</script>

<svelte:window onkeydown={onKey} />

<div class="library">
  {#if listing}
    <nav class="crumbs" aria-label="Where you are in the library">
      {#each listing.breadcrumbs as crumb, i}
        {#if i > 0}<span class="faint" aria-hidden="true">/</span>{/if}
        <button class="crumb" aria-current={i === listing.breadcrumbs.length - 1 ? 'page' : undefined} onclick={() => load(crumb.path)}>{i === 0 ? 'Library' : crumb.name}</button>
      {/each}
    </nav>

    <div class="tools">
      <label class="sr" for="filter">Filter this folder</label>
      <input id="filter" class="field" type="search" placeholder="Filter this folder (press /)" bind:value={filter} bind:this={search} />
      <div class="sorts" role="group" aria-label="Sort by">
        <button class="btn quiet" aria-pressed={sort === 'name'} onclick={() => (sort = 'name')}>Name</button>
        <button class="btn quiet" aria-pressed={sort === 'new'} onclick={() => (sort = 'new')}>Newest</button>
      </div>
    </div>

    {#if cbzCount > 1 || listing.canRecheck}
      <section class="book" aria-label="This comic">
        {#if cbzCount > 1}
          <div>
            <h2>Make it one book</h2>
            <p class="muted">Joins the {cbzCount} chapters here into a single file, in order. Nothing is downloaded again.</p>
          </div>
          <!-- A form post lets the browser stream a big file straight to disk instead of holding it in memory. -->
          <form method="post" action={`/api/library/repack?path=${encodeURIComponent(listing.path)}&format=cbz`}><button class="btn" type="submit">One CBZ</button></form>
          <form method="post" action={`/api/library/repack?path=${encodeURIComponent(listing.path)}&format=pdf`}><button class="btn" type="submit">One PDF</button></form>
        {/if}
        {#if listing.canRecheck}
          <div class="upd">
            <button class="btn" onclick={recheck}>Check for new chapters</button>
            <label>Check automatically
              <select class="field" value={listing.recheckIntervalDays} onchange={(e) => schedule(e.currentTarget.value)}>
                {#each intervals as [v, l]}<option value={v}>{l}</option>{/each}
              </select>
            </label>
          </div>
        {/if}
      </section>
    {/if}

    {#if entries.length === 0}
      <p class="empty muted">
        {#if filter}Nothing here matches &ldquo;{filter}&rdquo;.{:else}This folder is empty.{/if}
      </p>
    {:else}
      <ul class="rows">
        {#each entries as entry (entry.path)}
          <li class="row">
            {#if entry.isDirectory}
              <button class="name folder" onclick={() => load(entry.path)}>
                {entry.name}<span class="chev" aria-hidden="true">&rsaquo;</span>
              </button>
            {:else}
              <a class="name" href={href(entry)}>{entry.name}</a>
            {/if}

            <span class="info faint num">{info(entry)}</span>

            <span class="acts">
              {#if confirming === entry.path}
                <span class="sure">Delete{entry.isDirectory ? ' with everything inside' : ''}?</span>
                <button class="btn quiet danger" onclick={() => remove(entry)}>Yes, delete</button>
                <button class="btn quiet" onclick={() => (confirming = '')}>Keep</button>
              {:else}
                {#if !entry.isDirectory}<a class="btn quiet" href={href(entry)} download>Download</a>{/if}
                <button class="btn quiet danger" onclick={() => (confirming = entry.path)}>Delete</button>
              {/if}
            </span>
          </li>
        {/each}
      </ul>
    {/if}
  {:else if loading}
    <p class="empty muted">Opening the library&hellip;</p>
  {/if}
</div>

<style>
  .library { padding-top: var(--s-5); display: grid; gap: var(--s-4); }

  .crumbs { display: flex; flex-wrap: wrap; align-items: baseline; gap: var(--s-2); font-size: 1.25rem; font-weight: 700; letter-spacing: -0.01em; }
  .crumb { background: none; border: 0; padding: 0; cursor: pointer; font: inherit; color: var(--muted); }
  .crumb:hover { color: var(--ink); }
  .crumb[aria-current='page'] { color: var(--ink); cursor: default; }

  .tools { display: flex; gap: var(--s-3); align-items: center; }
  .tools .field { flex: 1; }
  .sorts { display: flex; }
  .sorts .btn[aria-pressed='true'] { color: var(--ink); background: var(--accent-soft); }

  .rows { list-style: none; margin: 0; padding: 0; border-top: 1px solid var(--ink); }
  .row {
    display: grid; grid-template-columns: 1fr auto; align-items: center; gap: 0 var(--s-4);
    padding: var(--s-2) 0; border-bottom: 1px solid var(--line);
    grid-template-areas: 'name acts' 'info acts';
  }
  .name { grid-area: name; font-weight: 600; text-align: left; overflow-wrap: anywhere; text-decoration: none; background: none; border: 0; padding: 0; cursor: pointer; font-family: inherit; font-size: 1rem; color: inherit; width: fit-content; }
  .name:hover { text-decoration: underline; text-decoration-color: var(--accent); text-underline-offset: 4px; }
  .chev { margin-left: var(--s-2); color: var(--faint); font-weight: 400; }
  .info { grid-area: info; font-size: .9375rem; }
  .acts { grid-area: acts; display: flex; align-items: center; gap: var(--s-1); justify-content: flex-end; flex-wrap: wrap; }
  .sure { color: var(--danger); font-size: .9375rem; }
  .acts :global(a.btn) { text-decoration: none; }

  .empty { padding: var(--s-5) 0; }

  .book { display: flex; flex-wrap: wrap; align-items: center; gap: var(--s-3) var(--s-4); padding: var(--s-3) 0; border-bottom: 1px solid var(--line); }
  .book h2 { font-size: 1.0625rem; font-weight: 700; }
  .book > div:first-child { flex: 1 1 16rem; }
  .upd { display: flex; flex-wrap: wrap; align-items: center; gap: var(--s-3); flex-basis: 100%; }
  .upd label { display: flex; align-items: center; gap: var(--s-2); color: var(--muted); }
  .upd .field { width: auto; }

  @media (min-width: 40rem) {
    .row { grid-template-columns: 1fr auto auto; grid-template-areas: 'name info acts'; }
    .info { text-align: right; }
  }
  /* Delete and download sit quietly until you point at the row; always shown on touch screens. */
  @media (hover: hover) and (min-width: 40rem) {
    .row:not(:hover):not(:focus-within) .acts { opacity: 0; }
    .row .acts { transition: opacity .15s var(--ease); }
  }
</style>
