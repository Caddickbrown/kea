'use strict';

/* Kea web UI. Plain ES modules-free JavaScript so the app deploys by copying wwwroot. */

const $ = (id) => document.getElementById(id);

const state = {
  jobs: new Map(),
  libraryPath: '',
  view: 'download',
  tokenRequired: false,
};

/* ---------- helpers ---------- */

async function api(path, options = {}) {
  const response = await fetch(path, {
    headers: { 'Content-Type': 'application/json' },
    ...options,
  });

  let body = null;
  const text = await response.text();
  if (text) {
    try { body = JSON.parse(text); } catch { body = null; }
  }

  if (!response.ok) {
    const error = new Error((body && body.error) || `Request failed (${response.status})`);
    error.status = response.status;
    throw error;
  }

  return body;
}

function showError(element, message) {
  if (!message) {
    element.hidden = true;
    element.textContent = '';
    return;
  }
  element.textContent = message;
  element.hidden = false;
}

function formatSize(bytes) {
  if (!bytes) return '';
  const units = ['B', 'KB', 'MB', 'GB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${value < 10 && unit > 0 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;
}

function formatDate(iso) {
  if (!iso) return '';
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleString();
}

/* ---------- views ---------- */

const VIEWS = ['download', 'jobs', 'library'];

function setView(name, updateUrl = true) {
  if (!VIEWS.includes(name)) name = 'download';

  state.view = name;
  for (const tab of document.querySelectorAll('.tab')) {
    tab.classList.toggle('active', tab.dataset.view === name);
  }
  for (const view of document.querySelectorAll('.view')) {
    view.hidden = view.id !== `view-${name}`;
  }

  // Keep the tab in the URL so it can be bookmarked and survives a reload.
  if (updateUrl) {
    const url = new URL(window.location.href);
    url.searchParams.set('view', name);
    window.history.replaceState({}, '', url);
  }

  if (name === 'library') loadLibrary(state.libraryPath);
}

function viewFromUrl() {
  const requested = new URL(window.location.href).searchParams.get('view');
  return VIEWS.includes(requested) ? requested : 'download';
}

/* ---------- jobs ---------- */

function renderJobs() {
  const list = $('jobs-list');
  const jobs = [...state.jobs.values()].sort(
    (a, b) => new Date(b.createdAt) - new Date(a.createdAt));

  $('jobs-empty').hidden = jobs.length > 0;

  const active = jobs.filter((j) => j.status === 'queued' || j.status === 'running').length;
  const badge = $('jobs-badge');
  badge.hidden = active === 0;
  badge.textContent = String(active);

  list.replaceChildren(...jobs.map(renderJob));
}

function renderJob(job) {
  const element = document.createElement('div');
  // The status also drives the progress bar colour: a full green bar on a failed job reads
  // as success.
  element.className = `job job-${job.status}`;

  const running = job.status === 'queued' || job.status === 'running';
  const percent = Math.round((job.progress || 0) * 100);
  const failures = (job.chapters || []).filter((c) => c.error);

  const head = document.createElement('div');
  head.className = 'job-head';

  const title = document.createElement('span');
  title.className = 'job-title';
  title.textContent = job.comics.join(', ');

  const status = document.createElement('span');
  status.className = `status status-${job.status}`;
  status.textContent = job.status;

  const meta = document.createElement('span');
  meta.className = 'job-meta';
  const range = job.end ? `ch ${job.start}–${job.end}` : `ch ${job.start}–end`;
  meta.textContent = `${job.format} · ${range} · ${formatDate(job.createdAt)}`;

  head.append(title, status, meta);

  const bar = document.createElement('div');
  bar.className = 'bar';
  const fill = document.createElement('div');
  fill.style.width = `${percent}%`;
  bar.append(fill);

  const statusText = document.createElement('div');
  statusText.className = 'job-status-text';
  statusText.textContent = job.error ? `${job.statusText} — ${job.error}` : job.statusText;

  element.append(head, bar, statusText);

  if (failures.length > 0) {
    const details = document.createElement('details');
    details.className = 'failures';
    const summary = document.createElement('summary');
    summary.textContent = `${failures.length} chapter(s) failed`;
    const ul = document.createElement('ul');
    for (const failure of failures.slice(0, 50)) {
      const li = document.createElement('li');
      li.textContent = `${failure.comic} chapter ${failure.chapterNumber}: ${failure.error}`;
      ul.append(li);
    }
    details.append(summary, ul);
    element.append(details);
  }

  const actions = document.createElement('div');
  actions.className = 'job-actions';

  if (running) {
    const cancel = document.createElement('button');
    cancel.className = 'small';
    cancel.textContent = 'Cancel';
    cancel.addEventListener('click', async () => {
      cancel.disabled = true;
      try { await api(`/api/jobs/${job.id}/cancel`, { method: 'POST' }); }
      catch (error) { alert(error.message); cancel.disabled = false; }
    });
    actions.append(cancel);
  } else {
    const remove = document.createElement('button');
    remove.className = 'small';
    remove.textContent = 'Dismiss';
    remove.addEventListener('click', async () => {
      remove.disabled = true;
      try {
        await api(`/api/jobs/${job.id}`, { method: 'DELETE' });
        state.jobs.delete(job.id);
        renderJobs();
      } catch (error) { alert(error.message); remove.disabled = false; }
    });
    actions.append(remove);

    if (job.succeeded > 0) {
      const open = document.createElement('button');
      open.className = 'small';
      open.textContent = 'Show in library';
      open.addEventListener('click', () => {
        const first = (job.chapters || []).find((c) => c.file);
        const folder = first && first.file.includes('/')
          ? first.file.slice(0, first.file.lastIndexOf('/'))
          : '';
        state.libraryPath = folder;
        setView('library');
      });
      actions.append(open);
    }
  }

  element.append(actions);
  return element;
}

async function loadJobs() {
  try {
    const jobs = await api('/api/jobs');
    state.jobs = new Map(jobs.map((job) => [job.id, job]));
    renderJobs();
  } catch (error) {
    console.error('Could not load jobs', error);
  }
}

/* ---------- library ---------- */

async function loadLibrary(path) {
  try {
    const listing = await api(`/api/library?path=${encodeURIComponent(path || '')}`);
    state.libraryPath = listing.path;
    renderLibrary(listing);
  } catch (error) {
    // A folder can vanish between listing and clicking; fall back to the root.
    if (path) { state.libraryPath = ''; loadLibrary(''); return; }
    console.error('Could not load library', error);
  }
}

function renderLibrary(listing) {
  const crumbs = $('breadcrumbs');
  crumbs.replaceChildren();
  listing.breadcrumbs.forEach((crumb, index) => {
    if (index > 0) {
      const sep = document.createElement('span');
      sep.textContent = '/';
      crumbs.append(sep);
    }
    const button = document.createElement('button');
    button.textContent = crumb.name;
    button.addEventListener('click', () => loadLibrary(crumb.path));
    crumbs.append(button);
  });

  const body = $('library-body');
  $('library-empty').hidden = listing.entries.length > 0;
  $('library-table').hidden = listing.entries.length === 0;

  body.replaceChildren(...listing.entries.map((entry) => {
    const row = document.createElement('tr');

    const nameCell = document.createElement('td');
    const name = document.createElement('div');
    name.className = 'name';

    const icon = document.createElement('span');
    icon.textContent = entry.isDirectory ? '\u{1F4C1}' : '\u{1F4C4}';
    name.append(icon);

    if (entry.isDirectory) {
      const link = document.createElement('button');
      link.className = 'link';
      link.textContent = entry.name;
      link.addEventListener('click', () => loadLibrary(entry.path));
      name.append(link);
    } else {
      const link = document.createElement('a');
      link.href = `/api/library/file?path=${encodeURIComponent(entry.path)}`;
      link.textContent = entry.name;
      name.append(link);
    }

    nameCell.append(name);

    const sizeCell = document.createElement('td');
    sizeCell.className = 'right';
    sizeCell.textContent = entry.isDirectory ? '' : formatSize(entry.size);

    const dateCell = document.createElement('td');
    dateCell.className = 'right';
    dateCell.textContent = formatDate(entry.modified);

    const actionCell = document.createElement('td');
    actionCell.className = 'right';
    const remove = document.createElement('button');
    remove.className = 'small';
    remove.textContent = 'Delete';
    remove.addEventListener('click', async () => {
      if (!confirm(`Delete "${entry.name}"? This cannot be undone.`)) return;
      try {
        await api(`/api/library?path=${encodeURIComponent(entry.path)}`, { method: 'DELETE' });
        loadLibrary(state.libraryPath);
      } catch (error) { alert(error.message); }
    });
    actionCell.append(remove);

    row.append(nameCell, sizeCell, dateCell, actionCell);
    return row;
  }));
}

/* ---------- live updates ---------- */

function connectEvents() {
  const indicator = $('connection');
  const source = new EventSource('/api/events');

  const onJob = (event) => {
    const job = JSON.parse(event.data);
    state.jobs.set(job.id, job);
    renderJobs();
    // A finished job usually means new files, so keep the library honest.
    if (state.view === 'library' && event.type === 'updated') loadLibrary(state.libraryPath);
  };

  source.addEventListener('created', onJob);
  source.addEventListener('updated', onJob);
  source.addEventListener('progress', onJob);
  source.addEventListener('removed', (event) => {
    const job = JSON.parse(event.data);
    state.jobs.delete(job.id);
    renderJobs();
  });

  source.onopen = () => {
    indicator.textContent = 'live';
    indicator.className = 'pill live';
  };

  source.onerror = () => {
    indicator.textContent = 'reconnecting';
    indicator.className = 'pill down';
    // EventSource reconnects on its own; re-sync state once it does.
    setTimeout(loadJobs, 3000);
  };
}

/* ---------- startup ---------- */

async function start() {
  const config = await api('/api/config');
  state.tokenRequired = config.tokenRequired;

  if (config.tokenRequired && !config.authenticated) {
    $('login').hidden = false;
    $('login-token').focus();
    return;
  }

  $('login').hidden = true;
  $('app').hidden = false;
  $('logout').hidden = !config.tokenRequired;

  const format = $('format');
  format.replaceChildren(...config.formats.map((item) => {
    const option = document.createElement('option');
    option.value = item.value;
    option.textContent = item.label;
    return option;
  }));

  syncChapterFolders();
  await loadJobs();
  connectEvents();
  setView(viewFromUrl(), false);
}

/* "a folder per chapter" only means anything when images are kept as separate files;
   every other format writes a single file per chapter. */
function syncChapterFolders() {
  const isImages = $('format').value === 'images';
  const checkbox = $('chapter-folders');
  checkbox.disabled = !isImages;
  checkbox.checked = isImages;
}

document.addEventListener('DOMContentLoaded', () => {
  for (const tab of document.querySelectorAll('.tab')) {
    tab.addEventListener('click', () => setView(tab.dataset.view));
  }

  $('format').addEventListener('change', syncChapterFolders);

  $('login-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    showError($('login-error'), '');
    try {
      await api('/api/auth/login', {
        method: 'POST',
        body: JSON.stringify({ token: $('login-token').value }),
      });
      window.location.reload();
    } catch (error) {
      showError($('login-error'), error.message);
    }
  });

  $('logout').addEventListener('click', async () => {
    await api('/api/auth/logout', { method: 'POST' });
    window.location.reload();
  });

  $('download-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    showError($('download-error'), '');

    const urls = $('urls').value.split('\n').map((l) => l.trim()).filter(Boolean);
    const endRaw = $('end').value.trim();
    const end = endRaw === '' || endRaw.toLowerCase() === 'end' ? null : Number(endRaw);

    if (end !== null && (!Number.isInteger(end) || end < 1)) {
      showError($('download-error'), 'The end chapter must be a whole number, or the word "end".');
      return;
    }

    const submit = $('submit');
    submit.disabled = true;
    try {
      await api('/api/jobs', {
        method: 'POST',
        body: JSON.stringify({
          urls,
          format: $('format').value,
          start: Number($('start').value) || 1,
          end,
          comicFolders: $('comic-folders').checked,
          chapterFolders: $('chapter-folders').checked,
        }),
      });

      $('urls').value = '';
      await loadJobs();
      setView('jobs');
    } catch (error) {
      showError($('download-error'), error.message);
    } finally {
      submit.disabled = false;
    }
  });

  start().catch((error) => {
    document.body.textContent = `Kea could not start: ${error.message}`;
  });
});
