<script>
  import { app, dismissToast } from './store.svelte.js';
</script>

<div class="toasts" role="status" aria-live="polite">
  {#each app.toasts as toast (toast.id)}
    <div class="toast" class:error={toast.kind === 'error'}>
      <span>{toast.message}</span>
      <button class="btn quiet" onclick={() => dismissToast(toast.id)} aria-label="Dismiss">Close</button>
    </div>
  {/each}
</div>

<style>
  .toasts {
    position: fixed; z-index: 20;
    left: var(--s-4); right: var(--s-4);
    bottom: calc(var(--s-4) + env(safe-area-inset-bottom));
    display: grid; gap: var(--s-2); justify-items: center; pointer-events: none;
  }
  .toast {
    pointer-events: auto;
    display: flex; align-items: center; gap: var(--s-3);
    max-width: 34rem; padding: var(--s-2) var(--s-2) var(--s-2) var(--s-4);
    background: var(--ink); color: var(--bg);
    border-radius: var(--r-md);
    animation: rise .25s var(--ease);
  }
  .toast.error { background: var(--danger); color: var(--bg-raised); }
  .toast :global(.btn.quiet) { color: inherit; opacity: .8; }
  .toast :global(.btn.quiet:hover) { background: transparent; opacity: 1; color: inherit; }
  @keyframes rise { from { opacity: 0; transform: translateY(8px); } }
</style>
