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
const DIAGNOSTICS_PATH = path.join(REPO_ROOT, '.local', 'receive-shared-context-diagnostics.json');
const LOCK_STALE_MS = 60_000;

// Sender invokes the host CLI (claude/codex) for summary generation, which
// re-fires SessionStart hooks. This guard makes those re-fires no-ops so we
// don't recurse into another receiver popup mid-generation.
if (process.env.HANDOFF_SUMMARY_GENERATION === '1') {
  process.exit(0);
}

/**
 * Append a debug line to the shared team-context log. Best-effort — never
 * throws so a logging failure can't take the hook down.
 */
function log(msg) {
  try {
    fs.mkdirSync(path.dirname(DEBUG_LOG), { recursive: true });
    fs.appendFileSync(DEBUG_LOG, `${new Date().toISOString()} ${msg}\n`);
  } catch (_) {}
}

/**
 * Parse a JSON file, returning `fallback` on any read/parse error. Used for
 * config + watermarks where a missing/corrupt file should reset to defaults
 * instead of crashing the hook.
 */
function readJSON(filePath, fallback) {
  try {
    return JSON.parse(fs.readFileSync(filePath, 'utf8'));
  } catch (_) {
    return fallback;
  }
}

/**
 * Serialize `data` as pretty JSON to `filePath`, creating parent directories
 * as needed. Errors propagate — callers decide whether to swallow.
 */
function writeJSON(filePath, data) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, JSON.stringify(data, null, 2));
}

/**
 * Snapshot the current decision state to disk for post-mortem debugging when
 * the popup doesn't appear as expected. Always best-effort.
 */
function writeDiagnostics(data) {
  try {
    writeJSON(DIAGNOSTICS_PATH, {
      updated_at: new Date().toISOString(),
      ...data,
    });
  } catch (_) {}
}

/**
 * Try to take an exclusive file lock so two concurrent hook fires (e.g.
 * SessionStart + UserPromptSubmit racing) don't both pop up the same item.
 * Returns the open fd on success or null if another instance holds it; if a
 * stale lock (older than LOCK_STALE_MS) is found it gets cleared and retried
 * once so a crashed predecessor doesn't lock us out forever.
 */
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

/**
 * Release the file lock acquired by tryAcquireLock. Both close and unlink are
 * swallowed because a half-released lock will be cleaned up on the next run
 * via the stale-lock fallback.
 */
function releaseLock(handle) {
  try { fs.closeSync(handle); } catch (_) {}
  try { fs.unlinkSync(LOCK_PATH); } catch (_) {}
}

/**
 * SHA-256 hex digest of the input string. Used to fingerprint a Supabase row
 * so we can detect whether the user has already been shown that exact content.
 */
function hashContent(content) {
  return crypto.createHash('sha256').update(content).digest('hex');
}

/**
 * Case-insensitive name equality. Member names come from git config + manual
 * config edits, so casing isn't reliable enough to compare directly.
 */
function sameName(a, b) {
  return String(a || '').toLowerCase() === String(b || '').toLowerCase();
}

/**
 * Build the watermark key + human-readable label for a shared_contexts row.
 * Prefers the row id (one-watermark-per-row), falls back to commit_sha +
 * updated_at + hash prefix so older rows missing an id still get a stable key.
 */
function getContextIdentity(row, hash) {
  const displayKey = `${row.member_name}/${row.branch}`;
  if (row.id !== null && row.id !== undefined && String(row.id).trim() !== '') {
    return {
      displayKey,
      key: `${displayKey}#${row.id}`,
    };
  }

  const parts = [
    row.commit_sha,
    row.updated_at,
    hash.slice(0, 16),
  ]
    .map(value => String(value || '').trim())
    .filter(Boolean);

  return {
    displayKey,
    key: `${displayKey}#${parts.join('#') || hash.slice(0, 16)}`,
  };
}

/**
 * Fingerprint the actual teammate message content, excluding DB identity fields
 * like id/updated_at. This prevents duplicate inserts of the same shared
 * message from re-prompting just because Supabase assigned a new row id.
 */
function getContentKey(row) {
  const content = {
    member_name: row.member_name || '',
    branch: row.branch || '',
    commit_sha: row.commit_sha || '',
    commit_message: row.commit_message || '',
    summary: row.summary || '',
    changed_files: row.changed_files || null,
  };
  return `content#${hashContent(JSON.stringify(content))}`;
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
// row's content hash against watermarks to find what's new for this user.
// New Supabase rows are tracked by id so multiple sends from the same
// member/branch are each handled once. The legacy member/branch key is still
// checked so old watermarks do not immediately re-prompt after this change.
function findNewSharedContexts(self, teamMembers, watermarks, rows) {
  const newItems = [];
  const skipped = {
    malformed: 0,
    self: 0,
    unsubscribed: 0,
    watermarked: 0,
  };

  for (const row of rows) {
    if (!row || !row.member_name || !row.branch) {
      skipped.malformed += 1;
      continue;
    }
    if (self && sameName(row.member_name, self)) {
      skipped.self += 1;
      continue;
    }

    // Allow-list: only inject from members explicitly marked subscribe:true.
    // Unknown members and entries with subscribe omitted/false are skipped.
    const entry = teamMembers.find(m => m && sameName(m.name, row.member_name));
    if (!entry || entry.subscribe !== true) {
      skipped.unsubscribed += 1;
      continue;
    }

    // Hash is computed client-side from the row JSON — the server no longer
    // stores it. Stable across runs because we always serialize the same
    // shape (System.Text.Json on the producer matches JSON.stringify here
    // for the columns we read).
    const hash = hashContent(JSON.stringify(row));
    const identity = getContextIdentity(row, hash);
    const contentKey = getContentKey(row);
    const legacyKey = identity.displayKey;

    if (!watermarks[identity.key] && !watermarks[contentKey] && watermarks[legacyKey] !== hash) {
      newItems.push({
        key: identity.key,
        contentKey,
        displayKey: identity.displayKey,
        hash,
        parsed: row,
      });
    } else {
      skipped.watermarked += 1;
    }
  }

  log(`filter: rows=${rows.length}, new=${newItems.length}, skipped=${JSON.stringify(skipped)}`);
  return newItems;
}

/**
 * Render the markdown preview shown in the receiver popup. Whatever string we
 * return here is the exact text injected into the model context on Allow, so
 * preview-text and injection-text are guaranteed identical (no transform).
 */
function buildPreview(items) {
  return items.map(item => {
    const p = item.parsed || {};
    const summary = p.summary || '(no summary)';
    const commitMessage = p.commit_message || '';
    const sha = p.commit_sha ? ` (${p.commit_sha})` : '';

    const lines = [
      `## From ${item.displayKey || item.key}`,
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
writeJSON(WATERMARKS_PATH, watermarks);

// One Supabase fetch per hook invocation, cached in `rows`. The post-lock
// recheck uses the same snapshot — re-fetching after the lock would catch
// rows pushed in the millisecond gap, but at the cost of doubling network
// latency on the hot path. The watermark comparison itself still re-runs
// (cheap) so we never inject a hash that another instance already consumed.
const rows = fetchSharedContextsFromSupabase(config.supabase);
const teamMembers = Array.isArray(config['team-members']) ? config['team-members'] : [];
log(`config: self=${config.self || '(empty)'}, subscribed=${teamMembers.filter(m => m && m.subscribe === true).map(m => m.name).join(',') || '(none)'}, fetched_rows=${rows.length}`);

const newItems = findNewSharedContexts(config.self, teamMembers, watermarks, rows);
writeDiagnostics({
  event,
  self: config.self || '',
  subscribed_members: teamMembers.filter(m => m && m.subscribe === true).map(m => m.name),
  fetched_rows: rows.length,
  new_items: newItems.map(item => item.key),
  receiver_exe_exists: fs.existsSync(RECEIVER_EXE),
});

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

// Show ONE popup per new item so the user can Allow/Deny each member
// independently. We accumulate the previews of items the user approved and
// emit them at the end as a single combined injection — Claude Code only
// reads stdout once per hook fire.
const allowedPreviews = [];

try {
  for (const item of lockedNewItems) {
    const itemPreview = buildPreview([item]);

    const result = spawnSync(
      RECEIVER_EXE,
      [],
      { input: itemPreview, encoding: 'utf8' }
    );
    const code = result.status;
    log(`${event}: gate (${item.key}) exited with code ${code}`);
    if (result.error) {
      log(`${event}: gate (${item.key}) error: ${result.error.message}`);
    }
    if (result.signal) {
      log(`${event}: gate (${item.key}) signal: ${result.signal}`);
    }
    if (result.stderr) {
      log(`${event}: gate (${item.key}) stderr: ${result.stderr.trim()}`);
    }

    // Rotate-on-suggest: advance the watermark on Allow (0) AND Deny (1) so
    // the same content never re-prompts the user. Other exit codes (signal,
    // crash, ENOENT) leave the watermark untouched so the next fire retries.
    if (code === 0 || code === 1) {
      const seen = {
        hash: item.hash,
        seen_at: new Date().toISOString(),
      };
      watermarks[item.key] = seen;
      watermarks[item.contentKey] = seen;
    } else {
      log(`${event}: gate (${item.key}) did not finish cleanly — watermark unchanged`);
    }

    if (code === 0) {
      allowedPreviews.push(itemPreview);
    }
  }
} finally {
  // Persist watermark progress even if a mid-loop crash happened — we don't
  // want a single bad gate to invalidate the user's prior Allow/Deny clicks.
  writeJSON(WATERMARKS_PATH, watermarks);
  releaseLock(lockHandle);
}

if (allowedPreviews.length > 0) {
  process.stdout.write(allowedPreviews.join('\n\n---\n\n'));
}
process.exit(0);
