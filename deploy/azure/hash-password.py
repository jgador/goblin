#!/usr/bin/env python3
"""Read a Goblin password from stdin and emit its versioned verifier only."""

import base64
import hashlib
import secrets
import sys


def hash_password(password: str) -> str:
    # Match the portal and .NET string-length limits, including UTF-16 pairs.
    length = len(password.encode("utf-16-le")) // 2
    if (
        length > 128
        or not password.strip()
        or any(ord(character) < 32 or ord(character) == 127 for character in password)
    ):
        raise ValueError(
            "Enter a non-blank Goblin password of up to 128 characters without control characters."
        )
    salt = secrets.token_bytes(16)
    digest = hashlib.pbkdf2_hmac(
        "sha256", password.encode("utf-8"), salt, 600_000, dklen=32
    )
    # Keep this format aligned with OwnerPassword.cs. Never trim the password.
    return (
        "pbkdf2-sha256$600000$"
        + base64.b64encode(salt).decode("ascii")
        + "$"
        + base64.b64encode(digest).decode("ascii")
    )


if __name__ == "__main__":
    try:
        # A 128-character password fits in 512 UTF-8 bytes. Bound stdin too.
        password = sys.stdin.buffer.read(513).decode("utf-8")
        print(hash_password(password))
    except (ValueError, UnicodeError):
        sys.exit(
            "Goblin password is invalid. Enter up to 128 characters, including a non-space character, without tabs or line breaks."
        )
