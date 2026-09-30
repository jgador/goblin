import { SetupStatus, SetupPhase, SetupStepStatus, isSetupState } from "./contract-values.js";

const message = document.querySelector('#message');
const connection = document.querySelector('#connection');
const steps = document.querySelector('#steps');
const log = document.querySelector('#install-log');
const followLog = document.querySelector('#follow-log');
const logConnection = document.querySelector('#log-connection');
let latest;
let failures = 0;

function render(state) {
  if (latest?.logGeneration === state.logGeneration && state.revision < latest.revision) return;
  latest = state;
  message.textContent = state.message;
  document.querySelector('#recovery').hidden = state.status !== SetupStatus.Failed;
  document.querySelector('h1').textContent = state.status === SetupStatus.Failed ? 'Setup needs attention.' : 'Getting things ready.';
  steps.replaceChildren(...state.steps.map((step, index) => {
    const row = document.createElement('li');
    row.dataset.status = step.status;
    const indicator = document.createElement('span');
    indicator.className = 'indicator';
    indicator.setAttribute('aria-hidden', 'true');
    indicator.textContent = step.status === SetupStepStatus.Complete ? '✓' : step.status === SetupStepStatus.Failed ? '!' : step.status === SetupStepStatus.Running ? '' : String(index + 1);
    const label = document.createElement('span');
    label.className = 'label';
    label.textContent = step.label;
    const status = document.createElement('span');
    status.className = 'status';
    status.textContent = ({ [SetupStepStatus.Waiting]: 'Waiting', [SetupStepStatus.Running]: 'Running', [SetupStepStatus.Complete]: 'Complete', [SetupStepStatus.Failed]: 'Failed' })[step.status];
    row.append(indicator, label, status);
    return row;
  }));
  updateElapsed();
  renderLog(state);
}

function renderLog(state) {
  if (!Array.isArray(state.logs) || !state.logs.length) return;
  const previousScroll = log.scrollTop;
  log.replaceChildren(...state.logs.map(entry => {
    const row = document.createElement('p');
    const time = new Date(entry.timestamp).toLocaleTimeString([], { hour12: false });
    row.textContent = `${time} · Attempt ${entry.attempt}${entry.step ? ` · ${entry.step}` : ''}\n${entry.message}`;
    return row;
  }));
  log.scrollTop = followLog.checked ? log.scrollHeight : previousScroll;
}
log.addEventListener('scroll', () => {
  if (log.scrollHeight - log.scrollTop - log.clientHeight > 20) followLog.checked = false;
});
followLog.addEventListener('change', () => {
  if (followLog.checked) log.scrollTop = log.scrollHeight;
});

const events = new EventSource('/setup/events');
events.addEventListener('progress', event => {
  try {
    const state = JSON.parse(event.data);
    if (!isSetupState(state)) return;
    render(state);
    logConnection.textContent = state.status === SetupStatus.Failed ? 'Installation stopped' : state.status === SetupStatus.Ready ? 'Complete' : 'Live';
  } catch { logConnection.textContent = 'Reconnecting…'; }
});
events.addEventListener('error', () => {
  // Bounded server batches close normally. Show a gap only when status also
  // cannot be reached; EventSource resumes automatically with its last event ID.
  if (failures) logConnection.textContent = 'Reconnecting…';
});
window.addEventListener('pagehide', () => events.close());

function updateElapsed() {
  if (!latest) return;
  const end = latest.status === SetupStatus.Failed || latest.status === SetupStatus.Ready ? Date.parse(latest.updatedAt) : Date.now();
  const seconds = Math.max(0, Math.floor((end - Date.parse(latest.startedAt)) / 1000));
  document.querySelector('#elapsed').textContent = `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
}

async function poll() {
  try {
    const response = await fetch('/setup/status', { cache: 'no-store', signal: AbortSignal.timeout(5000) });
    if (!response.ok) throw new Error('Status unavailable');
    const state = await response.json();
    if (!isSetupState(state)) throw new Error('Unexpected status');
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
    connection.textContent = latest?.phase === SetupPhase.Activating ? 'Connecting to Goblin…' : 'Reconnecting… Installation continues on your server.';
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
