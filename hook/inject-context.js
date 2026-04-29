const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { spawnSync } = require('child_process');

const HOOKS_DIR = __dirname;
const REPO_ROOT = path.resolve(HOOKS_DIR, '..');
const TEAM_CONTEXT_DIR = path.resolve(HOOKS_DIR, '../team-members');
const CONFIG_PATH = path.join(REPO_ROOT, 'config.local.json');
const WATERMARKS_PATH = path.join(REPO_ROOT, '.local', 'watermarks.json');
const LOCK_PATH = path.join(REPO_ROOT, '.local', 'team-context-hook.lock');
const GATE_PS1 = path.join(HOOKS_DIR, 'gate.ps1');
const DEBUG_LOG = path.join(REPO_ROOT, '.local', 'team-context-debug.log');
const LOCK_STALE_MS = 60_000;

function log(msg) {
  try {
    fs.appendFileSync(DEBUG_LOG, `${new Date().toISOString()} ${msg}\n`);
  } catch (_) {}
}

function readJSON(filePath, fallback) {
  try {
    return JSON.parse(fs.readFileSync(filePath, 'utf8'));
  } catch (_) {
    return fallback;
  }
}

function writeJSON(filePath, data) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, JSON.stringify(data, null, 2));
}

function tryAcquireLock() {
  fs.mkdirSync(path.dirname(LOCK_PATH), { recursive: true });

  try {
    const handle = fs.openSync(LOCK_PATH, 'wx');
    fs.writeFileSync(handle, `${process.pid}\n${new Date().toISOString()}\n`);
    return handle;
  } catch (error) {
    if (error && error.code === 'EEXIST') {
      try {
        const age = Date.now() - fs.statSync(LOCK_PATH).mtimeMs;
        if (age > LOCK_STALE_MS) {
          fs.unlinkSync(LOCK_PATH);
          return tryAcquireLock();
        }
      } catch (_) {}

      return null;
    }

    throw error;
  }
}

function releaseLock(handle) {
  try { fs.closeSync(handle); } catch (_) {}
  try { fs.unlinkSync(LOCK_PATH); } catch (_) {}
}

function hashContent(content) {
  return crypto.createHash('sha256').update(content).digest('hex');
}

function findNewSharedContexts(self, teamMembers, watermarks) {
  const newItems = [];
  if (!fs.existsSync(TEAM_CONTEXT_DIR)) return newItems;

  const memberDirs = fs.readdirSync(TEAM_CONTEXT_DIR, { withFileTypes: true })
    .filter(d => d.isDirectory() && !d.name.startsWith('.') && d.name !== self);

  for (const memberDir of memberDirs) {
    const member = memberDir.name;
    // Find this member's roster entry. Missing entry → treat as subscribed
    // (matches daemon's default for newly discovered members). Explicit
    // subscribe:false → skip.
    const entry = teamMembers.find(m => m && m.name === member);
    if (entry && entry.subscribe === false) continue;

    const memberPath = path.join(TEAM_CONTEXT_DIR, member);
    const branches = fs.readdirSync(memberPath, { withFileTypes: true })
      .filter(d => d.isDirectory());

    for (const branchDir of branches) {
      const branch = branchDir.name;
      const sharedPath = path.join(memberPath, branch, 'shared-context.json');
      if (!fs.existsSync(sharedPath)) continue;

      const content = fs.readFileSync(sharedPath, 'utf8');
      const hash = hashContent(content);
      const key = `${member}/${branch}`;

      if (watermarks[key] !== hash) {
        let parsed = null;
        try { parsed = JSON.parse(content); } catch (_) {}
        newItems.push({ key, content, hash, parsed });
      }
    }
  }

  return newItems;
}

function buildPreview(items) {
  return items.map(item => {
    const p = item.parsed || {};
    const summary = p.summary || '(no summary)';
    const commitMessage = p.commit_message || '';
    const sha = p.commit_sha ? ` (${p.commit_sha})` : '';

    const lines = [
      `## From ${item.key}`,
      '',
      `**Summary**: ${summary}`,
    ];

    if (commitMessage) {
      lines.push('');
      lines.push(`**Commit**: ${commitMessage}${sha}`);
    }

    return lines.join('\n');
  }).join('\n\n---\n\n');
}

let stdinJson = '';
try { stdinJson = fs.readFileSync(0, 'utf8'); } catch (_) {}

let event = 'unknown';
try {
  const parsed = JSON.parse(stdinJson || '{}');
  event = parsed.hook_event_name || parsed.hookEventName || 'unknown';
} catch (_) {}

log(`${event} fired`);

const config = readJSON(CONFIG_PATH, { self: null, 'team-members': [] });
let watermarks = readJSON(WATERMARKS_PATH, {});

const newItems = findNewSharedContexts(
  config.self,
  Array.isArray(config['team-members']) ? config['team-members'] : [],
  watermarks
);

if (newItems.length === 0) {
  log(`${event}: no new context — silent exit`);
  process.exit(0);
}

log(`${event}: ${newItems.length} new item(s): ${newItems.map(i => i.key).join(', ')}`);

const lockHandle = tryAcquireLock();
if (lockHandle === null) {
  log(`${event}: another hook instance is active — silent exit`);
  process.exit(0);
}

watermarks = readJSON(WATERMARKS_PATH, {});
const lockedNewItems = findNewSharedContexts(
  config.self,
  Array.isArray(config['team-members']) ? config['team-members'] : [],
  watermarks
);

if (lockedNewItems.length === 0) {
  log(`${event}: no new context after lock — silent exit`);
  releaseLock(lockHandle);
  process.exit(0);
}

const preview = buildPreview(lockedNewItems);

let result;
try {
  result = spawnSync(
    'powershell',
    ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Sta', '-File', GATE_PS1],
    { input: preview, encoding: 'utf8' }
  );
} finally {
  releaseLock(lockHandle);
}

const code = result.status;
log(`${event}: gate exited with code ${code}`);
if (result.error) {
  log(`${event}: gate error: ${result.error.message}`);
}
if (result.signal) {
  log(`${event}: gate signal: ${result.signal}`);
}
if (result.stderr) {
  log(`${event}: gate stderr: ${result.stderr.trim()}`);
}

if (code === 0 || code === 1) {
  for (const item of lockedNewItems) {
    watermarks[item.key] = item.hash;
  }
  writeJSON(WATERMARKS_PATH, watermarks);
} else {
  log(`${event}: gate did not finish cleanly — watermark unchanged`);
}

if (code === 0) {
  process.stdout.write(preview);
}
process.exit(0);
