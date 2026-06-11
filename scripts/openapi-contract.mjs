#!/usr/bin/env node

import { readFile, writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import openapiTS, { astToString } from 'openapi-typescript';
import { startServer } from './server-process.mjs';

const args = new Set(process.argv.slice(2));
const checkMode = args.has('--check');
const writeMode = args.has('--write') || !checkMode;

if (checkMode && writeMode && args.has('--write')) {
  console.error('Use either --check or --write.');
  process.exit(1);
}

const outputPath = path.resolve(process.cwd(), 'docs/openapi/openapi.v1.json');
const typesPath = path.resolve(process.cwd(), 'vueapp1.client/src/contracts/api.gen.ts');
const port = Number(process.env.OPENAPI_PORT ?? 5199);
const openApiUrl = `http://127.0.0.1:${port}/openapi/v1.json`;

// The generated types join the same drift gate as the JSON contract: an
// agent that changes the API surface cannot land it without regenerating
// the frontend's compile-time view of that surface.
const typesBanner = `// AUTO-GENERATED from docs/openapi/openapi.v1.json by \`npm run openapi:sync\` — do not edit.
// \`npm run openapi:check\` (part of \`npm run check\` and CI) fails when this file is stale.

`;

async function generateTypes(openApiDoc) {
  // structuredClone: the generator may annotate the document in place; the
  // JSON contract comparison above must see the pristine harvest.
  const ast = await openapiTS(structuredClone(openApiDoc));
  return `${typesBanner}${astToString(ast)}`;
}

const server = startServer({
  port,
  environment: 'Testing',
  extraEnv: {
    OpenApi__Enabled: 'true',
    OpenTelemetry__Enabled: 'false',
  },
});

async function run() {
  await server.waitFor(openApiUrl);
  const response = await fetch(openApiUrl);
  if (!response.ok) {
    throw new Error(`Unable to fetch OpenAPI document from ${openApiUrl}.`);
  }

  const openApiDoc = await response.json();
  // The servers array embeds the ephemeral localhost origin the document was
  // harvested from — meaningless in a committed contract, so strip it.
  delete openApiDoc.servers;
  // Normalize CRLF inside string values: the compiler writes XML doc files
  // with Environment.NewLine, so multi-line /// summaries flow \r\n into
  // OpenAPI descriptions on Windows and the contract would diff per-OS.
  normalizeStringNewlines(openApiDoc);
  const normalized = `${JSON.stringify(openApiDoc, null, 2)}\n`;
  const generatedTypes = await generateTypes(openApiDoc);

  if (checkMode) {
    await checkArtifact(outputPath, normalized, 'OpenAPI contract');
    await checkArtifact(typesPath, generatedTypes, 'generated API types');
  } else {
    await mkdir(path.dirname(outputPath), { recursive: true });
    await writeFile(outputPath, normalized, 'utf8');
    console.log(`OpenAPI contract written to ${outputPath}.`);
    await writeFile(typesPath, generatedTypes, 'utf8');
    console.log(`Generated API types written to ${typesPath}.`);
  }

  return process.exitCode ?? 0;
}

function normalizeStringNewlines(node) {
  if (Array.isArray(node)) {
    for (let i = 0; i < node.length; i += 1) {
      if (typeof node[i] === 'string') {
        node[i] = node[i].replaceAll('\r\n', '\n');
      } else {
        normalizeStringNewlines(node[i]);
      }
    }
  } else if (node !== null && typeof node === 'object') {
    for (const key of Object.keys(node)) {
      if (typeof node[key] === 'string') {
        node[key] = node[key].replaceAll('\r\n', '\n');
      } else {
        normalizeStringNewlines(node[key]);
      }
    }
  }
}

async function checkArtifact(filePath, expected, label) {
  let current = '';
  try {
    // Tolerate CRLF checkouts (defense in depth alongside .gitattributes):
    // the comparison is about contract content, not line endings.
    current = (await readFile(filePath, 'utf8')).replaceAll('\r\n', '\n');
  } catch (error) {
    if (error instanceof Error && 'code' in error && error.code === 'ENOENT') {
      console.error(`Baseline file for the ${label} is missing: ${filePath}`);
      console.error('Run "npm run openapi:sync" and commit the generated file.');
      process.exitCode = 1;
      return;
    }

    throw error;
  }

  if (current !== expected) {
    console.error(`Drift detected in the ${label}.`);
    console.error(`Run "npm run openapi:sync" to update ${filePath}.`);
    // Show the first differing lines (escaped, so invisible characters like
    // \r are diagnosable straight from CI output) instead of a bare verdict.
    const currentLines = current.split('\n');
    const expectedLines = expected.split('\n');
    let shown = 0;
    for (let i = 0; i < Math.max(currentLines.length, expectedLines.length) && shown < 5; i += 1) {
      if (currentLines[i] !== expectedLines[i]) {
        console.error(`  line ${i + 1}:`);
        console.error(`    committed: ${JSON.stringify(currentLines[i] ?? '<missing>')}`);
        console.error(`    generated: ${JSON.stringify(expectedLines[i] ?? '<missing>')}`);
        shown += 1;
      }
    }
    process.exitCode = 1;
  } else {
    console.log(`The ${label} ${label.endsWith('types') ? 'are' : 'is'} up to date.`);
  }
}

try {
  process.exitCode = await run();
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  const serverLogs = server.getLogs();
  if (serverLogs.length > 0) {
    console.error('\nRecent server logs:');
    console.error(serverLogs);
  }
  process.exitCode = 1;
} finally {
  await server.stop();
}
