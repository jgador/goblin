const version = document.querySelector('#version');
const status = document.querySelector('#status');
const deploy = document.querySelector('#deploy');
const notes = document.querySelector('#notes');
const pattern = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-preview\.[1-9]\d*)?$/;
function choose() {
  const selected = version.value;
  if (!selected) {
    deploy.removeAttribute('href');
    deploy.setAttribute('aria-disabled', 'true');
    notes.hidden = true;
    status.textContent = 'Choose a Goblin version to continue.';
    return;
  }
  const base = new URL(`versions/${selected}/`, location.href);
  deploy.href = `https://portal.azure.com/#create/Microsoft.Template/uri/${encodeURIComponent(new URL('azuredeploy.portal.json', base).href)}/createUIDefinitionUri/${encodeURIComponent(new URL('createUiDefinition.json', base).href)}`;
  deploy.removeAttribute('aria-disabled');
  notes.href = `https://github.com/jgador/goblin/releases/tag/goblin-v${selected}`;
  notes.hidden = false;
  status.textContent = selected.includes('-preview.') ? 'Preview release. Features and setup may change.' : 'Stable release.';
}
async function load() {
  try {
    const response = await fetch('releases.json', { cache: 'no-store' });
    if (!response.ok) throw new Error('Unavailable');
    const catalog = await response.json();
    if (catalog.schemaVersion !== 1 || !Array.isArray(catalog.releases) || !catalog.releases.every(entry => pattern.test(entry.version))) throw new Error('Invalid catalog');
    version.replaceChildren(new Option('Choose a version', ''));
    for (const entry of catalog.releases) version.add(new Option(`${entry.version}${entry.version === catalog.recommended ? ' (recommended)' : ''}`, entry.version));
    const requested = new URL(location.href).searchParams.get('version');
    const selected = requested ?? catalog.recommended;
    version.value = catalog.releases.some(entry => entry.version === selected) ? selected : '';
    version.disabled = catalog.releases.length === 0;
    choose();
    if (!catalog.releases.length) status.textContent = 'The first Goblin release is being prepared. Check back soon.';
    else if (requested && version.value !== requested) status.textContent = 'That version is unavailable. Choose a published version.';
  } catch {
    status.textContent = 'Could not load Goblin releases. Refresh the page to try again.';
    version.replaceChildren(new Option('Releases unavailable', ''));
  }
}
version.addEventListener('change', choose);
load();
