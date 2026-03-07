# Running the app on localhost

Use this guide to run the CoDIRA / ER Simulator app **on your machine only** (localhost). Nothing is exposed to the network.

**Important:** Do not commit `appsettings.json` or `appsettings.Development.json` to GitHub—they can contain API keys. They are listed in `.gitignore`. Use the `*Example.json` files as templates.

**You won’t have access to API keys when you clone the repo**—those files are not in the repository. Once you’ve got the repo on your local machine and are ready to configure it, ping me and I’ll get you the keys you need.

---

## Prerequisites

Install these once on your machine:

1. **.NET 8 SDK**  
   - [Download](https://dotnet.microsoft.com/download/dotnet/8.0) and install.  
   - Check: `dotnet --version` (should be 8.x).

2. **Python 3.10+**  
   - [python.org](https://www.python.org/downloads/) or your system package manager.  
   - Check: `python3 --version`.

3. **Git**  
   - So you can clone the repo (if you haven’t already).

---

## Step 1: Clone or open the repo

```bash
cd /path/where/you/keep/projects
git clone <your-repo-url> Emergency-Room-Simulator-Repository
cd Emergency-Room-Simulator-Repository
```

(If you already have the repo, just `cd` into it.)

---

## Step 2: One-time setup

### 2a. RAG backend (Python)

**You don’t need to create the venv yourself.** The first time you run `./start-app.sh`, it will create `rag_backend/.venv` and install dependencies automatically. This will take a long time, just be patient. Just ensure Python 3.10+ is installed (see Prerequisites).

### 2b. Local app settings (so the app talks to local RAG)

The app must point to the RAG backend on **localhost**. In Development it uses `appsettings.Development.json`.

1. Copy the example file:

   ```bash
   cp ERSimulatorApp/appsettings.Development.Example.json ERSimulatorApp/appsettings.Development.json
   ```

2. Edit **ERSimulatorApp/appsettings.Development.json** and set the RAG base URL to localhost:

   - Find the `"RAG"` section.
   - Set **`"BaseUrl"`** to:  
     `"http://127.0.0.1:8010/v1/chat/completions"`

   Example:

   ```json
   "RAG": {
     "BaseUrl": "http://127.0.0.1:8010/v1/chat/completions",
     "ApiKey": "YOUR_RAG_API_KEY_IF_NEEDED",
     ...
   }
   ```

   **HeyGen (avatar) is optional.** If you omit HeyGen keys, the app still starts; clicking **Start Session** will show a message that HeyGen is not configured. To use the full avatar locally, add your HeyGen API key and Avatar ID (ping me for keys). Other keys (e.g. ElevenLabs for voice input) only if you need them.

**Do not commit** `appsettings.Development.json`; it is in `.gitignore`.

### 2c. (Optional) PDF data for RAG

**PDF ingestion is handled by `./start-app.sh`.** Each time you run the script, it runs ingest on `rag_backend/data/trauma_pdfs` automatically (after RAG is up). If you don’t have data yet, ingest may log a warning; you can add PDFs later and start the script again to re-run ingest.

---

## Step 3: Start the app on localhost

From the **repository root** (where `start-app.sh` lives):

```bash
chmod +x start-app.sh
./start-app.sh
```

What this does:

1. **First run only:** If there’s no `rag_backend/.venv` yet, creates it and runs `pip install -r rag_backend/requirements.txt`.
2. Starts the RAG backend at **http://127.0.0.1:8010** (localhost only).
3. Runs ingest (if data is present).
4. Starts the .NET app at **http://localhost:5121**.

To stop everything, press **Ctrl+C** in the same terminal.

---

## Step 4: Open the app in your browser

Open:

**http://localhost:5121**

You should see the CoDIRA app. Use **Start Session** on the Avatar page to begin. The app talks only to services on your machine.

---

## Quick reference

| What              | URL / command                         |
|-------------------|----------------------------------------|
| App (browser)     | http://localhost:5121                 |
| RAG backend       | http://127.0.0.1:8010 (used by app)   |
| Start everything  | `./start-app.sh` (from repo root)   |
| Stop              | Ctrl+C in the terminal                |
| Local config      | `ERSimulatorApp/appsettings.Development.json` (do not commit) |

---

## Troubleshooting

- **“Connection refused” or RAG errors**  
  Make sure `appsettings.Development.json` has  
  `"BaseUrl": "http://127.0.0.1:8010/v1/chat/completions"`  
  and that you ran `./start-app.sh` from the repo root (so RAG starts first).

- **Port already in use**  
  If 5121 or 8010 is in use, stop the other process or change the port in `ERSimulatorApp/Properties/launchSettings.json` (profile `http`) and in the RAG command in `start-app.sh`.

- **Voice / microphone**  
  On plain HTTP localhost, some browsers may restrict microphone access. If you need voice for the demo, consider using the server (HTTPS) flow described in the main setup/demo docs instead.

- **Windows**  
  Run the script in Git Bash or WSL, or run the same steps by hand:  
  1) `cd rag_backend`, activate venv, then `python -m uvicorn app.main:app --host 127.0.0.1 --port 8010`;  
  2) in another terminal, run ingest (e.g. `curl -X POST http://127.0.0.1:8010/ingest -H "Content-Type: application/json" -d "{\"folder\": \"data/trauma_pdfs\"}"`);  
  3) `cd ERSimulatorApp` and `dotnet run --launch-profile http`.

---

## Not committing secrets

These are ignored by Git (see `.gitignore`); **do not add them to the repo**:

- `appsettings.json`
- `appsettings.Development.json`
- `.env`, `.env.local`, `.env.secrets`, and similar
- `ERSimulatorApp/certs/`
- Any file with API keys or passwords

Use the `*Example.json` files (in `ERSimulatorApp/`) and `rag_backend/.env.example` as templates and keep your real config only on your machine.
