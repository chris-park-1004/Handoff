<div align="center">

<img src="app/Handoff.WinUI/Assets/Logo.png" alt="Handoff" width="120" />

# Handoff

**Async pair programming for AI sessions.**
Your team's commits become your AI's memory.

</div>

---

## The problem

When your teammate finishes a Claude Code or Codex session, the context they built up dies with the session. The next AI session — yours, theirs, anyone's — starts from zero. You re-explain. You re-discover. Knowledge gets siloed inside ephemeral conversations.

## The fix

Handoff sits inside your AI CLI's hook chain. After every commit, it asks you to summarize what just happened — or auto-generates one using the same CLI you're already paying for. The summary lands in a shared store. When your teammate's next session starts, **their** AI sees it — only after they explicitly Allow each item.

No re-explaining. No copy-paste. No new API key. Opt-in per teammate, gated per item.

---

## Demo

<div align="center">

[![Handoff demo](https://img.youtube.com/vi/EijtdgiKSzQ/maxresdefault.jpg)](https://youtu.be/EijtdgiKSzQ)

*Recorded across two machines — Claude Code on one, Codex CLI on the other.*

</div>

---

## How it works

<img width="6688" height="1923" alt="4(4)" src="https://github.com/user-attachments/assets/fabd389c-5d9b-4802-aa36-9e0244c893a7" />


**Two AI touchpoints:**

1. **Generation** — the Outbox's *Auto-generate* button shells out to whichever CLI fired the hook (`claude` or `codex`). Uses your existing subscription, no separate API key.
2. **Injection** — the Inbox writes approved markdown to its hook's stdout. Claude Code and Codex both natively read hook stdout and prepend it to the model's next turn. That *is* the injection mechanism.

---

## Cross-agent by design

Handoff isn't Claude-only or Codex-only. The shared store is plain JSON; the injection contract is the hook stdout protocol that both CLIs implement identically. **A Codex user can hand context to a Claude user and vice versa**, with no special configuration.

> *Same context. Different agents. Different vendors.*

---

## Repo layout

```
.
├── app/                          # WinUI dashboard
│   └── Handoff.WinUI/            # Tray app + Dashboard, Team, Activity, Settings, About
├── hook/                         # The producer + consumer chain
│   ├── shared-context-summary.js   # Producer hook: fires on Stop, opens Outbox
│   ├── receive-shared-context.js   # Consumer hook: fires on SessionStart + UserPromptSubmit
│   ├── sender/                     # Outbox WinUI popup (Handoff.Sender.exe)
│   └── receiver/                   # Inbox  WinUI popup (Handoff.Receiver.exe)
├── team-members/                 # Per-member shared data (one folder per name)
├── .claude/settings.local.json   # Hook wiring for Claude Code
├── .codex/hooks.json             # Hook wiring for Codex CLI
├── .local/                       # Per-user runtime state (state, watermarks, locks, logs)
└── config.local.json             # Per-user identity, roster, Supabase creds, theme
```

---

## Setup

### Requirements

- **Windows 10/11** (the popups and dashboard are WinUI 3, unpackaged)
- **.NET 8 SDK**
- **Node.js** (for the hook scripts)
- **A Supabase project** with `team_members` and `shared_contexts` tables
- **Claude Code** or **Codex CLI** installed and on PATH

### Build

```bash
# Dashboard / tray app
dotnet build app/Handoff.WinUI/Handoff.WinUI.csproj -p:Platform=x64

# Outbox + Inbox popups (the hooks shell out to these)
dotnet build hook/sender/Handoff.Sender.csproj     -p:Platform=x64
dotnet build hook/receiver/Handoff.Receiver.csproj -p:Platform=x64
```

### Configure

Either run the dashboard and use the **Settings** page, or hand-edit `config.local.json`:

```json
{
  "self": "your-normalized-name",
  "team-members": [
    { "name": "teammate-one", "subscribe": true },
    { "name": "teammate-two", "subscribe": false }
  ],
  "supabase": {
    "url": "https://your-project.supabase.co",
    "key": "sb_publishable_..."
  },
  "theme": "System"
}
```

`subscribe: true` is opt-out at the *teammate* level — anyone you've toggled on can push context that reaches your model (still gated per row by the Inbox popup).

---

## Safety + control

- **Allow-list, not deny-list.** Unknown teammates never reach your model.
- **Per-row consent.** Every incoming context shows an Allow/Deny popup before injection. Both Allow *and* Deny watermark the row, so you're never re-asked about the same content.
- **Local-first.** Your own Supabase project, your own CLI subscription. Handoff itself runs no backend.
- **Stale-lock recovery.** Concurrent hook fires (SessionStart racing UserPromptSubmit) are deduped via file locks with 60-second auto-recovery.
- **Recursion guard.** When the Outbox spawns the CLI to auto-generate a summary, the child process re-fires the hook chain — `HANDOFF_SUMMARY_GENERATION=1` makes those silent no-ops.

---

## Tech stack

- **WinUI 3** (.NET 8, unpackaged) — dashboard, tray, Outbox, Inbox
- **H.NotifyIcon.WinUI** — system tray
- **Node.js** — hook scripts
- **Supabase Postgres + REST** — shared store
- **System.Text.Json** / **JSON.parse** — wire format on both sides

---

## Status

Early. The pieces work end-to-end and have been demoed cross-machine, cross-agent (Claude ↔ Codex). Some moving parts that still want polish:

- "Hide to tray on close" semantics (the tray icon currently dies with the window).
- Live Supabase rewiring after Settings changes (currently asks for an app restart).
- An in-app log viewer for `.local/daemon.log`.
