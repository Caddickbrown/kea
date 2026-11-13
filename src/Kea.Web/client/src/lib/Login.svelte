<script>
  import Mark from './Mark.svelte';
  import { api } from './api.js';

  let token = $state('');
  let error = $state('');
  let busy = $state(false);

  async function submit(event) {
    event.preventDefault();
    busy = true;
    error = '';
    try {
      await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ token }) });
      window.location.reload();
    } catch (e) {
      error = e.message;
      busy = false;
    }
  }
</script>

<main class="login">
  <form onsubmit={submit}>
    <h1><Mark size={28} /> Kea</h1>
    <p class="muted">This server wants an access token before it lets you in.</p>

    <label for="token">Access token</label>
    <!-- svelte-ignore a11y_autofocus -->
    <input id="token" class="field" type="password" autocomplete="current-password" required autofocus bind:value={token} />

    {#if error}<p class="error" role="alert">{error}</p>{/if}

    <button class="btn primary" type="submit" disabled={busy || !token}>{busy ? 'Checking' : 'Sign in'}</button>
  </form>
</main>

<style>
  .login { min-height: 100dvh; display: grid; place-items: center; padding: var(--s-4); }
  form { width: min(22rem, 100%); display: grid; gap: var(--s-3); }
  h1 { display: flex; align-items: center; gap: var(--s-2); font-size: 2rem; font-weight: 700; letter-spacing: -0.02em; }
  label { font-weight: 500; margin-top: var(--s-3); }
  .error { color: var(--danger); }
</style>
