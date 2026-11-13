import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const source = path.resolve(process.argv[2] || '');
const driver = path.resolve(process.argv[3] || '');
if (!source.startsWith(path.join(root, 'data', 'station-package') + path.sep) ||
    path.basename(driver) !== 'zbrn.inf_amd64_92b721070e611355') throw Error('Unexpected package inputs.');
const output = path.join(root, 'data', 'zebra-package', new Date().toISOString().replace(/[:.]/g, '-'));
await fs.mkdir(output, { recursive: true });
const files = new Map();
async function copy(file, relative, bom = false) {
  const target = path.join(output, relative);
  await fs.mkdir(path.dirname(target), { recursive: true });
  let bytes = await fs.readFile(file);
  if (bom) bytes = Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), bytes]);
  await fs.writeFile(target, bytes);
  files.set(relative.replaceAll('\\', '/'), createHash('sha256').update(bytes).digest('hex'));
}
async function tree(folder, relative) {
  for (const item of await fs.readdir(folder, { withFileTypes: true })) {
    const target = path.join(relative, item.name);
    if (item.isDirectory()) await tree(path.join(folder, item.name), target);
    else if (item.isFile()) await copy(path.join(folder, item.name), target);
    else throw Error('Unexpected symbolic link.');
  }
}
const manifest = JSON.parse(await fs.readFile(path.join(source, 'files.json'), 'utf8'));
for (const [name, hash] of Object.entries(manifest)) {
  const file = path.resolve(source, name);
  if (!file.startsWith(source + path.sep) || createHash('sha256').update(await fs.readFile(file)).digest('hex') !== hash) throw Error('Invalid base package.');
  await copy(file, name);
}
for (const name of ['Add-Zebra.cmd', 'Add-Zebra.ps1', 'Setup-Zebra-Printer.ps1'])
  await copy(path.join(root, 'station', 'zebra', name), name, name.endsWith('.ps1'));
await tree(driver, 'zebra-driver');
await fs.writeFile(path.join(output, 'files.json'), JSON.stringify(Object.fromEntries(files), null, 2));
const zip = path.join(path.dirname(output), 'zebra-gx420d-windows.zip');
execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
  '$items=Get-ChildItem -LiteralPath $env:ZEBRA_PACKAGE_DIR; Compress-Archive -LiteralPath $items.FullName -DestinationPath $env:ZEBRA_PACKAGE_ZIP -Force'],
  { env: { ...process.env, ZEBRA_PACKAGE_DIR: output, ZEBRA_PACKAGE_ZIP: zip }, stdio: 'pipe', timeout: 120_000 });
console.log(JSON.stringify({ folder: output, zip, files: files.size, bytes: (await fs.stat(zip)).size,
  sha256: createHash('sha256').update(await fs.readFile(zip)).digest('hex') }));
