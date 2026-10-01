// Runs the upstream installer, with filesystem/provider adapters, against real package bytes.
// This is an installer-code regression check, not a mod-manager UI test.
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import vm from 'node:vm';
import { createRequire, stripTypeScriptTypes } from 'node:module';
import crypto from 'node:crypto';

const [managerSource, oldZip, newZip, outputDir, jszipModule] = process.argv.slice(2);
if (!outputDir) throw new Error('Usage: node --experimental-vm-modules tests/manager-install.mjs <r2modman-source> <0.1.9.zip> <new.zip> <fresh-output-dir> [jszip-module-path]');
const require = createRequire(import.meta.url);
const JSZip = require(jszipModule || 'jszip');
const sourceRoot = path.resolve(managerSource);
const testRoot = path.resolve(outputDir);
try { await fs.access(testRoot); throw new Error('Use a fresh output directory.'); }
catch (error) { if (error.code !== 'ENOENT') throw error; }
await fs.mkdir(testRoot, { recursive: true });
const copiedTargets = new Map();
const collisions = [];
function checkedTarget(target) {
  const absolute = path.resolve(target);
  const relative = path.relative(testRoot, absolute);
  if (!relative || relative.startsWith('..') || path.isAbsolute(relative))
    throw new Error(`Filesystem mutation outside the test directory: ${absolute}`);
  return absolute;
}
async function exists(target) {
  try { await fs.access(target); return true; } catch (error) { if (error.code === 'ENOENT') return false; throw error; }
}
const adapter = {
  readdir: fs.readdir, lstat: fs.lstat, stat: fs.stat, readFile: fs.readFile, exists,
  mkdirs: target => fs.mkdir(checkedTarget(target), { recursive: true }),
  writeFile: (target, bytes) => fs.writeFile(checkedTarget(target), bytes),
  copyFile: async (source, destination) => {
    const target = checkedTarget(destination);
    if (copiedTargets.has(target)) collisions.push(path.relative(testRoot, target));
    copiedTargets.set(target, source);
    await fs.copyFile(source, target);
  },
  copyFolder: (source, destination) => fs.cp(source, checkedTarget(destination), { recursive: true }),
  rename: (source, destination) => fs.rename(checkedTarget(source), checkedTarget(destination)),
  unlink: target => fs.unlink(checkedTarget(target)),
  rmdir: target => fs.rmdir(checkedTarget(target)),
  emptyDirectory: async target => {
    const directory = checkedTarget(target);
    for (const entry of await fs.readdir(directory)) {
      const child = checkedTarget(path.join(directory, entry));
      await fs.rm(child, { recursive: true });
    }
  },
};
const context = vm.createContext({ console });
const modules = new Map();
function synthetic(key, values) {
  if (!modules.has(key)) {
    modules.set(key, new vm.SyntheticModule(Object.keys(values), function () {
      for (const [name, value] of Object.entries(values)) this.setExport(name, value);
    }, { context, identifier: key }));
  }
  return modules.get(key);
}
async function sourceModule(filename) {
  if (!modules.has(filename)) {
    const text = await fs.readFile(filename, 'utf8');
    let javascript = stripTypeScriptTypes(text, { mode: 'transform' });
    // Node's TS stripper preserves imports that the upstream bundler would erase as types.
    // Supply inert bindings for exported type aliases; installer runtime code is unchanged.
    for (const match of text.matchAll(/export type ([A-Za-z_][A-Za-z0-9_]*)\s*=/g))
      javascript += '\nexport const ' + match[1] + ' = undefined;';
    modules.set(filename, new vm.SourceTextModule(javascript, { context, identifier: filename }));
  }
  return modules.get(filename);
}
const schema = JSON.parse(await fs.readFile(path.join(sourceRoot, 'src/assets/data/ecosystem.json'), 'utf8'));
const game = schema.games['on-together'].r2modman.find(item => item.settingsIdentifier === 'OnTogetherVirtualCoWorking');
assert.equal(game.packageLoader, 'bepinex');
const schemaTypes = await sourceModule(path.join(sourceRoot, 'src/assets/data/ecosystemTypes.ts'));
await schemaTypes.link(() => { throw new Error('Unexpected schema dependency.'); });
await schemaTypes.evaluate();
const link = async (specifier, parent) => {
  if (specifier === 'yaml') return synthetic('yaml', { default: { parse() { throw new Error('Unexpected STATE rule.'); }, stringify() { throw new Error('Unexpected STATE rule.'); } } });
  if (specifier.endsWith('/FsProvider')) return synthetic('fs-provider', { default: { instance: adapter } });
  if (specifier.endsWith('/path/path')) return synthetic('path-provider', { default: path });
  if (specifier.endsWith('/ThunderstoreSchema')) return synthetic('schema-provider', {
    TrackingMethod: schemaTypes.namespace.TrackingMethod,
    EcosystemSupportedGames: { value: [['on-together', game]] },
    getGameConfigBySettingsIdentifier: key => key === game.settingsIdentifier ? game : undefined,
  });
  if (specifier.endsWith('/GameManager')) return synthetic('game-manager', { default: { activeGame: game } });
  if (specifier.endsWith('/ConflictManagementProvider')) return synthetic('conflict-provider', { default: { instance: {} } });
  if (specifier.endsWith('/PathResolver')) return synthetic('path-resolver', { default: { MOD_ROOT: testRoot } });
  if (specifier.endsWith('/ZipProvider')) return synthetic('zip-provider', { default: { instance: {} } });
  if (specifier.endsWith('/Profile')) return synthetic('profile-types', { default: class {}, ImmutableProfile: class {} });
  if (specifier.endsWith('/ManifestV2')) return synthetic('manifest-types', { default: class {} });
  if (specifier.endsWith('/PackageInstaller')) return synthetic('installer-types', { InstallArgs: class {}, PackageInstaller: class {} });
  if (specifier.endsWith('/ModFileTracker')) return synthetic('tracker-types', { default: class {} });
  if (!specifier.startsWith('.')) throw new Error(`Unexpected dependency: ${specifier}`);
  return sourceModule(path.resolve(path.dirname(parent.identifier), specifier + '.ts'));
};
const entry = await sourceModule(path.join(sourceRoot, 'src/installers/InstallRulePluginInstaller.ts'));
await entry.link(link);
await entry.evaluate();
const installer = new entry.namespace.InstallRulePluginInstaller();
const mod = { getName: () => 'QualityControled-OnTogetherSchoolScreen', isEnabled: () => true };
async function unpack(zipPath, name) {
  const archive = await JSZip.loadAsync(await fs.readFile(zipPath));
  const cachePath = path.join(testRoot, 'cache', name);
  for (const [name, file] of Object.entries(archive.files)) {
    const destination = checkedTarget(path.join(cachePath, name));
    assert(!path.relative(cachePath, destination).startsWith('..'), 'ZIP path traversal');
    if (file.dir) await fs.mkdir(destination, { recursive: true });
    else {
      await fs.mkdir(path.dirname(destination), { recursive: true });
      await fs.writeFile(destination, await file.async('nodebuffer'));
    }
  }
  return { archive, cachePath };
}
const profile = {
  joinToProfilePath: (...parts) => path.join(testRoot, 'profile', ...parts),
};
const pluginDir = profile.joinToProfilePath('BepInEx', 'plugins', mod.getName());
const oldPackage = await unpack(oldZip, 'old');
await installer.install({ mod, profile, packagePath: oldPackage.cachePath });
assert.equal(await exists(path.join(pluginDir, 'www/index.html')), false, 'Reproduce 0.1.9 missing www folder');
assert.equal(await exists(path.join(pluginDir, 'index.html')), true);
assert(collisions.some(target => target.endsWith('WebView2Loader.dll')), 'Reproduce 0.1.9 duplicate native loader');
await installer.uninstall({ mod, profile, packagePath: oldPackage.cachePath });
assert.equal(await exists(pluginDir), false, 'Old package must uninstall cleanly');
copiedTargets.clear();
collisions.length = 0;
const newPackage = await unpack(newZip, 'new');
const entries = Object.keys(newPackage.archive.files).filter(name => !newPackage.archive.files[name].dir);
assert.equal(entries.filter(name => name.endsWith('WebView2Loader.dll')).length, 1);
assert(entries.includes('BepInEx/plugins/www/index.html'));
await installer.install({ mod, profile, packagePath: newPackage.cachePath });
assert.equal(collisions.length, 0);
for (const relative of ['OnTogetherSchoolScreen.dll', 'SchoolScreenBrowser.exe', 'SchoolScreenBrowser.exe.config', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll', 'www/index.html']) {
  const source = await fs.readFile(path.join(newPackage.cachePath, 'BepInEx/plugins', relative));
  const installed = await fs.readFile(path.join(pluginDir, relative));
  assert.equal(crypto.createHash('sha256').update(installed).digest('hex'), crypto.createHash('sha256').update(source).digest('hex'), relative);
}
await installer.disable({ mod, profile });
assert.equal(await exists(path.join(pluginDir, 'www/index.html.old')), true);
await installer.enable({ mod: { ...mod, isEnabled: () => false }, profile });
assert.equal(await exists(path.join(pluginDir, 'www/index.html')), true);
const report = {
  checks: 'PASS', manager: 'r2modman production InstallRulePluginInstaller',
  sourceVersion: JSON.parse(await fs.readFile(path.join(sourceRoot, 'package.json'), 'utf8')).version,
  game: game.settingsIdentifier,
  oldPackage: 'Reproduced flattened HTML and colliding native loader',
  newPackage: 'Upgrade, preserved www path, dependency hashes, single native loader, disable and enable',
  guiTested: false, browserPath: path.join(pluginDir, 'SchoolScreenBrowser.exe'),
};
await fs.writeFile(path.join(testRoot, 'installer-report.json'), JSON.stringify(report, null, 2));
console.log(JSON.stringify(report, null, 2));
