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

if (process.env.HANDOFF_SUMMARY_GENERATION === '1') {
  process.exit(0);
}

function log(msg) {
  try {
    fs.mkdirSync(path.dirname(DEBUG_LOG), { recursive: true });
    fs.appendFileSync(DEBUG_LOG, `${new Date().toISOString()} producer: ${msg}\n`);
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

function buildSenderPayload(config, commit) {
  return JSON.stringify({
    author: config.self || '',
    repo_root: REPO_ROOT,
    supabase: config.supabase || null,
    ...commit,
  }, null, 2);
}

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

  log(`${event}: opening sender for ${commit.commit_sha.slice(0, 12)} without pre-generating summary`);
  const result = spawnSync(SENDER_EXE, [], {
    input: buildSenderPayload(config, commit),
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
