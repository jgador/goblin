const message = document.querySelector('#message');
const connection = document.querySelector('#connection');
const steps = document.querySelector('#steps');
let latest;
let failures = 0;

function render(state) {
  latest = state;
  message.textContent = state.message;
  document.querySelector('#recovery').hidden = state.status !== 'failed';
  document.querySelector('h1').textContent = state.status === 'failed' ? 'Setup needs attention.' : 'Getting things ready.';
  steps.replaceChildren(...state.steps.map((step, index) => {
    const row = document.createElement('li');
    row.dataset.status = step.status;
    const indicator = document.createElement('span');
    indicator.className = 'indicator';
    indicator.setAttribute('aria-hidden', 'true');
    indicator.textContent = step.status === 'complete' ? '✓' : step.status === 'failed' ? '!' : step.status === 'running' ? '' : String(index + 1);
    const label = document.createElement('span');
    label.className = 'label';
    label.textContent = step.label;
    const status = document.createElement('span');
    status.className = 'status';
    status.textContent = ({ waiting: 'Waiting', running: 'Running', complete: 'Complete', failed: 'Failed' })[step.status];
    row.append(indicator, label, status);
    return row;
  }));
  updateElapsed();
}

function updateElapsed() {
  if (!latest) return;
  const end = latest.status === 'failed' || latest.status === 'ready' ? Date.parse(latest.updatedAt) : Date.now();
  const seconds = Math.max(0, Math.floor((end - Date.parse(latest.startedAt)) / 1000));
  document.querySelector('#elapsed').textContent = `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
}

async function poll() {
  try {
    const response = await fetch('/setup/status', { cache: 'no-store', signal: AbortSignal.timeout(5000) });
    if (!response.ok) throw new Error('Status unavailable');
    const state = await response.json();
    if (state.version !== 1 || !Array.isArray(state.steps)) throw new Error('Unexpected status');
    if (state.publicUrl) {
      const target = new URL(state.publicUrl);
      if (!['http:', 'https:'].includes(target.protocol) || target.username || target.password) throw new Error('Unexpected workspace URL');
      // Move an IP/localhost visit to the configured origin while setup is still
      // serving it. Progress and readiness checks then use the final ingress host,
      // even if the browser misses the last status update before the handoff.
      if (target.origin !== location.origin) {
        location.replace(target.origin + '/');
        return;
      }
    }
    render(state);
    failures = 0;
    connection.textContent = '';
  } catch {
    failures++;
    connection.textContent = latest?.phase === 'activating' ? 'Connecting to Goblin…' : 'Reconnecting… Installation continues on your server.';
    // The Kubernetes application exposes this endpoint; the setup UI never does.
    // Check across two polls so a transient route change does not trigger a reload loop.
    try {
      const response = await fetch('/readyz', { cache: 'no-store', signal: AbortSignal.timeout(5000) });
      if (response.ok && (await response.json()).ready === true && failures >= 2) location.replace('/');
    } catch { /* A short connection gap is expected while port 80 changes owners. */ }
  } finally {
    setTimeout(poll, 3000);
  }
}
setInterval(updateElapsed, 1000);
poll();
