# Canva replacement — free-tier stack

The whole pipeline avoids Canva and other paid design tools. Everything below is free.

## Image generation

| Use case | Tool | Free tier? |
|---|---|---|
| Square posts 1080×1080 | `ImageSharp` (.NET) | ✅ open-source |
| Portrait 1080×1350 | `ImageSharp` | ✅ |
| Stock photos | `picsum.photos` API | ✅ unlimited |
| Backgrounds / patterns | `ImageSharp` procedural | ✅ |
| Color palette | hardcoded (`iris-default` amber/black) | ✅ |

### ImageSharp palette tokens

```csharp
"iris-default" → bg amber (#F59E0B), fg navy (#0F172A)
"iris-dark"    → bg navy (#0F172A), fg amber (#F59E0B)
"iris-warm"    → bg cream (#FFF7ED), fg rust (#7C2D12)
```

## PDF lead magnets

| Tool | Free tier | License |
|---|---|---|
| **QuestPDF** | Community | Open source (free for revenue under $1M) |

The bible's "Full Protocol Guide PDF" (40 pages) and the "Analytic Doubt Interrupt Sheet" (1 page) are rendered via `RenderPdfAsync(title, sections)`.

## Video (Reels / TikTok / YouTube)

| Tool | Free? |
|---|---|
| **ffmpeg** | ✅ open-source |

Workflow: `ImageSharp.RenderImageAsync` → PNG background → `ffmpeg -loop 1 -i bg.png -vf drawtext=textfile=caption.txt:...` → mp4.

### ffmpeg check

Codespaces `dotnet/runtime` base image includes ffmpeg. To verify:
```bash
ffmpeg -version
```

If missing on your Codespace:
```bash
sudo apt-get update && sudo apt-get install -y ffmpeg fonts-dejavu-core
```

### Free stock video

If you want stock video instead of generated backgrounds:
- `pexels.com` — free, no attribution required
- `pixabay.com` — same
- `mixkit.co` — same

Upload to Skool assets folder, paste URL into `req.MediaUrl`.

## Audio (for somatic body scan etc.)

The bible's "5-Min IRIS Somatic Body Scan" is a free audio file. Host it on:
- **Skool assets** (free for community members)
- **SoundCloud** (free tier, 3 hours upload)
- **Bunny.net stream** (free tier 5GB storage)

Link from Linktree.

## Icon / favicon

Skip — Linktree uses your profile pic.

## Estimated cost

| Need | Cost |
|---|---|
| Image generation | $0 |
| PDF generation | $0 |
| Video generation | $0 |
| Stock assets | $0 |
| Hosting (Codespaces free) | $0 |
| Custom domain (optional) | $0-12/yr |
| **Total** | **$0** |

Canva Pro equivalent: ~$13/mo = $156/yr saved.

