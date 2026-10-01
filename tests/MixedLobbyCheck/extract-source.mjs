import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const [input, output] = process.argv.slice(2);
if (!input || !output) throw new Error('Usage: node extract-source.mjs <SchoolScreenPlugin.cs> <output.cs>');
const source = fs.readFileSync(input, 'utf8');
const methods = [
  'FindSchoolBoard', 'UpdateHostState', 'ReadIsServer', 'ResetLobbyState', 'UpdateLobbyPresence', 'EnsureHostMember',
  'PruneStalePeers', 'GetLocalPlayerName', 'CreatePeerToken', 'EncodeBoardMarker', 'TryDecodeBoardMarker',
  'GetActivePeerCount', 'GetLowestActivePeerToken', 'BroadcastPeerPresence', 'SendPeerPacket',
  'AddVideoToQueue', 'ToggleSkipVote', 'SkipCurrentVideo', 'IsLocalPlaybackCoordinator', 'UpdateQueuePlayback',
  'RequestQueueStart', 'ScheduleQueueStart', 'AdvanceQueue', 'PlayQueuedVideo', 'RemoveQueuedVideo',
  'BroadcastQueueSnapshot', 'BroadcastVoteStatus', 'GetRequiredVotes', 'SendBoardPayload', 'ShortTitle',
  'OpenBrowserVideo', 'NextQueueEventSequence', 'AcceptPlaybackEvent', 'SendBoardCommand',
  'HandleNetworkCommand', 'HandlePeerPresence', 'ApplyPeerVote', 'ApplyQueueAdd', 'PackVideoIdGroup',
  'VideoIdCharacterValue', 'UnpackVideoId', 'AppendVideoIdGroup', 'BindPeerIdentity', 'GetRemotePlayerName',
  'ReceiveBoardCommand', 'ParseFloat', 'ParseVideoId', 'IsVideoId', 'QueryValue'
];

// Copy whole declarations and complete method bodies unchanged. The scanner skips
// comments and strings so braces inside the product's text do not end a method.
function blockEnd(start) {
  let depth = 0, state = 'code';
  for (let i = start; i < source.length; i++) {
    const c = source[i], next = source[i + 1];
    if (state === 'line') { if (c === '\n') state = 'code'; continue; }
    if (state === 'block') { if (c === '*' && next === '/') { state = 'code'; i++; } continue; }
    if (state === 'string' || state === 'char') {
      if (c === '\\') { i++; continue; }
      if (c === (state === 'string' ? '"' : "'")) state = 'code';
      continue;
    }
    if (state === 'verbatim') { if (c === '"') { if (next === '"') i++; else state = 'code'; } continue; }
    if (c === '/' && next === '/') { state = 'line'; i++; continue; }
    if (c === '/' && next === '*') { state = 'block'; i++; continue; }
    if (c === '@' && next === '"') { state = 'verbatim'; i++; continue; }
    if (c === '"') { state = 'string'; continue; }
    if (c === "'") { state = 'char'; continue; }
    if (c === '{') depth++;
    if (c === '}' && --depth === 0) return i + 1;
  }
  throw new Error('Unclosed source block');
}
function extract(name, isClass = false) {
  const pattern = isClass
    ? new RegExp(`^[ \\t]*private sealed class ${name}\\b`, 'm')
    : new RegExp(`^[ \\t]*private (?:static )?[^\\r\\n]+?\\b${name}\\([^;{}]*\\)`, 'm');
  const match = pattern.exec(source);
  if (!match) throw new Error(`Required product declaration not found: ${name}`);
  const opening = source.indexOf('{', match.index + match[0].length);
  const end = blockEnd(opening);
  return source.slice(match.index, end);
}
// Include new product helpers reached by these methods automatically, while keeping
// the named IO/native boundaries as stubs. This prevents a copied test implementation
// from silently taking over when product code grows another pure helper.
const boundaries = new Set(['StartBrowser', 'SendHelper', 'RequestQueueTitle', 'UpdateLocalVolume']);
const declarations = new Set([...source.matchAll(/^[ \t]*private (?:static )?[^\r\n]+?\b(\w+)\([^;{}]*\)/gm)].map(match => match[1]));
const selected = methods.map(name => ({ name, text: extract(name) }));
const names = new Set(methods);
for (let i = 0; i < selected.length; i++) {
  for (const match of selected[i].text.matchAll(/\b(\w+)\s*\(/g)) {
    const name = match[1];
    if (declarations.has(name) && !names.has(name) && !boundaries.has(name)) {
      selected.push({ name, text: extract(name) }); names.add(name);
    }
  }
}
const firstField = source.indexOf('        private const float BoardCommandMarker');
const firstNested = source.indexOf('        private sealed class LobbyMember');
if (firstField < 0 || firstNested < firstField) throw new Error('Could not identify product field declarations.');
const fields = source.slice(firstField, firstNested);
const all = `// GENERATED FROM PRODUCT SOURCE. DO NOT EDIT.\nusing PurrNet;\nusing UnityEngine;\nusing System;\nusing System.Collections.Generic;\nusing System.Collections.Concurrent;\nusing System.Diagnostics;\nusing System.IO.Pipes;\nusing System.Reflection;\nusing System.Text;\nusing System.Threading;\nnamespace OnTogetherSchoolScreen { public sealed partial class SchoolScreenPlugin {\n${fields}\n${extract('LobbyMember', true)}\n${extract('QueueEntry', true)}\n${selected.map(item => item.text).join('\n\n')}\n} }\n`;
fs.mkdirSync(path.dirname(path.resolve(output)), { recursive: true });
fs.writeFileSync(output, all);
fs.writeFileSync(`${output}.json`, JSON.stringify({
  input: path.resolve(input), sourceSha256: crypto.createHash('sha256').update(source).digest('hex'),
  methods: selected.map(item => ({ name: item.name, sha256: crypto.createHash('sha256').update(item.text).digest('hex') })),
  boundaryStubs: ['Unity/PurrNet APIs', 'StartBrowser', 'SendHelper', 'RequestQueueTitle', 'UpdateLocalVolume'],
  limitation: 'Selected unchanged product method bodies execute against test doubles. This is not a live Unity or network test.'
}, null, 2));
const calls = [...source.matchAll(/\.FillTheBlanksRPC\s*\(/g)].length;
const wrapperCalls = [...extract('SendBoardPayload').matchAll(/\.FillTheBlanksRPC\s*\(/g)].length;
if (calls !== 1 || wrapperCalls !== 1) {
  console.error(`FAIL: ${calls} outgoing board RPC calls found, ${wrapperCalls} in the safe envelope wrapper; all outgoing packets must use that one wrapper.`);
  process.exitCode = 1;
} else console.log(`PASS: extracted ${selected.length} unchanged product methods; one board RPC call, inside SendBoardPayload.`);
