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
spec = importlib.util.spec_from_file_location('local_install', sys.argv[1])
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
`;

test("local reset refuses an installation owned by another checkout or runner before invoking services", async (t) => {
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
for config in ({'mode': 'direct', 'repo': '/another-checkout'}, {'mode': 'vm', 'repo': str(module.REPO)}):
    module.OWNER.write_text(json.dumps(config))
    with patch('sys.argv', [sys.argv[1], 'reset', '--yes']), patch('os.geteuid', return_value=0):
        try:
            module.main()
            raise AssertionError('Reset must refuse an unowned installation')
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

test("local source snapshots include edits and exclude ignored local installation data and credentials", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "goblin-local-source-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const repo = join(root, "repo");
  await mkdir(repo);
  execFileSync("git", ["init", "-q", repo]);
  await writeFile(join(repo, ".gitignore"), ".goblin-local/\n.env\n");
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
    "goblin/.gitignore": ".goblin-local/\n.env\n",
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
  await cp("deploy/azure/build-setup-bundle.py", join(root, "build-setup-bundle.py"));
  await cp("deploy/azure/setup", join(root, "setup"), { recursive: true });
  await cp("deploy/azure/install-app.sh", join(root, "install-app.sh"));
  for (const name of ["setup-bundle.b64", "setup-bundle.sha256"]) await writeFile(join(root, name), "existing generated input\n");
  execFileSync("python3", [join(root, "build-setup-bundle.py"), "--output-only", "--output", join(root, "local.pyz")]);
  for (const name of ["setup-bundle.b64", "setup-bundle.sha256"])
    assert.equal(await readFile(join(root, name), "utf8"), "existing generated input\n");
  assert.ok((await readFile(join(root, "local.pyz"))).length > 0);
});

test("local bootstrap rendering embeds credentials as data and uses the real bundle", async () => {
  const output = execFileSync("python3", ["-c", loadRunner + `
import base64, hashlib
password = "local-test-'quoted'-$HOME-$(touch PWNED)"
bundle = b'local-test-bundle'
script = module.render_bootstrap(Path(sys.argv[2]), bundle, 'goblin.local.test', 'test-source', password)
assert password not in script
assert base64.b64encode(password.encode()).decode() in script
assert base64.b64encode(bundle).decode() in script
assert hashlib.sha256(bundle).hexdigest() in script
assert '__GOBLIN_' not in script
print(script)
`, runner, resolve("deploy/azure")], { encoding: "utf8" });
  execFileSync("bash", ["-n"], { input: output });
});
