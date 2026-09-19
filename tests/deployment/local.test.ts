import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { cp, mkdir, mkdtemp, readFile, rm, unlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

const runner = resolve("deploy/local/install.py");
const loadRunner = `
import importlib.util, sys
from pathlib import Path
sys.path.insert(0, str(Path(sys.argv[1]).parent))
spec = importlib.util.spec_from_file_location('local_install', sys.argv[1])
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
`;

test("local reset and database access refuse another checkout or runner before invoking services", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "goblin-local-owner-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  execFileSync("python3", ["-c", loadRunner + `
import json
from unittest.mock import patch
root = Path(sys.argv[2])
module.STATE = root / 'local'
module.OWNER = root / 'owner.json'
module.CONTROL_LOCK = root / 'control.lock'
def unexpected(*args, **kwargs):
    raise AssertionError('Refused reset must not invoke system commands')
module.run = unexpected
for command in (['reset', '--yes'], ['database']):
    for config in ({'mode': 'direct', 'repo': '/another-checkout'}, {'mode': 'vm', 'repo': str(module.REPO)}):
        module.OWNER.write_text(json.dumps(config))
        with patch('sys.argv', [sys.argv[1], *command]), patch('os.geteuid', return_value=0):
            try:
                module.main()
                raise AssertionError('Must refuse an unowned installation')
            except RuntimeError as error:
                assert 'another runner or checkout' in str(error)
        assert json.loads(module.OWNER.read_text()) == config
module.OWNER.unlink()
try:
    module.owned_config()
    raise AssertionError('Missing ownership must not permit reset')
except RuntimeError:
    pass
module.OWNER.write_text(json.dumps({'mode': 'direct', 'repo': str(module.REPO), 'http_port': 8788}))
assert module.origin(module.owned_config()) == 'http://localhost:8788'
`, runner, root]);
});

test("local database access refuses occupied ports, persists a loopback endpoint, and preserves live connections on rerun", async t => {
  const root = await mkdtemp(join(tmpdir(), "goblin-local-database-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  execFileSync("python3", ["-c", loadRunner + `
import json, socket
from types import SimpleNamespace
root = Path(sys.argv[2])
module.SYSTEMD = root / 'systemd'
module.SYSTEMD.mkdir()
module.OWNER = root / 'owner.json'
config = {'mode': 'direct', 'repo': str(module.REPO), 'http_port': 8788}
module.OWNER.write_text(json.dumps(config))
module.active = lambda unit: unit == 'k3s.service'
calls = []
def run(args, **kwargs):
    args = [str(arg) for arg in args]
    calls.append(args)
    return SimpleNamespace(stdout='10.43.23.45' if 'service' in args and 'get' in args else '')
module.run = run
with socket.socket() as occupied:
    occupied.bind(('127.0.0.1', 0))
    occupied.listen()
    port = occupied.getsockname()[1]
    try:
        module.configure_database(config, port)
        raise AssertionError('Must not replace an occupied listener')
    except RuntimeError as error:
        assert 'occupied' in str(error)
    assert calls == []
    assert json.loads(module.OWNER.read_text()) == config
module.configure_database(config, port)
unit = (module.SYSTEMD / module.DATABASE_SOCKET).read_text()
assert f'ListenStream=127.0.0.1:{port}' in unit and f'ListenStream=[::1]:{port}' in unit
assert '0.0.0.0' not in unit
service = (module.SYSTEMD / module.DATABASE_SERVICE).read_text()
assert 'systemd-socket-proxyd 10.43.23.45:5432' in service
assert 'DynamicUser=yes' in service and 'port-forward' not in service
assert json.loads(module.OWNER.read_text())['postgres_port'] == port
assert (module.SYSTEMD / module.DATABASE_SOCKET).stat().st_mode & 0o777 == 0o644
assert any('--kubeconfig=/etc/rancher/k3s/k3s.yaml' in args for args in calls)
calls.clear()
module.active = lambda unit: unit in ('k3s.service', module.DATABASE_SOCKET)
module.configure_database(config, None)
assert not any(args[:2] == ['systemctl', 'stop'] for args in calls)
assert ['systemctl', 'enable', '--now', module.DATABASE_SOCKET] in calls
module.stop()
assert ['systemctl', 'disable', '--now', module.DATABASE_SOCKET] in calls
assert ['systemctl', 'stop', module.DATABASE_SERVICE] in calls
assert (root / 'paused').exists()
`, runner, root]);
});

test("local source snapshots include edits and exclude ignored local installation data and credentials", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "goblin-local-source-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const repo = join(root, "repo");
  await mkdir(repo);
  execFileSync("git", ["init", "-q", repo]);
  const ignore = await readFile(".gitignore", "utf8");
  await writeFile(join(repo, ".gitignore"), ignore);
  await mkdir(join(repo, ".goblin-secrets"));
  await writeFile(join(repo, ".goblin-secrets/.gitkeep"), "");
  await writeFile(join(repo, ".goblin-secrets/owner-password"), "test-only ignored verifier");
  await writeFile(join(repo, "tracked.cs"), "old contents");
  await writeFile(join(repo, "deleted.cs"), "removed");
  execFileSync("git", ["-C", repo, "add", "."]);
  await writeFile(join(repo, "tracked.cs"), "current contents");
  await unlink(join(repo, "deleted.cs"));
  await writeFile(join(repo, "new.cs"), "new source");
  await writeFile(join(repo, ".env"), "test-only ignored data");
  await mkdir(join(repo, ".goblin-local"));
  await writeFile(join(repo, ".goblin-local/login-password"), "test-only ignored credential");
  const archive = join(root, "source.tar.gz");
  const output = execFileSync("python3", ["-c", loadRunner + `
import json, tarfile
module.snapshot_source(Path(sys.argv[2]), Path(sys.argv[3]))
with tarfile.open(sys.argv[3]) as archive:
    print(json.dumps({item.name: archive.extractfile(item).read().decode() for item in archive.getmembers()}))
`, runner, repo, archive], { encoding: "utf8" });
  assert.deepEqual(JSON.parse(output), {
    "goblin/.gitignore": ignore,
    "goblin/.goblin-secrets/.gitkeep": "",
    "goblin/new.cs": "new source",
    "goblin/tracked.cs": "current contents",
  });
});

test("source snapshots refuse files reached through symlinks", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "goblin-local-symlink-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  execFileSync("python3", ["-c", loadRunner + `
import subprocess
root = Path(sys.argv[2])
repo = root / 'repo'
repo.mkdir()
subprocess.run(['git', 'init', '-q', str(repo)], check=True)
directory = repo / 'tracked'
directory.mkdir()
(directory / 'file').write_text('tracked source')
subprocess.run(['git', '-C', str(repo), 'add', '.'], check=True)
directory.rename(root / 'external')
directory.symlink_to(root / 'external', target_is_directory=True)
try:
    module.snapshot_source(repo, root / 'snapshot.tar.gz')
    raise AssertionError('A symlink directory must not be followed')
except RuntimeError:
    pass
directory.unlink()
(repo / 'link').symlink_to(root / 'external/file')
try:
    module.snapshot_source(repo, root / 'snapshot.tar.gz')
    raise AssertionError('A symlink file must not be followed')
except RuntimeError:
    pass
`, runner, root]);
});

test("building a local bundle does not overwrite generated Azure template inputs", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "goblin-local-bundle-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const azure = join(root, "deploy/azure");
  const branding = join(root, "assets/branding/svg");
  await mkdir(azure, { recursive: true });
  await mkdir(branding, { recursive: true });
  await cp("assets/branding/svg/icon-light.svg", join(branding, "icon-light.svg"));
  await cp("deploy/azure/build-setup-bundle.py", join(azure, "build-setup-bundle.py"));
  await cp("deploy/azure/setup", join(azure, "setup"), { recursive: true });
  await cp("deploy/azure/install-app.sh", join(azure, "install-app.sh"));
  for (const name of ["setup-bundle.b64", "setup-bundle.sha256"]) await writeFile(join(azure, name), "existing generated input\n");
  execFileSync("python3", [join(azure, "build-setup-bundle.py"), "--output-only", "--output", join(root, "local.pyz")]);
  for (const name of ["setup-bundle.b64", "setup-bundle.sha256"])
    assert.equal(await readFile(join(azure, name), "utf8"), "existing generated input\n");
  assert.ok((await readFile(join(root, "local.pyz"))).length > 0);
});

test("local bootstrap rendering uses the real Azure script and bundle with a verifier supplied separately", async () => {
  const output = execFileSync("python3", ["-c", loadRunner + `
import base64, hashlib
bundle = b'local-test-bundle'
script = module.render_bootstrap(Path(sys.argv[2]), bundle, 'goblin.local.test', 'test-source')
assert 'GOBLIN_PASSWORD_HASH_FILE' in script
assert base64.b64encode(bundle).decode() in script
assert hashlib.sha256(bundle).hexdigest() in script
assert '__GOBLIN_' not in script
print(script)
`, runner, resolve("deploy/azure")], { encoding: "utf8" });
  execFileSync("bash", ["-n"], { input: output });
});
