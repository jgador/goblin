#!/usr/bin/env python3
"""Provision local credentials, then launch the unchanged Goblin application."""

import os
from pathlib import Path
import sys

from password import REPO, ensure_password, read_verifier


def main():
    configured = os.environ.get('GOBLIN_PASSWORD_HASH_FILE')
    if configured is not None:
        path = Path(configured).resolve()
        read_verifier(path)
    else:
        path = ensure_password()
    environment = dict(os.environ, GOBLIN_PASSWORD_HASH_FILE=str(path))
    environment.pop('GOBLIN_LOCAL_PASSWORD', None)
    executable = REPO / 'backend/src/Goblin.Web/bin/Debug/net10.0/Goblin.Web.dll'
    os.execvpe('dotnet', ['dotnet', str(executable)], environment)


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, OSError, EOFError, KeyboardInterrupt) as error:
        print(f'Local startup: {error}', file=sys.stderr)
        sys.exit(1)
