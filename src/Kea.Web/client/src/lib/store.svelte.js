import { api } from './api.js';

/** Everything the screens share. One object so it can be reassigned freely under runes. */
export const app = $state({
  config: null,
  view: 'add',
  jobs: [],
  connection: 'connecting',
  toasts: [],
  libraryPath: '',
  libraryTick: 0,
  tick: Date.now(),
});

const VIEWS = ['add', 'queue', 'library'];

export function setView(name, { push = true } = {}) {
  if (!VIEWS.includes(name)) name = 'add';
  app.view = name;
  if (push) {
    const url = new URL(window.location.href);
    url.searchParams.set('view', name);
    window.history.replaceState({}, '', url);
  }
}

export function viewFromUrl() {
  const asked = new URL(window.location.href).searchParams.get('view');
  // The old three-tab layout called these "download" and "jobs".
  const renamed = { download: 'add', jobs: 'queue' }[asked] || asked;
  return VIEWS.includes(renamed) ? renamed : 'add';
}

export function openInLibrary(path) {
  app.libraryPath = path || '';
  setView('library');
}

/* ---- notices ---- */

let nextToast = 1;

export function notify(message, kind = 'info') {
  const id = nextToast++;
  app.toasts = [...app.toasts, { id, message, kind }];
  setTimeout(() => dismissToast(id), kind === 'error' ? 9000 : 4500);
}

export function dismissToast(id) {
  app.toasts = app.toasts.filter((t) => t.id !== id);
}

/* ---- jobs ---- */

function upsert(job) {
  const rest = app.jobs.filter((j) => j.id !== job.id);
  app.jobs = [job, ...rest].sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
}

export async function loadJobs() {
  try {
    app.jobs = await api('/api/jobs');
  } catch (error) {
    console.error('Could not load jobs', error);
  }
}

export const isActive = (job) => job.status === 'queued' || job.status === 'running';

let source;

export function connectEvents() {
  if (source) source.close();
  source = new EventSource('/api/events');

  const onJob = (event) => {
    const job = JSON.parse(event.data);
    upsert(job);
    // A finished job usually means new files.
    if (event.type === 'updated' && !isActive(job)) app.libraryTick += 1;
  };

  source.addEventListener('created', onJob);
  source.addEventListener('updated', onJob);
  source.addEventListener('progress', onJob);
  source.addEventListener('removed', (event) => {
    const job = JSON.parse(event.data);
    app.jobs = app.jobs.filter((j) => j.id !== job.id);
  });

  source.onopen = () => { app.connection = 'live'; };
  source.onerror = () => {
    app.connection = 'reconnecting';
    // EventSource reconnects by itself; catch up on whatever was missed once it does.
    setTimeout(loadJobs, 3000);
  };

  // Keeps "2 minutes ago" honest without re-fetching anything.
  setInterval(() => { app.tick = Date.now(); }, 30000);
}

/* ---- theme ---- */

const saved = (() => {
  try { return JSON.parse(localStorage.getItem('kea.theme') || '{}'); } catch { return {}; }
})();

export const theme = $state({ mode: saved.mode || 'auto', tint: saved.tint || 'olive' });

export function applyTheme() {
  const root = document.documentElement;
  if (theme.mode === 'auto') delete root.dataset.mode; else root.dataset.mode = theme.mode;
  if (theme.tint === 'olive') delete root.dataset.tint; else root.dataset.tint = theme.tint;
  try { localStorage.setItem('kea.theme', JSON.stringify({ mode: theme.mode, tint: theme.tint })); } catch { /* private mode */ }
}
