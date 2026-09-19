import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

const load = `
import os, sys
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0, sys.argv[1])
import password
root = Path(sys.argv[2])
path = root / '.goblin-secrets/owner-password'
`;

test("local password setup confirms hidden input, stores only a private verifier, and reuses it", async t => {
  const root = await mkdtemp(join(tmpdir(), "goblin-password-setup-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  execFileSync("python3", ["-c", load + `
import base64, hashlib
chosen = " a-'quoted'-$HOME-π "
with patch.dict(os.environ, {}, clear=True), patch('sys.stdin.isatty', return_value=True), \
     patch('getpass.getpass', side_effect=[chosen, chosen]) as prompt:
    assert password.ensure_password(path) == path
    assert [call.args[0] for call in prompt.call_args_list] == ['Goblin password: ', 'Confirm Goblin password: ']
verifier = password.read_verifier(path)
assert chosen not in verifier and base64.b64encode(chosen.encode()).decode() not in verifier
algorithm, iterations, salt, digest = verifier.strip().split('$')
assert algorithm == 'pbkdf2-sha256' and iterations == '600000'
assert hashlib.pbkdf2_hmac('sha256', chosen.encode(), base64.b64decode(salt), int(iterations), 32) == base64.b64decode(digest)
assert path.stat().st_mode & 0o777 == 0o600
assert path.parent.stat().st_mode & 0o777 == 0o700
assert list(path.parent.iterdir()) == [path]
with patch.dict(os.environ, {'GOBLIN_LOCAL_PASSWORD': 'must-not-replace'}), \
     patch('getpass.getpass', side_effect=AssertionError('Must not prompt again')):
    password.ensure_password(path)
assert path.read_text() == verifier
with patch.dict(os.environ, {}, clear=True), patch('sys.stdin.isatty', return_value=True), \
     patch('getpass.getpass', side_effect=['new-password', 'mismatch']):
    try:
        password.ensure_password(path, replace=True)
        raise AssertionError('A mismatch must preserve the previous password')
    except RuntimeError:
        pass
assert path.read_text() == verifier
with patch.dict(os.environ, {}, clear=True), patch('sys.stdin.isatty', return_value=True), \
     patch('getpass.getpass', side_effect=['replacement-test', 'replacement-test']):
    password.ensure_password(path, replace=True)
assert password.read_verifier(path) != verifier
assert path.stat().st_mode & 0o777 == 0o600
`, resolve("deploy/local"), root]);
});

test("invalid, mismatched, missing, and corrupt local credentials fail without a fallback or partial file", async t => {
  const root = await mkdtemp(join(tmpdir(), "goblin-password-rejected-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  execFileSync("python3", ["-c", load + `
for inputs in (['first', 'second'], ['', ''], ['   ', '   '], ['x' * 129] * 2, ['line\\nbreak'] * 2):
    with patch.dict(os.environ, {}, clear=True), patch('sys.stdin.isatty', return_value=True), patch('getpass.getpass', side_effect=inputs):
        try:
            password.ensure_password(path)
            raise AssertionError('Invalid password must fail')
        except RuntimeError as error:
            assert all(value not in str(error) for value in inputs if value.strip())
    assert not path.exists() and not path.parent.exists()
with patch.dict(os.environ, {}, clear=True), patch('sys.stdin.isatty', return_value=False):
    try:
        password.ensure_password(path)
        raise AssertionError('Non-interactive setup must require an explicit password')
    except RuntimeError as error:
        assert 'setup:password' in str(error)
path.parent.mkdir()
path.write_text('broken-verifier')
with patch.dict(os.environ, {'GOBLIN_LOCAL_PASSWORD': 'must-not-replace'}):
    try:
        password.ensure_password(path)
        raise AssertionError('A corrupt verifier must not be replaced')
    except RuntimeError as error:
        assert 'invalid' in str(error)
assert path.read_text() == 'broken-verifier'
`, resolve("deploy/local"), root]);
});

test("the local launcher passes the verifier to the real app without forwarding the original password", async t => {
  const root = await mkdtemp(join(tmpdir(), "goblin-password-launch-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  execFileSync("python3", ["-c", load + `
import start
with patch.dict(os.environ, {'GOBLIN_LOCAL_PASSWORD': 'launcher-test'}, clear=True):
    password.ensure_password(path)
    with patch('start.ensure_password', return_value=path), patch('os.execvpe') as launch:
        start.main()
    command, args, environment = launch.call_args.args
    assert command == 'dotnet' and args[1].endswith('Goblin.Web.dll')
    assert environment['GOBLIN_PASSWORD_HASH_FILE'] == str(path)
    assert 'GOBLIN_LOCAL_PASSWORD' not in environment
    assert 'launcher-test' not in str(args)
with patch.dict(os.environ, {'GOBLIN_PASSWORD_HASH_FILE': str(path)}, clear=True), \
     patch('start.ensure_password', side_effect=AssertionError('Explicit configuration must win')), patch('os.execvpe') as launch:
    start.main()
    assert launch.call_args.args[2]['GOBLIN_PASSWORD_HASH_FILE'] == str(path)
path.write_text('broken-verifier')
with patch.dict(os.environ, {'GOBLIN_PASSWORD_HASH_FILE': str(path)}, clear=True), patch('os.execvpe') as launch:
    try:
        start.main()
        raise AssertionError('Invalid configuration must prevent launch')
    except RuntimeError:
        pass
    launch.assert_not_called()
`, resolve("deploy/local"), root]);
});

test("the full local installer imports retained verifiers and removes its legacy plaintext password", async t => {
  const root = await mkdtemp(join(tmpdir(), "goblin-password-migrate-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  execFileSync("python3", ["-c", load + `
import install
install.STATE = root / 'state'
install.INSTALL = root / 'vm'
install.STATE.mkdir()
password.PASSWORD_FILE = path
retained = install.INSTALL / 'install/private/owner-password'
retained.parent.mkdir(parents=True)
verifier = password.hash_password('existing-local-test') + '\\n'
retained.write_text(verifier)
legacy = install.STATE / 'login-password'
legacy.write_text('existing-local-test\\n')
with patch('getpass.getpass', side_effect=AssertionError('Existing installation must keep its password')):
    assert install.prepare_password() == path
assert path.read_text() == verifier
assert not legacy.exists()
path.unlink()
retained.unlink()
legacy.write_text('partial-bootstrap-test\\n')
install.prepare_password()
assert password.read_verifier(path).startswith('pbkdf2-sha256$600000$')
assert 'partial-bootstrap-test' not in path.read_text()
assert not legacy.exists()
`, resolve("deploy/local"), root]);
});

test("a completed local installation loads a changed verifier without rebuilding or changing it again on resume", async t => {
  const root = await mkdtemp(join(tmpdir(), "goblin-password-resume-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  execFileSync("python3", ["-c", load + `
import install, json
from types import SimpleNamespace
from contextlib import nullcontext
install.STATE = root / 'state'
install.INSTALL = root / 'vm'
install.OWNER = install.INSTALL / 'local-test/config.json'
install.SYSTEMD = root / 'systemd'
install.SYSTEMD.mkdir()
(install.SYSTEMD / 'k3s.service').touch()
password.PASSWORD_FILE = path
retained = install.INSTALL / 'install/private/owner-password'
retained.parent.mkdir(parents=True)
previous = password.hash_password('previous-test') + '\\n'
retained.write_text(previous)
(install.INSTALL / 'bootstrap-status').write_text('setup-ready\\n')
config = {'http_port': 8788}
with patch.dict(os.environ, {'GOBLIN_LOCAL_PASSWORD': 'replacement-test'}):
    password.ensure_password(path)
calls = []
def run(args, **kwargs):
    calls.append((args, kwargs))
    return SimpleNamespace(stdout=json.dumps({'kind': 'Secret'}))
with patch('install.preflight', return_value=config), patch('install.configure_forwarder'), \
     patch('install.read_status', return_value={'status': 'ready'}), patch('install.run', side_effect=run), \
     patch('urllib.request.urlopen', return_value=nullcontext(SimpleNamespace(status=200))):
    install.start(SimpleNamespace(http_port=None))
    assert retained.read_text() == path.read_text()
    assert any(f'--from-file=owner-password={path}' in args for args, _ in calls)
    assert any(args[:4] == ['k3s', 'kubectl', 'delete', 'pod'] for args, _ in calls)
    assert all('replacement-test' not in str(args) for args, _ in calls)
    calls.clear()
    install.start(SimpleNamespace(http_port=None))
    assert not any(args[0] == 'k3s' for args, _ in calls)
`, resolve("deploy/local"), root], { stdio: ["pipe", "pipe", "pipe"] });
});

test("Git includes only the empty secret directory placeholder", () => {
  const paths = [".goblin-secrets/.gitkeep", ".goblin-secrets/owner-password", ".goblin-secrets/nested/credential", ".goblin-secrets/.owner-password-temporary"];
  const result = execFileSync("git", ["check-ignore", "--no-index", ...paths], { encoding: "utf8" }).trim().split("\n");
  assert.deepEqual(result, paths.slice(1));
  assert.equal(execFileSync("python3", ["-c", "from pathlib import Path; assert Path('.goblin-secrets/.gitkeep').read_bytes() == b''"]).length, 0);
});
