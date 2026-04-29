const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { spawnSync } = require('child_process');

const HOOKS_DIR = __dirname;
const REPO_ROOT = path.resolve(HOOKS_DIR, '..');
const CONFIG_PATH = path.join(REPO_ROOT, 'config.local.json');
const WATERMARKS_PATH = path.join(REPO_ROOT, '.local', 'watermarks.json');
const LOCK_PATH = path.join(REPO_ROOT, '.local', 'team-context-hook.lock');
const RECEIVER_EXE = path.join(
  HOOKS_DIR,
  'receiver',
  'bin',
  'x64',
  'Debug',
  'net8.0-windows10.0.19041.0',
  'Handoff.Receiver.exe'
);
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

// Pulls every shared_contexts row via Supabase REST. We use curl (shipped on
// Win10+) to keep this synchronous — hooks must complete fast and predictably,
// and async/await would add a layer for no real gain at this scale.
// Returns [] on any failure so the hook still exits cleanly.
function fetchSharedContextsFromSupabase(supabase) {
  if (!supabase || !supabase.url || !supabase.key) {
    log('supabase config missing — fetch skipped');
    return [];
  }
  const endpoint = `${supabase.url}/rest/v1/shared_contexts?select=*`;
  const result = spawnSync('curl', [
    '-s', '-S',
    '-H', `apikey: ${supabase.key}`,
    '-H', `Authorization: Bearer ${supabase.key}`,
    '-H', 'Accept: application/json',
    endpoint,
  ], { encoding: 'utf8' });

  if (result.status !== 0) {
    log(`curl failed (status=${result.status}): ${result.stderr || ''}`);
    return [];
  }
  try {
    const parsed = JSON.parse(result.stdout || '[]');
    if (!Array.isArray(parsed)) {
      log(`unexpected supabase payload: ${result.stdout.slice(0, 200)}`);
      return [];
    }
    return parsed;
  } catch (e) {
    log(`failed to parse supabase response: ${e.message}`);
    return [];
  }
}

// Filters fetched rows by self-exclusion + subscribe flag, then compares each
// row's content_hash against watermarks to find what's new for this user.
// "key" matches the file-based version (member/branch) so existing
// watermarks.json entries keep working without migration.
function findNewSharedContexts(self, teamMembers, watermarks, rows) {
  const newItems = [];
  for (const row of rows) {
    if (!row || !row.member || !row.branch) continue;
    if (self && row.member === self) continue;

    // Missing roster entry => treated as subscribed (matches daemon's
    // opt-out default for newly discovered members). Explicit subscribe:false
    // is the only thing that filters a member out.
    const entry = teamMembers.find(m => m && m.name === row.member);
    if (entry && entry.subscribe === false) continue;

    // Trust the DB-provided hash. If it's missing for any reason, fall back
    // to hashing the row JSON so watermarking still detects changes.
    const hash = row.content_hash || hashContent(JSON.stringify(row));
    const key = `${row.member}/${row.branch}`;

    if (watermarks[key] !== hash) {
      newItems.push({ key, hash, parsed: row });
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

const config = readJSON(CONFIG_PATH, { self: null, 'team-members': [], supabase: null });
let watermarks = readJSON(WATERMARKS_PATH, {});

// One Supabase fetch per hook invocation, cached in `rows`. The post-lock
// recheck uses the same snapshot — re-fetching after the lock would catch
// rows pushed in the millisecond gap, but at the cost of doubling network
// latency on the hot path. The watermark comparison itself still re-runs
// (cheap) so we never inject a hash that another instance already consumed.
const rows = fetchSharedContextsFromSupabase(config.supabase);
const teamMembers = Array.isArray(config['team-members']) ? config['team-members'] : [];

const newItems = findNewSharedContexts(config.self, teamMembers, watermarks, rows);

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
const lockedNewItems = findNewSharedContexts(config.self, teamMembers, watermarks, rows);

if (lockedNewItems.length === 0) {
  log(`${event}: no new context after lock — silent exit`);
  releaseLock(lockHandle);
  process.exit(0);
}

const preview = buildPreview(lockedNewItems);

let result;
try {
  result = spawnSync(
    RECEIVER_EXE,
    [],
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
