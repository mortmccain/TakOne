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

You'll see `takone_sql_data` and `takone_uploads`. These live at
`/var/lib/docker/volumes/` on the server's filesystem. You don't
need to touch them directly — Docker manages them.

### "How do I back up the database?"

```bash
docker compose exec sql /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "YOUR_SA_PASSWORD" \
  -Q "BACKUP DATABASE TakOne TO DISK='/var/opt/mssql/data/TakOne.bak'"
```

Then copy the backup file out of the container:

```bash
docker compose cp sql:/var/opt/mssql/data/TakOne.bak ~/TakOne-$(date +%F).bak
```

Now you have a `.bak` file in your home folder. Download it to your
laptop with `scp` for safekeeping.

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
