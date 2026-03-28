"""
Run this script from the Dochadzkovnik folder AFTER stopping the API:
    python apply_migration.py

It applies the AddCars + AddCarPhotoUrl migrations directly to the SQLite database.
The PostgreSQL production database on Railway will be updated automatically
when you deploy (dotnet run applies pending migrations at startup).
"""
import sqlite3
import sys
import os

DB_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "API", "dochadzkovnik.db")
MIGRATION_CARS  = "20260328100000_AddCars"
MIGRATION_PHOTO = "20260328120000_AddCarPhotoUrl"

def recorded(cur, mid):
    cur.execute("SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = ?", (mid,))
    return cur.fetchone()[0] > 0

def table_exists(cur, name):
    cur.execute("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?", (name,))
    return cur.fetchone()[0] > 0

def column_exists(cur, table, col):
    cur.execute(f"PRAGMA table_info({table})")
    return any(row[1] == col for row in cur.fetchall())

def index_exists(cur, name):
    cur.execute("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=?", (name,))
    return cur.fetchone()[0] > 0

def main():
    if not os.path.exists(DB_PATH):
        print(f"ERROR: Database not found at {DB_PATH}")
        sys.exit(1)

    conn = sqlite3.connect(DB_PATH)
    conn.execute("PRAGMA journal_mode=WAL")
    cur = conn.cursor()

    try:
        # ── Migration 1: AddCars ─────────────────────────────────────────────
        cars_done = recorded(cur, MIGRATION_CARS)
        cars_exists = table_exists(cur, "Cars")
        car_id_exists = column_exists(cur, "TimeEntries", "CarId")

        print(f"[AddCars] recorded={cars_done}  Cars table={cars_exists}  CarId col={car_id_exists}")

        if not cars_exists:
            print("  Creating Cars table...")
            cur.execute("""
                CREATE TABLE "Cars" (
                    "Id"           INTEGER NOT NULL CONSTRAINT "PK_Cars" PRIMARY KEY AUTOINCREMENT,
                    "Name"         TEXT    NOT NULL,
                    "LicensePlate" TEXT,
                    "IsActive"     INTEGER NOT NULL DEFAULT 1,
                    "CreatedAt"    TEXT    NOT NULL,
                    "UpdatedAt"    TEXT    NOT NULL
                )
            """)

        if not car_id_exists:
            print("  Adding CarId to TimeEntries...")
            cur.execute('ALTER TABLE "TimeEntries" ADD COLUMN "CarId" INTEGER')

        if not index_exists(cur, "IX_TimeEntries_CarId"):
            print("  Creating index IX_TimeEntries_CarId...")
            cur.execute('CREATE INDEX "IX_TimeEntries_CarId" ON "TimeEntries" ("CarId")')

        if not cars_done:
            cur.execute("INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES (?, ?)",
                        (MIGRATION_CARS, "9.0.0"))
            print("  Recorded AddCars migration.")

        # ── Migration 2: AddCarPhotoUrl ──────────────────────────────────────
        photo_done = recorded(cur, MIGRATION_PHOTO)
        photo_exists = column_exists(cur, "Cars", "PhotoUrl")

        print(f"[AddCarPhotoUrl] recorded={photo_done}  PhotoUrl col={photo_exists}")

        if not photo_exists:
            print("  Adding PhotoUrl to Cars...")
            cur.execute('ALTER TABLE "Cars" ADD COLUMN "PhotoUrl" TEXT')

        if not photo_done:
            cur.execute("INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES (?, ?)",
                        (MIGRATION_PHOTO, "9.0.0"))
            print("  Recorded AddCarPhotoUrl migration.")

        conn.commit()
        print("\nAll migrations applied. Start the API with: dotnet run")

    except Exception as e:
        conn.rollback()
        print(f"\nERROR: {e}")
        sys.exit(1)
    finally:
        conn.close()

if __name__ == "__main__":
    main()
