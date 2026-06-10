#!/usr/bin/env node
/**
 * Renames the template project (VueApp1) to your project name — file contents,
 * file names, and directory names, including all case variants.
 *
 *   node scripts/rename.mjs MyApp          # dry run: prints what would change
 *   node scripts/rename.mjs MyApp --apply  # performs the rename
 *   node scripts/rename.mjs MyApp --apply --repo owner/repo
 *                                          # also rewrites the template's GitHub
 *                                          # slug (badges, links) to yours
 *
 * Zero dependencies; safe to run offline. After applying, this script no
 * longer matches anything — delete it (the template-cleanup workflow does so
 * automatically for repos created via "Use this template").
 */
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';

const SOURCE_PASCAL = 'VueApp1';
const SOURCE_LOWER = 'vueapp1';
const SOURCE_UPPER = 'VUEAPP1';
const TEMPLATE_SLUG = 'monotio/spa-vue-template';

const IGNORED_DIRS = new Set([
  '.git',
  'node_modules',
  'bin',
  'obj',
  'dist',
  'coverage',
  'test-results',
  'TestResults',
  '.vs',
  '.idea',
]);
const BINARY_EXTENSIONS = new Set([
  '.png',
  '.ico',
  '.jpg',
  '.jpeg',
  '.gif',
  '.webp',
  '.woff',
  '.woff2',
  '.ttf',
  '.eot',
  '.zip',
  '.dll',
  '.exe',
  '.pdb',
]);

const args = process.argv.slice(2);
const apply = args.includes('--apply');
const repoFlagIndex = args.indexOf('--repo');
const newSlug = repoFlagIndex !== -1 ? args[repoFlagIndex + 1] : null;
const rawName = args.find((a) => !a.startsWith('--') && a !== newSlug);

if (!rawName) {
  console.error('Usage: node scripts/rename.mjs <NewProjectName> [--apply] [--repo owner/repo]');
  process.exit(1);
}

// Sanitize to a valid .NET/npm-friendly PascalCase identifier:
// "my cool-app" -> "MyCoolApp"; a leading digit gets an "App" prefix.
const pascal = rawName
  .split(/[^A-Za-z0-9]+/)
  .filter(Boolean)
  .map((part) => part[0].toUpperCase() + part.slice(1))
  .join('');
const target = /^[0-9]/.test(pascal) ? `App${pascal}` : pascal;
if (!target) {
  console.error(`Could not derive a valid project name from "${rawName}".`);
  process.exit(1);
}
const targetLower = target.toLowerCase();
const targetUpper = target.toUpperCase();

const repoRoot = path.resolve(import.meta.dirname, '..');
const selfPath = path.resolve(import.meta.dirname, 'rename.mjs');

function replaceAll(text) {
  let result = text
    .replaceAll(SOURCE_PASCAL, target)
    .replaceAll(SOURCE_LOWER, targetLower)
    .replaceAll(SOURCE_UPPER, targetUpper);
  if (newSlug) {
    result = result.replaceAll(TEMPLATE_SLUG, newSlug);
  }
  return result;
}

const contentChanges = [];
const pathRenames = [];

function walk(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (IGNORED_DIRS.has(entry.name)) continue;
      walk(fullPath);
    } else if (entry.isFile()) {
      if (fullPath === selfPath) continue; // the search terms above must survive
      if (!BINARY_EXTENSIONS.has(path.extname(entry.name).toLowerCase())) {
        const text = fs.readFileSync(fullPath, 'utf8');
        const replaced = replaceAll(text);
        if (replaced !== text) contentChanges.push({ fullPath, replaced });
      }
    }
    const newName = replaceAll(entry.name);
    if (newName !== entry.name) {
      pathRenames.push({ from: fullPath, to: path.join(dir, newName) });
    }
  }
}

walk(repoRoot);
// Rename deepest paths first so parent renames don't invalidate child paths.
pathRenames.sort((a, b) => b.from.length - a.from.length);

console.log(`Renaming ${SOURCE_PASCAL} -> ${target} (${SOURCE_LOWER} -> ${targetLower})`);
if (newSlug) console.log(`Rewriting repo slug ${TEMPLATE_SLUG} -> ${newSlug}`);
console.log(`\n${contentChanges.length} files with content changes:`);
for (const { fullPath } of contentChanges) {
  console.log(`  ${path.relative(repoRoot, fullPath)}`);
}
console.log(`\n${pathRenames.length} file/directory renames:`);
for (const { from, to } of pathRenames) {
  console.log(`  ${path.relative(repoRoot, from)} -> ${path.relative(repoRoot, to)}`);
}

if (!apply) {
  console.log('\nDry run — nothing written. Re-run with --apply to perform the rename.');
  process.exit(0);
}

for (const { fullPath, replaced } of contentChanges) {
  fs.writeFileSync(fullPath, replaced);
}
for (const { from, to } of pathRenames) {
  fs.renameSync(from, to);
}

console.log('\nDone. Suggested follow-ups:');
console.log('  1. Delete scripts/rename.mjs (it has served its purpose).');
console.log('  2. Regenerate PWA icons from your own logo: npm run generate-pwa-assets');
console.log(`  3. Run: npm ci --prefix ${targetLower}.client && npm run check`);
