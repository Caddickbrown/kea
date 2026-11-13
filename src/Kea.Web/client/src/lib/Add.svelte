<script>
  import { api } from './api.js';
  import { parseLink } from './format.js';
  import { app, notify, setView } from './store.svelte.js';

  let text = $state('');
  let format = $state('');
  let start = $state(1);
  let end = $state('');
  let comicFolders = $state(true);
  let chapterFolders = $state(false);
  let busy = $state(false);

  // The server sends the list of formats; start on its first.
  // Starts on the format used last time, or the server's first.
  $effect(() => {
    const formats = app.config?.formats ?? [];
    if (format || !formats.length) return;
    const last = localStorage.getItem('kea.format');
    format = formats.some((f) => f.value === last) ? last : formats[0].value;
  });
  $effect(() => { if (format) localStorage.setItem('kea.format', format); });

  const isImages = $derived(format === 'images');
  $effect(() => { chapterFolders = isImages; });

  const links = $derived.by(() => {
    const seen = new Set();
    return text.split(/\r?\n/).map(parseLink).filter(Boolean).map((link) => {
      if (link.ok && seen.has(link.titleNo)) return { ...link, ok: false, reason: 'Already listed above.' };
      if (link.ok) seen.add(link.titleNo);
      return link;
    });
  });

  const good = $derived(links.filter((l) => l.ok));
  const max = $derived(app.config?.maxUrlsPerJob ?? 25);
  const tooMany = $derived(good.length > max);

  const endNumber = $derived(end.trim() === '' || /^end$/i.test(end.trim()) ? null : Number(end));
  const rangeError = $derived.by(() => {
    if (!Number.isInteger(Number(start)) || Number(start) < 1) return 'The first chapter is 1 or higher.';
    if (endNumber !== null && (!Number.isInteger(endNumber) || endNumber < Number(start))) return 'The last chapter has to come after the first.';
    return '';
  });

  const ready = $derived(good.length > 0 && !tooMany && !rangeError && !busy);

  async function submit(event) {
    event?.preventDefault();
    if (!ready) return;

    busy = true;
    try {
      const job = await api('/api/jobs', {
        method: 'POST',
        body: JSON.stringify({
          urls: good.map((l) => l.raw),
          format,
          start: Number(start),
          end: endNumber,
          comicFolders,
          chapterFolders,
        }),
      });

      const skipped = links.length - good.length;
      notify(`Queued ${job.comics.join(', ')}.${skipped ? ` Skipped ${skipped} line${skipped === 1 ? '' : 's'} that did not look right.` : ''}`);
      text = '';
      setView('queue');
    } catch (e) {
      notify(e.message, 'error');
    } finally {
      busy = false;
    }
  }

  function onKey(event) {
    if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') submit(event);
  }
</script>

<form class="add" onsubmit={submit}>
  <h2>Paste a webtoon.</h2>
  <p class="lead muted">
    Use the comic&rsquo;s chapter list page, the one with <code>title_no</code> in the address. One link per line.
  </p>

  <label class="sr" for="links">Comic links</label>
  <!-- svelte-ignore a11y_autofocus -->
  <textarea
    id="links" class="field links" rows="4" spellcheck="false" autofocus
    placeholder="https://www.webtoons.com/en/canvas/step-by-step/list?title_no=1127362"
    bind:value={text} onkeydown={onKey}
  ></textarea>

  {#if links.length}
    <ul class="found" aria-label="What was understood">
      {#each links as link}
        <li class:bad={!link.ok}>
          {#if link.ok}
            <span class="name">{link.name}</span>
            <span class="faint num">title {link.titleNo}</span>
          {:else}
            <span class="name">{link.raw.length > 56 ? link.raw.slice(0, 55) + '\u2026' : link.raw}</span>
            <span class="why">{link.reason}</span>
          {/if}
        </li>
      {/each}
    </ul>
  {/if}

  {#if tooMany}<p class="problem" role="alert">That is {good.length} comics; one go takes at most {max}.</p>{/if}

  <fieldset class="options">
    <legend>Options</legend>

    <div class="sentence">
      <label>
        <span>Save as</span>
        <select class="field" bind:value={format}>
          {#each app.config?.formats ?? [] as f}<option value={f.value}>{f.label}</option>{/each}
        </select>
      </label>
      <label>
        <span>From chapter</span>
        <input class="field num" type="number" min="1" step="1" bind:value={start} />
      </label>
      <label>
        <span>To chapter</span>
        <input class="field num" type="text" inputmode="numeric" placeholder="the end" bind:value={end} />
      </label>
    </div>

    <div class="checks">
      <label><input type="checkbox" bind:checked={comicFolders} /> A folder for each comic</label>
      <label class:off={!isImages}>
        <input type="checkbox" bind:checked={chapterFolders} disabled={!isImages} />
        <span>
          A folder for each chapter
          {#if !isImages}<span class="faint note">Only when keeping loose images.</span>{/if}
        </span>
      </label>
    </div>

    {#if rangeError}<p class="problem" role="alert">{rangeError}</p>{/if}
  </fieldset>

  <div class="go">
    <button class="btn primary" type="submit" disabled={!ready}>
      {#if busy}Adding{:else if good.length > 1}Add {good.length} comics to the queue{:else}Add to the queue{/if}
    </button>
    <span class="faint hint">or press Ctrl+Enter</span>
  </div>
</form>

<style>
  .add { display: grid; gap: var(--s-3); padding-top: var(--s-6); }
  h2 { font-size: clamp(2rem, 6vw, 3rem); font-weight: 700; letter-spacing: -0.03em; line-height: 1.05; }
  .lead { max-width: 36rem; margin-bottom: var(--s-2); }
  code { font-family: var(--font); font-weight: 600; color: var(--ink); }

  .links { font-size: 1.0625rem; line-height: 1.45; resize: vertical; min-height: 7rem; }

  .found { list-style: none; margin: 0; padding: 0; }
  .found li {
    display: flex; flex-wrap: wrap; align-items: baseline; justify-content: space-between; gap: var(--s-1) var(--s-4);
    padding: var(--s-2) 0; border-bottom: 1px solid var(--line);
  }
  .name { font-weight: 600; overflow-wrap: anywhere; }
  .found .bad .name { font-weight: 400; color: var(--muted); }
  .why { color: var(--danger); }

  .problem { color: var(--danger); }

  .options { border: 0; border-top: 1px solid var(--line); padding: var(--s-4) 0 0; margin: var(--s-3) 0 0; display: grid; gap: var(--s-4); }
  .options legend { font-weight: 600; padding: 0 var(--s-2) 0 0; float: left; width: 100%; margin-bottom: var(--s-3); }
  .options legend + * { clear: both; }

  .sentence { display: grid; grid-template-columns: repeat(auto-fit, minmax(9.5rem, 1fr)); gap: var(--s-3); }
  .sentence label { display: grid; gap: var(--s-1); }
  .sentence span { color: var(--muted); font-size: .9375rem; }

  .checks { display: grid; gap: var(--s-2); }
  .checks label { display: flex; align-items: center; gap: var(--s-2); }
  .checks label.off { color: var(--muted); }
  .note { display: block; font-size: .9375rem; }
  input[type='checkbox'] { width: 1.125rem; height: 1.125rem; accent-color: var(--accent); }

  .go { display: flex; align-items: center; gap: var(--s-4); flex-wrap: wrap; margin-top: var(--s-2); }
  .go .btn { min-height: 46px; padding: 0 var(--s-5); }
  .hint { font-size: .9375rem; }
  @media (pointer: coarse) { .hint { display: none; } }
</style>
