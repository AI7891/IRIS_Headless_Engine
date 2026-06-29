# Phone-only deployment — Android + Codespaces

The full pipeline runs on GitHub Codespaces, controlled from an Android phone. **No laptop, no PC required.**

## Hardware: what you need

| Item | Why |
|---|---|
| Android phone (any modern, Android 8+) | Control surface |
| Termux (from F-Droid, not Play Store) | SSH client + heartbeat |
| Tasker + Termux:API | Heartbeat automation |
| GitHub account | Free Codespaces tier (60 hr/month) |
| Cellular or WiFi | Connectivity |

## Software: install once

### Step 1: Termux
1. Open F-Droid (`f-droid.org`) in your phone browser
2. Search "Termux" → install
3. Open Termux, run:
   ```bash
   pkg update && pkg upgrade
   pkg install openssh termux-api
   ```

### Step 2: Tasker
1. Install **Tasker** (~$3) from Play Store
2. Install **Termux:API** from F-Droid
3. In Android **Settings → Apps → Termux → Battery → Unrestricted**

### Step 3: GitHub Codespaces
1. Open `github.com/codespaces` in phone browser
2. Tap **New codespace** → pick your repo
3. Wait for VS Code Web to load (1-2 min)

## The 30-min idle problem (and fix)

**GitHub Codespaces free tier kills inactive sessions after 30 minutes.**

The pipeline has 4 background jobs (heartbeat, daily-post, token-refresh, webhook-sweep) that need to run 24/7. Without a heartbeat, the scheduler dies after 30 min.

**The fix**: Tasker pings `GET /healthz` every 10 minutes from your phone. Codespaces sees the request, considers the session active, never idles out.

### Tasker profile setup
1. Open Tasker
2. **Profiles** tab → tap **+**
3. **Time** → set to repeat every 10 min (or "every 30 min" if battery is a concern)
4. **New Task** → tap **+**
5. Add action: **Net → HTTP Request**
   - Method: `GET`
   - URL: `https://YOUR-CODESPACE-NAME-5000.app.github.dev/healthz`
   - Timeout: 10
   - Trust Any Certificate: ON
6. Save, back out

That's it. The heartbeat is now active. As long as your phone has internet and Tasker isn't battery-restricted, Codespaces stays alive.

### Battery-saving alternative: SSH heartbeat
If Tasker is too battery-heavy:
```bash
# In Termux, run this loop:
while true; do
  curl -sS https://YOUR-CODESPACE.app.github.dev/healthz > /dev/null
  sleep 600
done
```
This uses less battery than Tasker but requires Termux to be foregrounded once.

## SSH into Codespaces (for advanced ops)

GitHub provides SSH for each codespace:
```bash
# In Termux:
ssh codespace@YOUR-CODESPACE-NAME.ssh.github.com
```

Once connected, you can:
- Edit `appsettings.json` with `vim` or `nano`
- Tail logs: `tail -f data/iris.log`
- Restart the app: `pkill dotnet && cd src && dotnet run &`
- Query the API: `curl localhost:5000/api/iris/queue`

## Phone home-screen shortcuts

Add these as Android home screen shortcuts (use **HTTP Shortcuts** app or Chrome "Add to Home Screen"):

1. **Healthcheck** → `GET /healthz`
2. **Queue** → `GET /api/iris/queue`
3. **Status** → `GET /api/providers/status`
4. **Conversions** → `GET /api/monetization/conversions?limit=10`
5. **Re-auth Meta** → `GET /auth/meta/login`
6. **Re-auth TikTok** → `GET /auth/tiktok/login`
7. **Re-auth YouTube** → `GET /auth/youtube/login`

## Network considerations

| Network | Works? |
|---|---|
| Home WiFi | ✅ |
| 4G/5G cellular | ✅ (Tasker works on cellular) |
| Public WiFi (hotel/cafe) | ✅ |
| Behind VPN | ✅ but VPN must be on the phone, not split-tunnel |
| Captive portal WiFi (airports) | ⚠️ may break heartbeat; use cellular |

## First-time setup checklist (1 hour)

- [ ] Termux installed + SSH key generated (`ssh-keygen`)
- [ ] GitHub SSH key added (`Settings → SSH and GPG keys`)
- [ ] Codespace created, port 5000 forwarded
- [ ] `dotnet --version` returns 8.x in Codespaces terminal
- [ ] `appsettings.json` filled with App ID/Secrets
- [ ] `dotnet run` in `src/` works
- [ ] All 3 OAuth flows completed (Meta, TikTok, YouTube)
- [ ] `GET /api/providers/status` shows all 3 as `"valid"`
- [ ] Tasker profile created and tested
- [ ] First 3 auto-posts visible in `GET /api/iris/queue`
- [ ] Phone shortcuts added to home screen

## Time investment

| Task | Time |
|---|---|
| Termux + Tasker install | 10 min |
| Codespace creation | 5 min |
| .NET install + restore | 10 min |
| App credentials + auth flows | 20 min |
| Tasker heartbeat setup | 5 min |
| First test posts | 10 min |
| **Total** | **~1 hour** |

After that, the pipeline runs itself. Operator only needs to:
- Check `/api/monetization/summary` daily (5 sec)
- Re-auth any platform whose token expires (2 min, every 60 days for Meta)
- Adjust hooks/pillars based on performance (15 min weekly)

