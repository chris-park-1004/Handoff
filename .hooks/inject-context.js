const fs = require('fs');
const path = require('path');
const os = require('os');
const crypto = require('crypto');
const { spawnSync } = require('child_process');

const HOOKS_DIR = __dirname;
const TEAM_CONTEXT_DIR = path.resolve(HOOKS_DIR, '..');
const CONFIG_PATH = path.join(TEAM_CONTEXT_DIR, 'config.local.json');
const WATERMARKS_PATH = path.join(TEAM_CONTEXT_DIR, '.local', 'watermarks.json');
const GATE_PS1 = path.join(HOOKS_DIR, 'gate.ps1');
const DEBUG_LOG = path.join(os.tmpdir(), 'team-context-debug.log');

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

function hashContent(content) {
  return crypto.createHash('sha256').update(content).digest('hex');
}

function findNewSharedContexts(self, subscriptions, watermarks) {
  const newItems = [];
  if (!fs.existsSync(TEAM_CONTEXT_DIR)) return newItems;

  const memberDirs = fs.readdirSync(TEAM_CONTEXT_DIR, { withFileTypes: true })
    .filter(d => d.isDirectory() && !d.name.startsWith('.') && d.name !== self);

  for (const memberDir of memberDirs) {
    const member = memberDir.name;
    if (subscriptions.length > 0 && !subscriptions.includes(member)) continue;

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

const config = readJSON(CONFIG_PATH, { self: null, subscriptions: [], flags: {} });
const watermarks = readJSON(WATERMARKS_PATH, {});

const newItems = findNewSharedContexts(
  config.self,
  Array.isArray(config.subscriptions) ? config.subscriptions : [],
  watermarks
);

if (newItems.length === 0) {
  log(`${event}: no new context — silent exit`);
  process.exit(0);
}

log(`${event}: ${newItems.length} new item(s): ${newItems.map(i => i.key).join(', ')}`);

const preview = buildPreview(newItems);

const result = spawnSync(
  'powershell',
  ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Sta', '-File', GATE_PS1],
  { input: preview, encoding: 'utf8' }
);

const code = result.status;
log(`${event}: gate exited with code ${code}`);

for (const item of newItems) {
  watermarks[item.key] = item.hash;
}
writeJSON(WATERMARKS_PATH, watermarks);

if (code === 0) {
  process.stdout.write(preview);
}
process.exit(0);
