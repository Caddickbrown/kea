export function size(bytes) {
  if (!bytes) return '';
  const units = ['B', 'KB', 'MB', 'GB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit += 1; }
  return `${value < 10 && unit > 0 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;
}

const rtf = new Intl.RelativeTimeFormat('en', { numeric: 'auto' });

/** "2 minutes ago", "yesterday", then a plain date for anything older than a week. */
export function ago(iso, now = Date.now()) {
  if (!iso) return '';
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return '';

  const seconds = Math.round((then - now) / 1000);
  const abs = Math.abs(seconds);
  if (abs < 45) return 'just now';
  if (abs < 3600) return rtf.format(Math.round(seconds / 60), 'minute');
  if (abs < 86400) return rtf.format(Math.round(seconds / 3600), 'hour');
  if (abs < 7 * 86400) return rtf.format(Math.round(seconds / 86400), 'day');
  return new Date(then).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
}

export function chapterRange(job) {
  return job.end ? `chapters ${job.start}\u2013${job.end}` : job.start > 1 ? `from chapter ${job.start}` : 'every chapter';
}

/** The comic's folder inside the library, taken from the first chapter file a job saved. */
export function jobFolder(job) {
  const first = (job.chapters || []).find((c) => c.file);
  return first && first.file.includes('/') ? first.file.slice(0, first.file.lastIndexOf('/')) : '';
}

/** Reads a pasted line as a Webtoons comic link. Advisory only: the server decides in the end. */
export function parseLink(raw) {
  const text = raw.trim();
  if (!text) return null;

  let url;
  try { url = new URL(text); } catch { return { raw: text, ok: false, reason: 'That is not a link.' }; }

  if (!/(^|\.)webtoons\.com$/i.test(url.hostname)) return { raw: text, ok: false, reason: 'Only webtoons.com links work.' };
  if (/\/viewer/i.test(url.pathname)) return { raw: text, ok: false, reason: 'That is one episode. Use the comic\u2019s list page.' };

  const titleNo = url.searchParams.get('title_no');
  if (!titleNo || !/^\d+$/.test(titleNo)) return { raw: text, ok: false, reason: 'The link has no title_no. Open the comic\u2019s list page.' };

  const parts = url.pathname.split('/').filter(Boolean);
  const slug = parts.length >= 2 ? parts[parts.length - 2] : '';
  const name = slug ? slug.replace(/-/g, ' ').replace(/^./, (c) => c.toUpperCase()) : `Title ${titleNo}`;

  return { raw: text, ok: true, titleNo, name };
}
