# How to deploy TakOne to your Linux server — for absolute beginners

This guide assumes you know **nothing** about Docker, Linux servers, or
deployment. We'll go one step at a time, in plain English.

By the end, you'll have your app running at `http://YOUR-SERVER-IP:8080`.

---

## What the hell is Docker, anyway?

Think of Docker like a shipping container for software.

Normally, to run an app on a server, you'd need to install .NET, SQL
Server, configure a bunch of stuff, hope the versions match what the
developer used, and pray it works. It's a nightmare.

Docker fixes this. A Docker **image** is a snapshot of everything the
app needs to run — the code, the runtime, the libraries, all of it —
packed into one file. A Docker **container** is that image running
live, like a tiny self-contained computer inside your server.

You build the image once, then run it anywhere Docker is installed.
No "but it works on my machine" — if it works in the image, it works
on the server.

For your project, we'll run **two containers**:
1. **The app** (your .NET code, in one container)
2. **SQL Server** (the database, in another container)

They talk to each other over Docker's internal network. You don't
need to expose SQL Server to the internet (in fact, you shouldn't).

---

## Files I already made for you (just so you know what they are)

I added these to your repo:

| File | What it does |
|------|--------------|
| `Dockerfile` | The recipe that builds a Docker image of your app |
| `docker-compose.yml` | Says "run these 2 containers together, hook them up, persist data" |
| `docker-entrypoint.sh` | Runs inside the app container at startup. Waits for SQL Server, applies DB migrations, then starts the app |
| `.dockerignore` | Tells Docker "ignore bin/obj/.git" so builds are fast |
| `.env.example` | A template for passwords. You copy it to `.env` and edit |
| `TakOne.WebUI/appsettings.Production.json` | Production config — overrides the dev connection string |

You don't need to touch any of these unless something breaks. Just
commit them to git so they're on your server when you clone the repo.

---

## Step 0: Commit the new files to git and push

On your dev machine (where your repo lives now):

```bash
cd /path/to/your/TakOne-repo
git add Dockerfile docker-compose.yml docker-entrypoint.sh \
        .dockerignore .env.example \
        TakOne.WebUI/appsettings.Production.json \
        .gitignore
git commit -m "chore(docker): add Dockerfile + compose for Linux deployment"
git push
```

Why? Because on the server, you'll `git clone` the repo, and you
need these files to be in the repo.

---

## Step 1: SSH into your server

Open a terminal on your laptop and SSH in:

```bash
ssh your-username@your-server-ip
```

(If you don't know how to SSH, ask whoever set up your server. You
need the IP address, username, and password or SSH key.)

Once you're in, you should see a prompt like `user@server:~$`. That
means you're on the server.

---

## Step 2: Install Docker

Run these commands **one at a time**. Wait for each to finish before
running the next.

### 2a. Update the package list

```bash
sudo apt update
```

(If your server isn't Ubuntu/Debian — like if it's CentOS, Rocky,
AlmaLinux — the package manager is `dnf` or `yum` instead of `apt`.
Tell me what distro you're on if `apt` doesn't work and I'll give
you the right commands.)

### 2b. Install Docker + Docker Compose

```bash
sudo apt install -y docker.io docker-compose-v2
```

This installs:
- `docker.io` — the Docker engine (runs containers)
- `docker-compose-v2` — the `docker compose` command (orchestrates
  multi-container setups)

### 2c. Add your user to the `docker` group

By default, you need `sudo` to run Docker. Annoying. This command
lets you run Docker without `sudo`:

```bash
sudo usermod -aG docker $USER
```

**IMPORTANT**: This doesn't take effect immediately. You need to log
out and log back in:

```bash
exit
```

Then SSH back in.

### 2d. Verify Docker works

```bash
docker --version
docker compose version
```

You should see version numbers. If you see "command not found",
something went wrong — tell me the error.

---

## Step 3: Clone your repo on the server

Decide where you want the code to live. `~/apps/TakOne` is a fine
spot. Run:

```bash
mkdir -p ~/apps
cd ~/apps
git clone https://github.com/mortmccain/TakOne.git
cd TakOne
```

If your repo is private, git will ask for your GitHub username and
a Personal Access Token (NOT your password — GitHub killed password
auth a while back). To get a token:
1. Go to https://github.com/settings/tokens
2. Click "Generate new token (classic)"
3. Check the `repo` scope
4. Generate, copy the token, paste it when git asks for your password

---

## Step 4: Set your SQL Server password

```bash
cp .env.example .env
nano .env
```

(`nano` is a simple text editor. If it's not installed, run
`sudo apt install -y nano` first.)

You'll see this:

```
SQL_SA_PASSWORD=CHANGE_ME_TO_A_STRONG_PASSWORD
```

Change `CHANGE_ME_TO_A_STRONG_PASSWORD` to a real password. Rules:
- At least 8 characters
- Must contain 3 of these 4: uppercase letters, lowercase letters,
  digits, symbols
- Don't use a password you use anywhere else

Example: `Kj7$mP9!nQ4vLw2x`

(Don't use that exact one — it's in a public file. Pick your own.)

Save and exit nano:
- Press `Ctrl+O` then `Enter` (save)
- Press `Ctrl+X` (exit)

**Don't lose this password.** Write it down somewhere safe (a password
manager). You'll need it later to connect to SQL Server from your
laptop if you ever want to look at the DB directly.

---

## Step 5: Build and start everything

Make sure you're in the repo folder (where `docker-compose.yml` lives):

```bash
cd ~/apps/TakOne
```

Run:

```bash
docker compose up -d --build
```

What this does:
- `--build` — builds the Docker image from your `Dockerfile` (first
  time takes 3-5 minutes — it's downloading .NET, restoring NuGet
  packages, compiling your code)
- `up` — starts the containers
- `-d` — "detached" — runs them in the background so you get your
  terminal back

You'll see a lot of output. Don't panic. As long as it doesn't end
with "ERROR", you're fine.

The first build takes a few minutes. Subsequent builds are much
faster (Docker caches layers).

---

## Step 6: Wait for it to boot, then check it's running

SQL Server takes 20-30 seconds to start. The app waits for it
automatically (that's what `docker-entrypoint.sh` does). So just
wait ~60 seconds, then check the logs:

```bash
docker compose logs web
```

You're looking for a line like:
```
Now listening on: http://[::]:8080
```

That means the app is up.

If you see errors instead, scroll up and read them. Common ones:
- "SQL Server did not become ready" → SQL Server container is
  failing to start. Run `docker compose logs sql` to see why.
  Most common cause: weak password (SQL Server refuses to boot if
  the SA password doesn't meet complexity rules).
- "Migrations applied successfully" then crashes → some C# error.
  Copy the stack trace and send it to me.

To leave the logs view, press `Ctrl+C`.

---

## Step 7: Open it in your browser

On your laptop, open a browser and go to:

```
http://YOUR-SERVER-IP:8080
```

(Replace `YOUR-SERVER-IP` with the actual IP, like `http://192.168.1.50:8080`)

You should see the login page. 🎉

**Can't reach it?** Check your server's firewall:

```bash
sudo ufw status
```

If it says "active" and port 8080 isn't listed, open it:

```bash
sudo ufw allow 8080/tcp
```

Also check if your VM host (the partitioning layer above your VM)
has a firewall. Sometimes the hypervisor blocks ports too.

---

## Step 8: Log in

The `appsettings.Production.json` I created enables the default
admin seeder. So your first login is:

- **Username**: `ADMIN-0001`
- **Password**: check the `DefaultAdminSeeder.cs` file in
  `TakOne.Infrastructure/Identity/` — that's where the default
  password is set. (You didn't tell me what it is, but you wrote
  that code, so you know.)

It will probably force you to change the password on first login.

---

## Step 9: You're done. Here's how to manage it going forward.

### See what's running

```bash
docker compose ps
```

Shows both containers (`sql` + `web`) and their status. Both should
say "Up".

### View live logs

```bash
docker compose logs -f web     # just the app
docker compose logs -f sql      # just SQL Server
docker compose logs -f          # everything
```

Press `Ctrl+C` to exit.

### Stop everything

```bash
docker compose stop
```

Containers are paused but not deleted. Start them again with
`docker compose start`.

### Stop AND delete containers (data is preserved)

```bash
docker compose down
```

Containers are deleted, but the **volumes** (SQL Server data +
uploaded files) are preserved. Start again with `docker compose up -d`.

### Delete everything including data ⚠️

```bash
docker compose down -v
```

The `-v` flag deletes the volumes too. **This wipes your database
and uploaded files.** Only do this if you want a fresh start.

### Update to a new version of your code

When you push new commits to GitHub and want to deploy them:

```bash
cd ~/apps/TakOne
git pull
docker compose up -d --build
```

The `--build` rebuilds the image with your new code. The `up -d`
swaps the running container to the new image. Data in volumes is
preserved.

### Restart just the app (not SQL Server)

```bash
docker compose restart web
```

Useful if you want to force a clean restart without touching the DB.

---

## Common questions

### "Where is my data actually stored?"

In Docker **named volumes**. Run:

```bash
docker volume ls
```

You'll see `takone_sql_data`, `takone_uploads`, and `takone_backups`
(the scheduled database backups — see the next question). These live at
`/var/lib/docker/volumes/` on the server's filesystem. You don't
need to touch them directly — Docker manages them.

### "How do I back up the database?"

**You don't have to — it backs itself up now.** The app runs a scheduled
backup service that takes a full `BACKUP DATABASE` **twice a day, at
03:30 UTC and 14:30 UTC**, and automatically deletes backup files older
than **7 days** (so there are at most 14 backup files at any time, and
the disk usage is bounded).

The backups live in the `takone_backups` Docker volume. To copy one out
of the server:

```bash
# List the backups (same files, two mount points — pick either)
docker compose exec takone-web ls -lh /var/lib/takone/backups
docker compose exec takone-db  ls -lh /var/opt/mssql/backup

# Copy the newest one out to your home folder
docker compose cp takone-web:/var/lib/takone/backups/$( \
  docker compose exec takone-web sh -c \
  'ls /var/lib/takone/backups | tail -1' | tr -d '\r' \
) ~/TakOne-$(date +%F-%H%M).bak
```

Then download it to your laptop with `scp` for safekeeping.

**To restore a backup** (e.g. onto a fresh install), stop the app, put
the `.bak` into the backup volume, and restore it over the database:

```bash
docker compose down
docker run --rm -v takone_takone_backups:/backup -v "$PWD":/src alpine \
  cp /src/TakOne-RESTORE-ME.bak /backup/
docker compose up -d db
sleep 30   # wait for SQL Server to be healthy
docker compose exec takone-db /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "YOUR_SA_PASSWORD" -C \
  -Q "ALTER DATABASE TakOne SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE TakOne FROM DISK='/var/opt/mssql/backup/TakOne-RESTORE-ME.bak' WITH REPLACE; ALTER DATABASE TakOne SET MULTI_USER"
docker compose up -d
```

**How the automatic backups work (and why the Docker setup looks a bit
odd):** SQL Server's `BACKUP DATABASE ... TO DISK` writes the file on the
**database server's own filesystem** — inside the `db` container, not
the app container. That's why `docker-compose.yml` mounts ONE shared
volume (`takone_backups`) into **both** containers:

| Container | Mount point | Used for |
|---|---|---|
| `takone-db` | `/var/opt/mssql/backup` | where `BACKUP DATABASE` writes (the `DatabaseBackups__ServerPath` env var) |
| `takone-web` | `/var/lib/takone/backups` | where the app verifies the file + deletes expired backups (the `DatabaseBackups__Directory` env var) |

Two mount points, one physical volume — the app sees the exact files the
database wrote, and the retention sweep (7 days) runs from the app side.

**Backup volume permissions — the `takone-backup-init` service.** A fresh
Docker *named volume* inherits the ownership of the image directory it is
mounted over **only when that path exists in the image**. `/var/opt/mssql`
exists in the mssql image (that's why the data volume just works), but
`/var/opt/mssql/backup` does **not** — and `/var/lib/takone/backups` does
not exist in the web image either. So the `takone_backups` volume is
created `root:root` with mode `0755`, and *neither* process can use it:

* SQL Server runs as uid **10001** (`mssql`) → cannot write `.bak` files →
  every `BACKUP ... TO DISK` fails with *Cannot open backup device*
  (Operating system error 5).
* The web app runs as uid **1654** (`app`) → cannot write the run-history
  ledger and cannot delete expired backups (both failures are silent by
  design — a run's outcome must never fail because its history could not
  be saved).

The `takone-backup-init` one-shot service in `docker-compose.yml` fixes
this on every `compose up`: it runs `chown -R 10001:0` + `chmod 0777` on
the volume root **before** the db and web containers start, and drops a
`.takone-volume-initialized` sentinel so no image-seeding ever re-owns
the volume afterwards. It is idempotent and also **repairs volumes
created before the service existed**.

**"Backups say SUCCESS but 0 files / 0 bytes — what happened?"** This is
the signature of the permission problem above, on a deployment that
predates the init service. Because SQL Server could not write the shared
volume, the app's fallback wrote every backup into SQL Server's **own
default backup folder inside the db container** (`/var/opt/mssql/data/`)
and reported success with an explanatory note — a note you never saw,
because the app also couldn't write the ledger it would have been saved
in. The backups are **not lost**. After updating and running
`docker compose up -d --build` (which fixes the volume), move them onto
the shared volume:

```bash
# 1. See the stranded backups (there is roughly one per scheduled slot
#    + every manual "Back up now" since the first deploy):
docker compose exec takone-db ls -lh /var/opt/mssql/data/TakOne-backup-*.bak

# 2. Move them onto the shared backup volume (the Settings page will
#    list them immediately — hit Refresh; the 7-day retention sweep
#    then prunes the old ones on the next run):
docker compose exec takone-db sh -c \
  'mv /var/opt/mssql/data/TakOne-backup-*.bak /var/opt/mssql/backup/'
```

If step 1 prints `ls: cannot access ... No such file or directory`, there
are no stranded files (e.g. the fallback never triggered on your box) —
nothing to rescue.

The Settings → backup card now also **tells you** instead of staying
silent: a completed run whose file landed elsewhere shows a yellow
"completed with a notice" alert with the exact server-side path, a backup
folder the app cannot write to shows a read-only warning, and a missing
folder shows its own error — so "0 files" is never ambiguous again.

**Differences vs a non-Docker (dev) deployment**, for the record:

1. **Paths.** In dev (Kestrel + local SQL Server on one machine) the
   backup goes to `backups/` under the app's working directory — the
   app and SQL Server share a filesystem, so `DatabaseBackups:Directory`
   and `DatabaseBackups:ServerPath` are the same value (the default:
   an unset `ServerPath` follows `Directory`). Relative values are
   resolved to **absolute** before the `BACKUP` command runs, because
   SQL Server interprets a relative `TO DISK` path against **its own
   instance backup folder** (`C:\Program Files\Microsoft SQL
   Server\MSSQL17.<INSTANCE>\MSSQL\Backup\`) — never the app's
   working directory — so a raw relative path fails with `Operating
   system error 3 (The system cannot find the path specified)`. In
   Docker the two values MUST be the volume mount points above.
2. **Permissions.** In dev, your Windows/SQL login needs
   `db_backupoperator` on the database (a sysadmin login has it
   implicitly). In Docker the app connects as `sa`, which has it
   implicitly. On a single-machine **Windows** install the SQL Server
   *service account* also needs write access to the backup folder — it
   performs the file write, not the app. The smoothest setup is a
   dedicated folder outside the app tree (set it via
   `DatabaseBackups:Directory`) granted once:

   ```bat
   icacls "C:\TakOneBackups" /grant "NT SERVICE\MSSQL$HANZ:(OI)(CI)M"
   ```

   (substitute your instance name; for the default instance use
   `NT SERVICE\MSSQLSERVER`)

   **If you skip that grant**, backups still work: when SQL Server
   rejects the configured folder with *Access is denied* (`Operating
   system error 5`), the app automatically retries against the SQL
   Server instance's **own default backup directory** (read from the
   instance registry — the one folder the service account can always
   write) and then moves/copies the `.bak` into your backups folder so
   the retention sweep and the admin files list keep working. Such a
   run is marked SUCCESS with a note in the run-history ledger saying
   exactly what happened (and, if even the copy was impossible, where
   the file remains on the SQL Server — that copy is not covered by the
   7-day sweep and must be removed manually). Caveats: the fallback
   needs a login that may read the instance registry
   (`xp_instance_regread` — `sa`/sysadmin has it), and when the app
   runs non-elevated it can usually copy but not delete from the
   instance folder, so a duplicate `.bak` may accumulate there — the
   `icacls` grant above remains the clean setup (the file then lands
   directly in the configured folder, no detour, no duplicates).
3. **Timezone.** The schedule is pinned to **UTC** (03:30 and 14:30)
   regardless of the container's `TZ` (the web container runs
   `Asia/Tehran` for the UI greeting). 03:30 UTC = 07:00 and 14:30 UTC =
   18:00 Tehran time.
4. **Missed slots are skipped, not replayed.** If the server was down at
   03:30, that backup simply doesn't happen — the next one is at most 11
   hours away.
5. **Remote SQL Server.** When the app and SQL Server run on
   **different machines**, `DatabaseBackups:ServerPath` must be set
   explicitly to a path that exists on the *database server's*
   filesystem — a Windows drive-letter path (`C:\…`) passes through
   verbatim even when the app itself runs on Linux. The app-side
   `Directory` must then also see those same files (e.g. an SMB share
   mounted at both ends) or the post-backup verification + the 7-day
   retention sweep cannot find them.

You can turn the whole thing off with `DatabaseBackups__Enabled=false`
in the environment, or take a manual backup any time with the `sqlcmd`
one-liner from the start of this section (just write it into
`/var/opt/mssql/backup/` so the retention sweep manages it too).

**Admin UI (Settings → Admin tools → "Database backups"):** the backup
subsystem also has a first-class admin surface — sign in as an Admin and
open `/Settings`:

- **Status card** — last run (time, duration, succeeded/failed badge,
  and the failure message when it failed), the next scheduled slot, the
  retention window, and the full newest-first list of `*.bak` files with
  sizes and Tehran-local timestamps.
- **Run history** — a newest-first ledger of recently COMPLETED runs
  (scheduled AND manual), each with its Tehran start time, duration,
  trigger (clock icon = scheduled slot, hand icon = manual "Back up
  now"), outcome badge, and — for failed runs — the failure message in
  the row's hover tooltip. Runs that succeeded via the
  default-backup-directory fallback (see Permissions above) show a
  secondary note line under the row explaining where the file came
  from / landed. This is persisted across restarts in
  `backup-history.jsonl` (one JSON line per completed run, written next
  to the `*.bak` files on the shared volume) and pruned by the same
  7-day retention sweep, so "did the 03:30 slot actually run last week?"
  is answerable without grepping container logs. The ledger is
  best-effort: a failed history write is logged as a warning and never
  fails the backup itself.
- **"Back up now"** — triggers an extra backup immediately (admin-only,
  gated server-side by `[RequireRoles(Admin)]` via
  `TriggerBackupNowCommand`). The run executes on the backup service's
  own background loop — the card polls the status every 5 s until the
  run finishes. A manual run NEVER shifts the fixed 03:30/14:30 UTC
  schedule, and a second click while one is running is rejected with a
  per-reason message (in flight vs. already pending vs. schedule
  disabled — the coordinator reports the concrete cause).
- **"Download run report (CSV)"** — exports the run-history ledger as a
  portable CSV audit artifact (`takone-backup-runs-<timestamp>.csv`):
  one chronological row per completed run with UTC ISO-8601 start/finish
  timestamps, raw duration seconds, outcome, trigger, file name, size in
  bytes, the failure detail, and the run's note (the fallback
  annotation; empty on normal runs). Handy for ops handovers/ticketing
  where pasting the live card isn't enough. Disabled until the first run
  completes.

The status reads the same singleton the schedule loop mutates (the
hosted service is dual-registered as the `IBackupCoordinator` port), so
what the card shows is always the loop's actual state.

### "Can I use a real domain name instead of the IP?"

Yes, but that's step 2. For now, get it working on the IP. Once you
have a domain pointing at your server, we'll add **Caddy** as a
reverse proxy in front of the app — it handles HTTPS automatically
(free certificates from Let's Encrypt).

### "The build is failing. What do I do?"

Copy the last 30 lines of the build output and paste it to me.
Don't paste all 500 lines — just the end where the error is.

### "It built but the app crashes on startup."

```bash
docker compose logs web
```

Copy the last 50 lines and send them to me.

---

## Quick reference — the 5 commands you'll use 99% of the time

| Command | When to use it |
|---------|----------------|
| `docker compose up -d --build` | After `git pull` to deploy new code |
| `docker compose logs -f web` | To see what the app is doing |
| `docker compose ps` | To check if containers are running |
| `docker compose restart web` | To restart just the app |
| `docker compose down` | To stop everything (data preserved) |

---

## You got this.

If something breaks, don't panic. Copy the error message and tell
me what command you ran. We'll figure it out.
