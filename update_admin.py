"""
Update the Šichtovnica admin username and password.

Usage
-----
Local SQLite only (dev):
    python update_admin.py --username NEW_NAME --password NEW_PASS --db sqlite

Production PostgreSQL only:
    DATABASE_URL=postgres://... python update_admin.py --username NEW_NAME --password NEW_PASS --db postgres

Both at once (default):
    DATABASE_URL=postgres://... python update_admin.py --username NEW_NAME --password NEW_PASS

How it works
------------
Generates a proper ASP.NET Core Identity v3 password hash
(PBKDF2-SHA256, 100 000 iterations) so the API accepts the new
password without any code changes.
"""

import argparse
import base64
import hashlib
import os
import struct
import sys
import uuid


# ─── Password hashing ────────────────────────────────────────────────────────

def hash_password_v3(password: str) -> str:
    """
    Replicates ASP.NET Core Identity v3 PasswordHasher.HashPassword().
    Format: 0x01 | PRF(4B BE) | iterCount(4B BE) | saltLen(4B BE) | salt | subkey
    """
    PRF_HMACSHA256 = 1
    ITER_COUNT     = 100_000
    SALT_SIZE      = 16   # 128 bits
    SUBKEY_SIZE    = 32   # 256 bits

    salt   = os.urandom(SALT_SIZE)
    subkey = hashlib.pbkdf2_hmac('sha256', password.encode('utf-8'), salt, ITER_COUNT, dklen=SUBKEY_SIZE)

    header = struct.pack('>BIII', 0x01, PRF_HMACSHA256, ITER_COUNT, SALT_SIZE)
    return base64.b64encode(header + salt + subkey).decode('utf-8')


# ─── SQLite (local dev) ───────────────────────────────────────────────────────

def update_sqlite(username: str, pw_hash: str) -> None:
    import sqlite3

    db_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'API', 'dochadzkovnik.db')
    if not os.path.exists(db_path):
        print(f'[SQLite] ERROR: DB not found at {db_path}')
        sys.exit(1)

    conn = sqlite3.connect(db_path)
    cur  = conn.cursor()
    cur.execute(
        '''UPDATE "AspNetUsers"
           SET "UserName"           = ?,
               "NormalizedUserName" = ?,
               "PasswordHash"       = ?,
               "SecurityStamp"      = ?''',
        (username, username.upper(), pw_hash, uuid.uuid4().hex.upper())
    )
    print(f'[SQLite] Updated {cur.rowcount} user(s)  →  username: {username}')
    conn.commit()
    conn.close()


# ─── PostgreSQL (production) ──────────────────────────────────────────────────

def update_postgres(username: str, pw_hash: str) -> None:
    try:
        import psycopg2
    except ImportError:
        print('[PostgreSQL] ERROR: psycopg2 not installed.  Run: pip install psycopg2-binary')
        sys.exit(1)

    db_url = os.environ.get('DATABASE_URL')
    if not db_url:
        print('[PostgreSQL] ERROR: DATABASE_URL env var not set.')
        sys.exit(1)

    from urllib.parse import urlparse
    uri  = urlparse(db_url)
    conn = psycopg2.connect(
        host=uri.hostname, port=uri.port,
        database=uri.path.lstrip('/'),
        user=uri.username, password=uri.password,
        sslmode='require'
    )
    cur = conn.cursor()
    cur.execute(
        '''UPDATE "AspNetUsers"
           SET "UserName"           = %s,
               "NormalizedUserName" = %s,
               "PasswordHash"       = %s,
               "SecurityStamp"      = %s''',
        (username, username.upper(), pw_hash, str(uuid.uuid4()).upper())
    )
    print(f'[PostgreSQL] Updated {cur.rowcount} user(s)  →  username: {username}')
    conn.commit()
    cur.close()
    conn.close()


# ─── Entry point ─────────────────────────────────────────────────────────────

def main() -> None:
    parser = argparse.ArgumentParser(description='Update Šichtovnica admin credentials')
    parser.add_argument('--username', required=True, help='New admin username')
    parser.add_argument('--password', required=True, help='New admin password')
    parser.add_argument('--db', choices=['sqlite', 'postgres', 'both'], default='both',
                        help='Which database to update (default: both)')
    args = parser.parse_args()

    print(f'Generating ASP.NET Identity v3 hash for "{args.username}"...')
    pw_hash = hash_password_v3(args.password)
    print(f'Hash: {pw_hash[:24]}...')
    print()

    if args.db in ('sqlite', 'both'):
        update_sqlite(args.username, pw_hash)

    if args.db in ('postgres', 'both'):
        if os.environ.get('DATABASE_URL'):
            update_postgres(args.username, pw_hash)
        elif args.db == 'postgres':
            print('[PostgreSQL] ERROR: DATABASE_URL not set.')
            sys.exit(1)
        else:
            print('[PostgreSQL] Skipped — DATABASE_URL not set. Set it to also update production.')

    print()
    print('Done. Restart the API if it is currently running.')


if __name__ == '__main__':
    main()
