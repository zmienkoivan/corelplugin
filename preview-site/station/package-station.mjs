import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';

if (process.platform !== 'win32' || process.arch !== 'x64') throw new Error('Build this Windows x64 package on Windows x64.');
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const output = path.join(root, 'data', 'station-package', new Date().toISOString().replace(/[:.]/g, '-'));
await fs.mkdir(output, { recursive: true });
const files = new Map();
async function copy(source, relative, bom = false) {
  const target = path.join(output, relative);
  await fs.mkdir(path.dirname(target), { recursive: true });
  if (/[^\x20-\x7e]/.test(relative)) throw new Error('Use ASCII file names in the Windows ZIP.');
  let bytes = await fs.readFile(source);
  if (relative.endsWith('.cmd')) {
    if (bytes.some(byte => byte > 127)) throw new Error('CMD launcher must contain only ASCII.');
    bytes = Buffer.from(bytes.toString('ascii').replace(/\r?\n/g, '\r\n'), 'ascii');
  }
  await fs.writeFile(target, bom ? Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), bytes]) : bytes);
  files.set(relative.replaceAll('\\', '/'), createHash('sha256').update(await fs.readFile(target)).digest('hex'));
}
async function tree(source, relative) {
  for (const item of await fs.readdir(source, { withFileTypes: true })) {
    if (item.isDirectory()) await tree(path.join(source, item.name), path.join(relative, item.name));
    else if (item.isFile()) await copy(path.join(source, item.name), path.join(relative, item.name));
    else throw new Error('Unexpected symbolic link in package.');
  }
}
await copy(process.execPath, 'app/node.exe');
const licensePath = path.join(root, 'data', `station-node-license-${process.version}.txt`);
try { await fs.access(licensePath); } catch {
  const reply = await fetch(`https://raw.githubusercontent.com/nodejs/node/${process.version}/LICENSE`,
    { signal: AbortSignal.timeout(30_000), redirect: 'error' });
  if (!reply.ok) throw new Error('Node license download failed.');
  const license = await reply.text();
  if (!license.startsWith('Node.js is licensed') || license.length < 10_000) throw new Error('Unexpected Node license.');
  await fs.writeFile(licensePath, license);
}
await copy(licensePath, 'app/NODE-LICENSE.txt');
for (const name of ['print-station.mjs', 'remote-orders.mjs', 'local-printing.mjs', 'network-print-agent.mjs', 'printing-io.mjs', 'label-photo-index.mjs', 'windows-print.ps1'])
  await copy(path.join(root, 'server', name), 'app/server/' + name, name.endsWith('.ps1'));
await copy(path.join(root, 'station', 'windows-print-helper.cs'), 'app/server/windows-print-helper.cs');
const compiler = path.join(process.env.WINDIR || 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');
const nativeHelper = path.join(output, 'app', 'server', 'windows-print-helper.exe');
execFileSync(compiler, ['/nologo','/optimize+','/target:exe','/platform:x64','/reference:System.Drawing.dll',
  '/reference:System.Web.Extensions.dll','/out:' + nativeHelper,path.join(output, 'app', 'server', 'windows-print-helper.cs')],
  { windowsHide:true, stdio:'pipe', timeout:30_000 });
files.set('app/server/windows-print-helper.exe', createHash('sha256').update(await fs.readFile(nativeHelper)).digest('hex'));
for (const name of ['sharp', '@img/sharp-win32-x64', '@img/colour', 'detect-libc', 'semver'])
  await tree(path.join(root, 'node_modules', name), 'app/node_modules/' + name);
for (const name of ['station.ps1', 'shortcuts.ps1', 'Install.cmd', 'Update.cmd', 'Open.cmd', 'Diagnose-Printers.cmd', 'Diagnose-Printers.ps1', 'README.txt'])
  await copy(path.join(root, 'station', name), name, name.endsWith('.ps1'));
await fs.writeFile(path.join(output, 'files.json'), JSON.stringify(Object.fromEntries(files), null, 2));
execFileSync(path.join(output, 'app', 'node.exe'), ['--input-type=module', '-e',
  "import sharp from 'sharp'; const b=await sharp({create:{width:2,height:2,channels:3,background:'#fff'}}).png().toBuffer(); if(b.length<50)process.exit(1);"],
  { cwd: path.join(output, 'app'), stdio: 'pipe' });
const zip = path.join(path.dirname(output), 'order-station-windows.zip');
execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
  "$stationFiles = Get-ChildItem -LiteralPath $env:STATION_PACKAGE_DIR; Compress-Archive -LiteralPath $stationFiles.FullName -DestinationPath $env:STATION_PACKAGE_ZIP -Force"],
  { env: { ...process.env, STATION_PACKAGE_DIR: output, STATION_PACKAGE_ZIP: zip }, stdio: 'pipe', timeout: 120_000 });
console.log(JSON.stringify({ folder: output, zip, files: files.size, bytes: (await fs.stat(zip)).size,
  sha256: createHash('sha256').update(await fs.readFile(zip)).digest('hex') }));
