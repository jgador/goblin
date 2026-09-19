#!/usr/bin/env python3
"""Provision the same password verifier as Azure in this checkout."""

import argparse
import base64
import binascii
import getpass
import os
from pathlib import Path
import runpy
import sys
import tempfile

REPO = Path(__file__).resolve().parents[2]
PASSWORD_FILE = REPO / '.goblin-secrets/owner-password'
hash_password = runpy.run_path(str(REPO / 'deploy/azure/hash-password.py'))['hash_password']


def read_verifier(path):
    """Reject a broken saved verifier instead of silently choosing a new password."""
    if path.is_symlink():
        raise RuntimeError('The password verifier must not be a symbolic link.')
    with path.open('rb') as stream:
        value = stream.read(257)
    try:
        fields = value.decode('ascii').strip().split('$')
        if len(value) > 256 or len(fields) != 4 or fields[:2] != ['pbkdf2-sha256', '600000']:
            raise ValueError()
        if len(base64.b64decode(fields[2], validate=True)) != 16 or len(base64.b64decode(fields[3], validate=True)) != 32:
            raise ValueError()
    except (ValueError, UnicodeError, binascii.Error):
        raise RuntimeError('The saved Goblin password verifier is invalid; restore it before starting.') from None
    return value.decode('ascii')


def save_verifier(path, verifier):
    if path.parent.is_symlink() or path.is_symlink():
        raise RuntimeError('Local password storage must not use symbolic links.')
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    path.parent.chmod(0o700)
    # The full installer runs through sudo. Keep the shared local verifier
    # accessible to the developer who also runs npm start without sudo.
    owner = (int(os.environ['SUDO_UID']), int(os.environ['SUDO_GID'])) if (
        os.name != 'nt' and os.geteuid() == 0 and 'SUDO_UID' in os.environ and 'SUDO_GID' in os.environ
    ) else None
    descriptor, temporary = tempfile.mkstemp(prefix='.owner-password-', dir=path.parent)
    try:
        with os.fdopen(descriptor, 'w') as stream:
            stream.write(verifier.rstrip('\n') + '\n')
        if owner:
            os.chown(temporary, *owner)
            os.chown(path.parent, *owner)
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


def ensure_password(path=PASSWORD_FILE, *, replace=False):
    if not replace and (path.exists() or path.is_symlink()):
        read_verifier(path)
        return path
    password = os.environ.get('GOBLIN_LOCAL_PASSWORD')
    if password is None:
        if not sys.stdin.isatty():
            raise RuntimeError('No local password is configured. Run npm run setup:password in a terminal, or supply GOBLIN_LOCAL_PASSWORD for unattended setup.')
        password = getpass.getpass('Goblin password: ')
        confirmation = getpass.getpass('Confirm Goblin password: ')
        if password != confirmation:
            raise RuntimeError('The Goblin passwords do not match. No password was saved.')
    try:
        verifier = hash_password(password)
    except (ValueError, UnicodeError):
        raise RuntimeError('Enter a non-blank Goblin password of up to 128 characters without control characters.') from None
    save_verifier(path, verifier)
    return path


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--replace', action='store_true', help='Choose a new password, replacing the saved verifier after confirmation')
    args = parser.parse_args()
    try:
        path = ensure_password(replace=args.replace)
        print(f'Goblin password verifier: {path}')
        print('Use the password chosen during setup to open Goblin.')
    except (RuntimeError, OSError, EOFError, KeyboardInterrupt) as error:
        print(f'Local password setup: {error}', file=sys.stderr)
        sys.exit(1)
