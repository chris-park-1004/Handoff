const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const HOOKS_DIR = __dirname;
const REPO_ROOT = path.resolve(HOOKS_DIR, '..');
const CONFIG_PATH = path.join(REPO_ROOT, 'config.local.json');
const STATE_PATH = path.join(REPO_ROOT, '.local', 'producer-state.json');
const LOCK_PATH = path.join(REPO_ROOT, '.local', 'producer-hook.lock');
const DEBUG_LOG = path.join(REPO_ROOT, '.local', 'team-context-debug.log');
const SENDER_EXE = path.join(
  HOOKS_DIR,
  'sender',
  'bin',
  'x64',
  'Debug',
  'net8.0-windows10.0.19041.0',
  'Handoff.Sender.exe'
);
const LOCK_STALE_MS = 60_000;

// When the sender opens claude/codex to generate a summary, that child
// process re-fires hooks. This guard makes the re-fire a silent no-op so we
// don't recurse into another sender popup mid-generation.
if (process.env.HANDOFF_SUMMARY_GENERATION === '1') {
  process.exit(0);
}

/**
 * Append a debug line tagged with `producer:` so its origin is obvious in the
 * shared team-context log. Best-effort — never throws.
 */
function log(msg) {
  try {
    fs.mkdirSync(path.dirname(DEBUG_LOG), { recursive: true });
    fs.appendFileSync(DEBUG_LOG, `${new Date().toISOString()} producer: ${msg}\n`);
  } catch (_) {}
}

/**
 * Parse a JSON file, returning `fallback` on any read/parse error. Used for
 * config + producer state where a missing file means "first run" rather than
 * an error condition.
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
 * as needed.
 */
function writeJSON(filePath, data) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, JSON.stringify(data, null, 2));
}

/**
 * Try to take an exclusive lock so concurrent producer-side fires (SessionStart
 * + Stop racing on session end) don't both spawn the sender for the same
 * commit. Returns null when another instance holds the lock; auto-recovers
 * locks older than LOCK_STALE_MS so a crashed predecessor isn't fatal.
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
 * Release the lock acquired by tryAcquireLock. Failures are swallowed — the
 * stale-lock fallback in the next run handles cleanup.
 */
function releaseLock(handle) {
  try { fs.closeSync(handle); } catch (_) {}
  try { fs.unlinkSync(LOCK_PATH); } catch (_) {}
}

/**
 * Read HEAD's branch + the most recent commit metadata directly from the
 * .git directory. We deliberately avoid `git log -1` here because hook
 * subprocesses sometimes inherit a working directory that confuses git's
 * porcelain commands; reading reflog + HEAD is cheap and unambiguous.
 */
function getCommitInfo() {
  const gitDir = findGitDir(REPO_ROOT);
  const headText = fs.readFileSync(path.join(gitDir, 'HEAD'), 'utf8').trim();
  const branch = headText.startsWith('ref:')
    ? headText.slice('ref:'.length).trim().replace(/^refs\/heads\//, '')
    : 'detached-head';

  const reflogPath = path.join(gitDir, 'logs', 'HEAD');
  const reflog = fs.readFileSync(reflogPath, 'utf8')
    .split(/\r?\n/)
    .map(line => line.trim())
    .filter(Boolean);

  if (reflog.length === 0) {
    throw new Error(`HEAD reflog is empty at ${reflogPath}`);
  }

  const latest = parseReflogLine(reflog[reflog.length - 1]);

  return {
    commit_sha: latest.commitSha,
    branch,
    commit_message: latest.message,
    timestamp: latest.timestamp,
    changed_files: getChangedFiles(latest.commitSha),
  };
}

/**
 * Return the list of files touched by `commitSha`. Uses `--root` so the very
 * first commit of a repo (which has no parent) still produces a file list
 * instead of erroring out. Returns [] on any failure.
 */
function getChangedFiles(commitSha) {
  const result = spawnSync('git', [
    '-C',
    REPO_ROOT,
    'diff-tree',
    '--no-commit-id',
    '--name-only',
    '-r',
    '--root',
    commitSha,
  ], {
    encoding: 'utf8',
    windowsHide: true,
    timeout: 15_000,
  });

  if (result.error || result.status !== 0) {
    log(`changed files lookup failed: ${result.error ? result.error.message : (result.stderr || '').trim()}`);
    return [];
  }

  return (result.stdout || '')
    .split(/\r?\n/)
    .map(line => line.trim())
    .filter(Boolean);
}

/**
 * Walk up from `startDirectory` looking for a .git entry. Handles both real
 * .git directories and gitfile pointers (used by submodules and worktrees) so
 * the hook keeps working in non-trivial repo layouts.
 */
function findGitDir(startDirectory) {
  let current = startDirectory;
  while (current && path.dirname(current) !== current) {
    const candidate = path.join(current, '.git');
    if (fs.existsSync(candidate)) {
      const stat = fs.statSync(candidate);
      if (stat.isDirectory()) {
        return candidate;
      }

      const text = fs.readFileSync(candidate, 'utf8').trim();
      const match = /^gitdir:\s*(.+)$/i.exec(text);
      if (!match) {
        throw new Error(`Unsupported .git file format at ${candidate}`);
      }
      return path.resolve(current, match[1].trim());
    }
    current = path.dirname(current);
  }
  throw new Error(`No .git directory found from ${startDirectory}`);
}

/**
 * Extract commit SHA, timestamp, and message from a single HEAD reflog line.
 * Reflog format is `<old> <new> <name> <email> <unixSec> <tz>\t<action>:msg`,
 * stable across git versions so a regex on it is safe.
 */
function parseReflogLine(line) {
  const tabIndex = line.indexOf('\t');
  const meta = tabIndex === -1 ? line : line.slice(0, tabIndex);
  const action = tabIndex === -1 ? '' : line.slice(tabIndex + 1);
  const parts = meta.split(' ');
  const commitSha = parts[1] || '';

  if (!/^[0-9a-f]{40}$/i.test(commitSha)) {
    throw new Error(`Could not parse HEAD reflog line: ${line}`);
  }

  const timezone = parts[parts.length - 1] || '+0000';
  const unixSeconds = Number(parts[parts.length - 2]);
  const timestamp = Number.isFinite(unixSeconds)
    ? formatGitTimestamp(unixSeconds, timezone)
    : new Date().toISOString();

  const message = action.replace(/^commit(?:\s+\([^)]*\))?:\s*/i, '').trim()
    || 'Updated shared context';

  return { commitSha, timestamp, message };
}

/**
 * Format a unix timestamp + git timezone offset (e.g. "+0900") as ISO-8601 in
 * that local timezone. We can't use Date.toISOString() because it forces UTC
 * and the producer's tz is what teammates actually want to see.
 */
function formatGitTimestamp(unixSeconds, timezone) {
  const sign = timezone.startsWith('-') ? -1 : 1;
  const hours = Number(timezone.slice(1, 3)) || 0;
  const minutes = Number(timezone.slice(3, 5)) || 0;
  const offsetMinutes = sign * ((hours * 60) + minutes);
  const localMillis = (unixSeconds + (offsetMinutes * 60)) * 1000;
  const local = new Date(localMillis);

  const pad = value => String(value).padStart(2, '0');
  return [
    local.getUTCFullYear(),
    '-',
    pad(local.getUTCMonth() + 1),
    '-',
    pad(local.getUTCDate()),
    'T',
    pad(local.getUTCHours()),
    ':',
    pad(local.getUTCMinutes()),
    ':',
    pad(local.getUTCSeconds()),
    timezone.slice(0, 3),
    ':',
    timezone.slice(3, 5),
  ].join('');
}

/**
 * Identify which CLI invoked this hook so the sender can pick a matching
 * generator. Claude Code injects CLAUDE_PROJECT_DIR into hook subprocesses;
 * Codex does not. Calling `codex` from a Claude session (or vice versa)
 * fails with "file not found" because the other CLI isn't on PATH.
 */
function detectCli() {
  if (process.env.CLAUDE_PROJECT_DIR) return 'claude';
  return 'codex';
}

/**
 * Build the JSON blob piped into Sender.exe over stdin. Includes the cli tag
 * so Sender knows which generator to use for Auto-generate.
 */
function buildSenderPayload(config, commit, cli) {
  return JSON.stringify({
    author: config.self || '',
    repo_root: REPO_ROOT,
    supabase: config.supabase || null,
    cli,
    ...commit,
  }, null, 2);
}

/**
 * Case-insensitive check for the Stop hook event. Stop is the only event
 * that should actually open the sender; SessionStart fires just to record a
 * baseline for the "did HEAD change?" comparison.
 */
function isStopEvent(eventName) {
  return String(eventName || '').toLowerCase() === 'stop';
}

let stdinJson = '';
try { stdinJson = fs.readFileSync(0, 'utf8'); } catch (_) {}

let event = 'unknown';
try {
  const parsed = JSON.parse(stdinJson || '{}');
  event = parsed.hook_event_name || parsed.hookEventName || 'unknown';
} catch (_) {}

log(`${event} fired`);

let lockHandle = null;
try {
  lockHandle = tryAcquireLock();
  if (lockHandle === null) {
    log(`${event}: another producer hook instance is active — silent exit`);
    process.exit(0);
  }

  const config = readJSON(CONFIG_PATH, { self: '' });
  const state = readJSON(STATE_PATH, {});
  const commit = getCommitInfo();

  if (!isStopEvent(event)) {
    state.last_seen_commit_sha = commit.commit_sha;
    state.updated_at = new Date().toISOString();
    writeJSON(STATE_PATH, state);
    log(`${event}: producer baseline set at ${commit.commit_sha.slice(0, 12)} — waiting for Stop`);
    process.exit(0);
  }

  if (!state.last_seen_commit_sha) {
    state.last_seen_commit_sha = commit.commit_sha;
    state.updated_at = new Date().toISOString();
    writeJSON(STATE_PATH, state);
    log(`${event}: initialized producer state at ${commit.commit_sha.slice(0, 12)}`);
    process.exit(0);
  }

  // TEST MODE: while validating the sender flow, open the producer window on
  // every Stop event even when HEAD did not change. Re-enable the guard below
  // when the real flow should prompt only once per new local commit.
  //
  // if (state.last_seen_commit_sha === commit.commit_sha) {
  //   log(`${event}: no new local commit — silent exit`);
  //   process.exit(0);
  // }

  if (!fs.existsSync(SENDER_EXE)) {
    log(`${event}: sender exe missing at ${SENDER_EXE}`);
    process.exit(0);
  }

  const cli = detectCli();
  log(`${event}: opening sender for ${commit.commit_sha.slice(0, 12)} (cli=${cli}) without pre-generating summary`);
  const result = spawnSync(SENDER_EXE, [], {
    input: buildSenderPayload(config, commit, cli),
    encoding: 'utf8',
    windowsHide: false,
  });

  log(`${event}: sender exited with code ${result.status}`);
  if (result.error) {
    log(`${event}: sender error: ${result.error.message}`);
  }
  if (result.stderr) {
    log(`${event}: sender stderr: ${result.stderr.trim()}`);
  }

  state.last_seen_commit_sha = commit.commit_sha;
  state.updated_at = new Date().toISOString();
  writeJSON(STATE_PATH, state);
} catch (error) {
  log(`${event}: ${error.message}`);
} finally {
  if (lockHandle !== null) {
    releaseLock(lockHandle);
  }
}

process.exit(0);
