# Where each file goes in your repo

This zip contains 7 files + this README. The folder structure inside
the zip already mirrors where they should land in your repo. So:

    takone-docker-files/
    ├── Dockerfile                          →  repo root
    ├── docker-compose.yml                  →  repo root
    ├── docker-entrypoint.sh                →  repo root
    ├── .dockerignore                       →  repo root
    ├── .env.example                        →  repo root
    ├── DEPLOYMENT.md                       →  repo root
    └── TakOne.WebUI/
        └── appsettings.Production.json     →  inside TakOne.WebUI/ folder

## How to use

1. Unzip this into the root of your TakOne repo on your dev machine.
   The folder structure matches, so files will land in the right places.

2. ALSO edit your existing .gitignore (at the repo root) and add these
   3 lines at the bottom (these are additions to the existing file,
   not a new file — that's why it's not in the zip):

       # --- Docker deployment artifacts ---
       # .env is already ignored above. Exception so the template is committed:
       !.env.example

       # Docker volumes (if anyone runs docker compose up outside a container)
       .docker/

3. Then commit + push:

       git add Dockerfile docker-compose.yml docker-entrypoint.sh \
               .dockerignore .env.example DEPLOYMENT.md \
               TakOne.WebUI/appsettings.Production.json .gitignore
       git commit -m "chore(docker): add Docker deployment files"
       git push

4. On your server: git clone, then `cp .env.example .env` and edit
   the password. Then `docker compose up -d --build`.

   Full instructions in DEPLOYMENT.md (included in this zip).

## Files NOT in this zip (and why)

- `.gitignore` — already exists in your repo. I only added 3 lines.
  See step 2 above.
- `.env` — you create this on the SERVER only, by copying .env.example.
  Never commit it.
