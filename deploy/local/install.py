#!/usr/bin/env python3
"""Install Goblin directly in Ubuntu/WSL and follow setup at localhost:8788."""

import argparse
import base64
import fcntl
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tarfile
import time
import urllib.request

import password as local_password

REPO = Path(__file__).resolve().parents[2]
STATE = REPO / '.goblin-local'
INSTALL = Path('/var/lib/goblin')
OWNER = INSTALL / 'local-test/config.json'
SYSTEMD = Path('/etc/systemd/system')
SETUP = Path('/opt/goblin/setup')
PROXY = Path('/opt/goblin/local/forward.py')
CONTROL_LOCK = Path('/run/goblin-local.lock')
DATABASE_SOCKET = 'goblin-local-postgres.socket'
DATABASE_SERVICE = 'goblin-local-postgres.service'


def run(args, **kwargs):
    return subprocess.run([str(arg) for arg in args], check=True, **kwargs)


def private_write(path, content):
    path.write_text(content)
    path.chmod(0o600)


def snapshot_source(repo, destination):
    """Include current Git-visible files, including edits, but never ignored stores."""
    repo = repo.resolve()
    inventory = run(['git', '-C', repo, 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], capture_output=True).stdout
    with tarfile.open(destination, 'w:gz') as archive:
        for name in sorted(set(os.fsdecode(entry) for entry in inventory.split(b'\0') if entry)):
            relative = Path(name)
            if relative.is_absolute() or '..' in relative.parts:
                raise RuntimeError('Unsafe source archive path')
            source = repo / relative
            if not source.exists() and not source.is_symlink():
                continue  # Working-tree deletions remain deleted in the snapshot.
            if source.parent.resolve() != source.parent:
                raise RuntimeError(f'Local source snapshots do not accept symlink directories: {name}')
            if source.is_symlink():
                raise RuntimeError(f'Local source snapshots do not accept symlinks: {name}')
            if not source.is_file():
                raise RuntimeError(f'Unsupported source entry: {name}')
            info = archive.gettarinfo(str(source), arcname='goblin/' + name)
            info.uid = info.gid = 0
            info.uname = info.gname = ''
            with source.open('rb') as stream:
                archive.addfile(info, stream)
    destination.chmod(0o600)


def render_bootstrap(azure, bundle, hostname, source_ref):
    encode = lambda value: base64.b64encode(value.encode()).decode()
    replacements = {
        '__GOBLIN_PASSWORD_HASHER__': (azure / 'hash-password.py').read_text(),
        '__GOBLIN_HOSTNAME_BASE64__': encode(hostname),
        '__GOBLIN_SOURCE_REF_BASE64__': encode(source_ref),
        '__GOBLIN_PASSWORD_BASE64__': '',
        '__GOBLIN_SETUP_BUNDLE_BASE64__': base64.b64encode(bundle).decode(),
        '__GOBLIN_SETUP_BUNDLE_SHA256__': hashlib.sha256(bundle).hexdigest(),
    }
    script = (azure / 'bootstrap.sh').read_text()
    for placeholder, value in replacements.items():
        if script.count(placeholder) != 1:
            raise RuntimeError(f'Unexpected bootstrap placeholder: {placeholder}')
        script = script.replace(placeholder, value)
    return script


def origin(config):
    return f'http://localhost:{config["http_port"]}'


def read_status():
    path = INSTALL / 'install/status.json'
    return json.loads(path.read_text()) if path.exists() else None


def active(unit):
    return subprocess.run(['systemctl', 'is-active', '--quiet', unit]).returncode == 0


def owned_config():
    if not OWNER.exists():
        raise RuntimeError('No direct local installation exists. Run npm run install:local -- start.')
    config = json.loads(OWNER.read_text())
    if config.get('mode') != 'direct' or config.get('repo') != str(REPO):
        raise RuntimeError('This installation belongs to another runner or checkout; refusing to change it.')
    return config


def preflight(port):
    if Path('/proc/1/comm').read_text().strip() != 'systemd':
        raise RuntimeError('Enable systemd in WSL (/etc/wsl.conf: [boot] systemd=true), then restart WSL.')
    if os.uname().machine != 'x86_64' or 'ID=ubuntu' not in Path('/etc/os-release').read_text():
        raise RuntimeError('The local installer requires Ubuntu on x86-64 (Ubuntu 24.04 recommended).')
    for name in ('git', 'curl', 'systemctl', 'flock'):
        if not shutil.which(name):
            raise RuntimeError(f'Missing {name}. Install it before starting Goblin.')
    if not Path('/usr/lib/systemd/systemd-socket-proxyd').exists():
        raise RuntimeError('Missing systemd-socket-proxyd. Install the Ubuntu systemd package.')
    if OWNER.exists():
        config = owned_config()
        if port is not None and port != config['http_port']:
            raise RuntimeError('The existing installation uses a different port. Reset before changing it.')
        return config
    # This runner owns a dedicated cluster. Never adopt or uninstall an existing one.
    for path in (INSTALL, Path('/opt/goblin'), Path('/etc/rancher/k3s'), Path('/var/lib/rancher/k3s'),
                 Path('/etc/kubernetes'), Path('/var/lib/kubelet'), Path('/var/lib/cni'),
                 SYSTEMD / 'k3s.service', SYSTEMD / 'goblin-setup.service',
                 SYSTEMD / 'goblin-installer.service', SYSTEMD / 'goblin-local.socket',
                 SYSTEMD / 'goblin-local.service', SYSTEMD / DATABASE_SOCKET, SYSTEMD / DATABASE_SERVICE):
        if path.exists():
            raise RuntimeError(f'{path} already exists outside this runner. Use a clean WSL distribution for this test.')
    if shutil.which('k3s'):
        raise RuntimeError('K3s is already installed outside this runner. Use a clean WSL distribution for this test.')
    selected_port = port or 8788
    # Traefik eventually owns 80/443. The loopback socket keeps Windows localhost
    # forwarding alive while ownership of port 80 moves from setup to Kubernetes.
    for candidate in (80, 443, selected_port):
        with socket.socket() as listener:
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                listener.bind(('0.0.0.0', candidate))
            except OSError as error:
                raise RuntimeError(f'WSL/Linux port {candidate} is already in use. Stop that listener first.') from error
    return {'version': 2, 'mode': 'direct', 'repo': str(REPO), 'http_port': selected_port}


def configure_forwarder(config):
    PROXY.parent.mkdir(parents=True, exist_ok=True, mode=0o755)
    PROXY.parent.parent.chmod(0o755)
    PROXY.parent.chmod(0o755)
    shutil.copyfile(REPO / 'deploy/local/forward.py', PROXY)
    PROXY.chmod(0o644)
    (SYSTEMD / 'goblin-local.socket').write_text(f'''[Unit]
Description=Goblin local browser port
[Socket]
ListenStream=127.0.0.1:{config['http_port']}
ListenStream=[::1]:{config['http_port']}
BindIPv6Only=ipv6-only
[Install]
WantedBy=sockets.target
''')
    (SYSTEMD / 'goblin-local.service').write_text('''[Unit]
Description=Forward the local browser to Goblin setup or Kubernetes
Requires=goblin-local.socket
After=network.target
[Service]
ExecStart=/usr/bin/python3 /opt/goblin/local/forward.py
DynamicUser=yes
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
''')
    for name in ('goblin-local.socket', 'goblin-local.service'):
        (SYSTEMD / name).chmod(0o644)
    run(['systemctl', 'daemon-reload'])
    run(['systemctl', 'enable', '--now', 'goblin-local.socket'])


def configure_database(config, requested_port):
    """Expose the owned local cluster over raw TCP, independent of kubectl tunnels."""
    port = requested_port or config.get('postgres_port', 55432)
    if not isinstance(port, int) or not 1024 <= port <= 65535 or port == config['http_port']:
        raise RuntimeError('Choose a database port between 1024 and 65535, different from the browser port.')
    if not active('k3s.service'):
        raise RuntimeError('Start the local cluster first: npm run install:local -- start.')
    if 'postgres_port' not in config and any((SYSTEMD / unit).exists() for unit in (DATABASE_SOCKET, DATABASE_SERVICE)):
        raise RuntimeError('Database forwarding units already exist outside this runner; refusing to replace them.')
    if config.get('postgres_port') != port or not active(DATABASE_SOCKET):
        for family, address in ((socket.AF_INET, '127.0.0.1'), (socket.AF_INET6, '::1')):
            with socket.socket(family) as listener:
                listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                if family == socket.AF_INET6:
                    listener.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 1)
                try:
                    listener.bind((address, port))
                except OSError as error:
                    raise RuntimeError(f'Local port {port} is occupied. Stop its existing port-forward or choose another port.') from error
    kubectl = ['k3s', 'kubectl', '--kubeconfig=/etc/rancher/k3s/k3s.yaml']
    run([*kubectl, 'get', 'statefulset', 'goblin-postgres', '-n', 'goblin'], stdout=subprocess.DEVNULL)
    run([*kubectl, 'apply', '-f', REPO / 'deploy/local/postgres-service.yaml'])
    address = run([*kubectl, 'get', 'service', 'goblin-postgres-local', '-n', 'goblin',
                   '-o', 'jsonpath={.spec.clusterIP}'], capture_output=True, text=True).stdout.strip()
    try:
        address = str(ipaddress.IPv4Address(address))
    except ipaddress.AddressValueError as error:
        raise RuntimeError('The local PostgreSQL service has no usable IPv4 cluster address.') from error
    units = {
        DATABASE_SOCKET: f'''[Unit]
Description=Goblin local PostgreSQL port
[Socket]
ListenStream=127.0.0.1:{port}
ListenStream=[::1]:{port}
BindIPv6Only=ipv6-only
[Install]
WantedBy=sockets.target
''',
        DATABASE_SERVICE: f'''[Unit]
Description=Forward local PostgreSQL clients to the Kubernetes service
Requires={DATABASE_SOCKET} k3s.service
After=network.target k3s.service
[Service]
ExecStart=/usr/lib/systemd/systemd-socket-proxyd {address}:5432
DynamicUser=yes
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
''',
    }
    changed = any(not (SYSTEMD / name).exists() or (SYSTEMD / name).read_text() != text for name, text in units.items())
    if changed:
        for name in units:
            if (SYSTEMD / name).exists():
                run(['systemctl', 'stop', name])
        for name, text in units.items():
            (SYSTEMD / name).write_text(text)
            (SYSTEMD / name).chmod(0o644)
    config['postgres_port'] = port
    private_write(OWNER, json.dumps(config, indent=2) + '\n')
    run(['systemctl', 'daemon-reload'])
    run(['systemctl', 'enable', '--now', DATABASE_SOCKET])
    print(f'PostgreSQL is available at localhost:{port}; local access runs in the background. No kubectl port-forward is needed.')


def prepare_password():
    path = local_password.PASSWORD_FILE
    legacy = STATE / 'login-password'
    if not path.exists():
        retained = INSTALL / 'install/private/owner-password'
        if retained.exists():
            local_password.save_verifier(path, local_password.read_verifier(retained))
        elif legacy.exists():
            local_password.save_verifier(path, local_password.hash_password(legacy.read_text().removesuffix('\n')))
    local_password.ensure_password(path)
    # Earlier local installations saved the original password. Once a verifier
    # is retained, that recoverable copy is no longer needed.
    legacy.unlink(missing_ok=True)
    return path


def prepare(config):
    print('Preparing the installation page and current source checkout…', flush=True)
    bundle = STATE / 'goblin-setup.pyz'
    run(['python3', REPO / 'deploy/azure/build-setup-bundle.py', '--output-only', '--output', bundle])
    source = STATE / 'source.tar.gz'
    snapshot_source(REPO, source)
    source_ref = run(['git', '-C', REPO, 'rev-parse', 'HEAD'], capture_output=True, text=True).stdout.strip()
    script = render_bootstrap(REPO / 'deploy/azure', bundle.read_bytes(), 'localhost', source_ref)
    INSTALL.mkdir(mode=0o751, exist_ok=True)
    OWNER.parent.mkdir(mode=0o700, exist_ok=True)
    private_write(OWNER, json.dumps(config, indent=2) + '\n')
    shutil.copyfile(source, OWNER.parent / 'source.tar.gz')
    (OWNER.parent / 'source.tar.gz').chmod(0o600)
    override = SYSTEMD / 'goblin-installer.service.d'
    override.mkdir(mode=0o755, exist_ok=True)
    (override / 'local-test.conf').write_text(f'''[Service]
Environment=GOBLIN_PUBLIC_ORIGIN={origin(config)}
ExecStartPre=/usr/bin/install -D -m 0600 /var/lib/goblin/local-test/source.tar.gz /var/lib/goblin/install/private/work/goblin-source.tar.gz
''')
    (override / 'local-test.conf').chmod(0o644)
    return script


def start(args):
    config = preflight(args.http_port)
    password_file = prepare_password()
    os.environ.pop('GOBLIN_LOCAL_PASSWORD', None)
    bootstrapped = INSTALL / 'bootstrap-status'
    complete_bootstrap = bootstrapped.exists() and bootstrapped.read_text().strip() == 'setup-ready'
    if not complete_bootstrap:
        script = prepare(config)
    configure_forwarder(config)
    if complete_bootstrap:
        state = read_status()
        if (SYSTEMD / 'k3s.service').exists():
            run(['systemctl', 'enable', '--now', 'k3s'])
        retained = INSTALL / 'install/private/owner-password'
        verifier = local_password.read_verifier(password_file)
        if local_password.read_verifier(retained) != verifier:
            if not state or state['status'] != 'ready':
                raise RuntimeError('Finish or retry the current installation before changing its password verifier.')
            secret = run(['k3s', 'kubectl', 'create', 'secret', 'generic', 'goblin-owner-password', '-n', 'goblin',
                          f'--from-file=owner-password={password_file}', '--dry-run=client', '-o', 'json'], capture_output=True, text=True).stdout
            run(['k3s', 'kubectl', 'apply', '--server-side', '--field-manager=goblin-bootstrap', '-f', '-'], input=secret, text=True)
            run(['k3s', 'kubectl', 'delete', 'pod', '-n', 'goblin', '-l', 'app=goblin-auth', '--ignore-not-found=true', '--wait=true'])
            private_write(retained, verifier)
        if config.get('postgres_port'):
            run(['systemctl', 'enable', '--now', DATABASE_SOCKET])
        if state and state['status'] != 'ready':
            run(['systemctl', 'enable', '--now', 'goblin-setup.service'])
            if state['status'] != 'failed' or (OWNER.parent / 'paused').exists():
                run(['systemctl', 'enable', '--now', 'goblin-installer.service'])
        (OWNER.parent / 'paused').unlink(missing_ok=True)
    else:
        print('Starting the setup page and background installation…', flush=True)
        # Pass the rendered bootstrap through stdin, not a file or command-line argument.
        with (STATE / 'bootstrap.log').open('ab') as log:
            environment = dict(os.environ, GOBLIN_PASSWORD_HASH_FILE=str(password_file))
            environment.pop('GOBLIN_LOCAL_PASSWORD', None)
            run(['bash'], input=script, text=True, stdout=log, stderr=subprocess.STDOUT, env=environment)
    # Check through the exact browser port, including the local TCP forwarder.
    for _ in range(30):
        try:
            with urllib.request.urlopen(origin(config) + '/', timeout=2) as response:
                if response.status == 200:
                    break
        except OSError:
            time.sleep(1)
    else:
        raise RuntimeError('The browser port is not ready. Inspect npm run install:local -- logs.')
    print(f'Open in your Windows or Linux browser: {origin(config)}', flush=True)
    print('Installation continues in WSL/Linux after this command exits.')
    print('Progress: npm run install:local -- status')
    print('Sign in with the Goblin password chosen during setup.')
    state = read_status()
    if state and state['status'] == 'failed':
        print('The previous attempt failed. Inspect logs, then run npm run install:local -- retry.')


def status(config):
    state = read_status()
    if not state:
        print('Bootstrap is incomplete. Inspect logs, then run start again.')
        return
    print(f'{origin(config)} — {state["status"]} (attempt {state["attempt"]})')
    print(state['message'])
    for step in state['steps']:
        print(f'  {step["status"]:8} {step["label"]}')
    if not active('goblin-local.socket'):
        print('Local browser access is stopped. Run start to resume.')
    if config.get('postgres_port'):
        print(f'PostgreSQL: localhost:{config["postgres_port"]} ({"listening" if active(DATABASE_SOCKET) else "stopped"})')


def stop():
    # Stop the worker first so its recovery handler cannot restart setup later.
    for unit in ('goblin-installer.service', 'goblin-setup.service', 'goblin-local.socket', DATABASE_SOCKET):
        if (SYSTEMD / unit).exists():
            run(['systemctl', 'disable', '--now', unit], stdout=subprocess.DEVNULL)
    for unit in ('goblin-local.service', DATABASE_SERVICE):
        if (SYSTEMD / unit).exists():
            run(['systemctl', 'stop', unit])
    if (SYSTEMD / 'k3s.service').exists():
        run(['systemctl', 'disable', '--now', 'k3s'])
        # Stopping k3s alone intentionally leaves its containers running.
        run(['/usr/local/bin/k3s-killall.sh'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    (OWNER.parent / 'paused').touch(mode=0o600)
    print('Local Goblin services stopped; installation data retained.')


def reset():
    stop()
    uninstall = Path('/usr/local/bin/k3s-uninstall.sh')
    if uninstall.exists():
        run([uninstall], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        if (SYSTEMD / 'k3s.service').exists() or shutil.which('k3s'):
            raise RuntimeError('K3s is installed but its uninstaller is missing; local data was retained.')
        # A failed download can leave our initial manifest/config without installing K3s.
        for path in ('/etc/rancher/k3s', '/var/lib/rancher/k3s'):
            shutil.rmtree(path, ignore_errors=True)
    # K3s leaves an empty self-bind mount here on WSL even after uninstalling.
    # Keep ownership/state if cleanup is incomplete, so reset can be retried.
    kubelet = Path('/var/lib/kubelet')
    if kubelet.exists():
        if any(kubelet.iterdir()):
            raise RuntimeError('Kubelet data remains after uninstall. Inspect /var/lib/kubelet, then retry reset.')
        if subprocess.run(['mountpoint', '--quiet', str(kubelet)]).returncode == 0:
            run(['umount', '--', kubelet])
        kubelet.rmdir()
    for name in ('goblin-local.socket', 'goblin-local.service', 'goblin-setup.service', 'goblin-installer.service', DATABASE_SOCKET, DATABASE_SERVICE):
        subprocess.run(['systemctl', 'reset-failed', name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        (SYSTEMD / name).unlink(missing_ok=True)
    override = SYSTEMD / 'goblin-installer.service.d'
    (override / 'local-test.conf').unlink(missing_ok=True)
    if override.exists() and not any(override.iterdir()):
        override.rmdir()
    shutil.rmtree(INSTALL)
    shutil.rmtree(SETUP, ignore_errors=True)
    shutil.rmtree(PROXY.parent, ignore_errors=True)
    for name in ('goblin-setup.pyz', 'source.tar.gz', 'bootstrap.sh', 'bootstrap.log', 'login-password'):
        (STATE / name).unlink(missing_ok=True)
    for name in ('goblin-bootstrap.log', 'goblin-installer.log'):
        Path('/var/log', name).unlink(missing_ok=True)
    # A subsequent fresh test must not mistake an empty directory for an existing installation.
    if SETUP.parent.exists() and not any(SETUP.parent.iterdir()):
        SETUP.parent.rmdir()
    run(['systemctl', 'daemon-reload'])
    print('Local Goblin cluster and application data removed. The repository password verifier, Docker and download caches are retained.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    create = commands.add_parser('start', help='Show setup and install directly in WSL/Linux, or resume')
    create.add_argument('--http-port', type=int, choices=range(1024, 65536), metavar='PORT')
    database = commands.add_parser('database', help='Enable persistent local PostgreSQL access after database setup')
    database.add_argument('--port', type=int, choices=range(1024, 65536), metavar='PORT')
    for name in ('status', 'retry', 'stop', 'password'):
        commands.add_parser(name)
    logs = commands.add_parser('logs')
    logs.add_argument('--follow', action='store_true')
    for name in ('reset', 'destroy'):
        destroy = commands.add_parser(name, help='Remove this runner’s local cluster and application data')
        destroy.add_argument('--yes', action='store_true', required=True)
    args = parser.parse_args()
    if os.geteuid() != 0:
        os.execvp('sudo', ['sudo', '--preserve-env=GOBLIN_LOCAL_PASSWORD', sys.executable, str(Path(__file__).resolve()), *sys.argv[1:]])
    os.umask(0o077)
    if args.command == 'password':
        print(f'Local password verifier: {local_password.PASSWORD_FILE}')
        print('The original password is not stored and cannot be displayed.')
        return
    if args.command in ('status', 'logs'):
        config = owned_config()
        if args.command == 'status':
            status(config)
        else:
            paths = [path for path in (Path('/var/log/goblin-bootstrap.log'), Path('/var/log/goblin-installer.log')) if path.exists()]
            if not paths:
                print('No installation log exists yet.')
            else:
                run(['tail', *(['-F'] if args.follow else ['-n', '100']), *paths])
        return
    STATE.mkdir(mode=0o700, exist_ok=True)
    STATE.chmod(0o700)
    # All checkouts share one machine-level lock, including the first ownership check.
    with CONTROL_LOCK.open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        if args.command == 'start':
            start(args)
        else:
            config = owned_config()
            if args.command == 'database':
                configure_database(config, args.port)
            elif args.command == 'retry':
                current = read_status()
                if not current:
                    raise RuntimeError('Bootstrap is incomplete. Run start again.')
                if current['status'] == 'ready':
                    raise RuntimeError('Installation is complete. Use reset --yes, then start for a fresh test.')
                configure_forwarder(config)
                if (SYSTEMD / 'k3s.service').exists():
                    run(['systemctl', 'enable', '--now', 'k3s'])
                run(['systemctl', 'enable', '--now', 'goblin-setup.service', 'goblin-installer.service'])
                print('Installer retry started; retained source and password are reused.')
            elif args.command == 'stop':
                stop()
            elif args.command in ('reset', 'destroy'):
                reset()


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, ValueError, OSError, EOFError, KeyboardInterrupt, subprocess.CalledProcessError) as error:
        print(f'Local installation: {error if not isinstance(error, subprocess.CalledProcessError) else "Command failed; inspect npm run install:local -- logs and .goblin-local/bootstrap.log."}', file=sys.stderr)
        sys.exit(1)
