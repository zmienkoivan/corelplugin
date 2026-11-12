import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import sharp from 'sharp';
import { BinaryBitmap, HybridBinarizer, RGBLuminanceSource, MultiFormatReader, DecodeHintType, BarcodeFormat } from '@zxing/library';
import { orderLabel } from './labels.mjs';

const card = { id: 4, number: '10004', period: '2026-10', labelToken: 'abcdefghijklmnopqrstuv',
  createdAt: '2026-10-08T06:00:00Z', customer: 'Наталья Давыдова', items: '125 шт.',
  technologies: ['Вышивка'], description: 'Худи заказчика', sendState: 'sent', dueDate: '2026-10-25' };

async function decode(image) {
  const { data, info } = await sharp(image).flatten({ background: '#fff' }).greyscale().raw().toBuffer({ resolveWithObject: true });
  const reader = new MultiFormatReader();
  reader.setHints(new Map([[DecodeHintType.POSSIBLE_FORMATS, [BarcodeFormat.CODE_128]]]));
  return reader.decode(new BinaryBitmap(new HybridBinarizer(new RGBLuminanceSource(new Uint8ClampedArray(data), info.width, info.height)))).getText();
}

test('full label is 80×120, has no QR and the enlarged Code 128 decodes at 203 dpi', async () => {
  const label = await orderLabel(card, 'https://orders.example');
  assert.equal(label.widthMm, 80); assert.equal(label.heightMm, 120);
  assert.ok(!label.svg.includes('<image')); assert.ok(!label.svg.includes('https://orders.example'));
  assert.equal(label.qrUrl, 'https://orders.example/o/' + card.labelToken);
  assert.equal(label.barcodeLayout.quietModules, 10);
  assert.ok(label.barcodeLayout.heightMm > 40);
  const png = await sharp(Buffer.from(label.svg), { density: 203.2 }).png().toBuffer();
  assert.equal(await decode(png), '2026/10004');
  assert.equal((await orderLabel(card, 'https://orders.example', { heightMm: 110 })).heightMm, 110);
  await assert.rejects(orderLabel(card, 'https://orders.example', { heightMm: 90 }), /высота/);
});

test('Windows draws binary integer-width bars at native 203, 300 and 600 dpi without spooling', { skip: process.platform !== 'win32' }, async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'label-raster-'));
  t.after(async () => { assert.ok(path.resolve(root).startsWith(path.resolve(os.tmpdir()) + path.sep)); await fs.rm(root, { recursive: true, force: true }); });
  const label = await orderLabel(card, 'https://orders.example');
  const imagePath = path.join(root, 'background.png');
  await sharp(Buffer.from(label.svg), { density: 300 }).png().toFile(imagePath);
  for (const dpi of [203.2, 300, 600]) {
    const previewPath = path.join(root, `native-${dpi}.png`), payloadPath = path.join(root, 'payload.json');
    await fs.writeFile(payloadPath, JSON.stringify({ paperMode: 'label', labelWidthMm: 80, labelHeightMm: 120, imagePath, barcodeLayout: label.barcodeLayout, previewDpi: dpi, previewPath }));
    const { stdout } = await promisify(execFile)('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', path.resolve('server/windows-print.ps1'), '-Action', 'raster-preview', '-PayloadPath', payloadPath]);
    assert.equal(JSON.parse(stdout).ok, true);
    assert.equal(await decode(previewPath), '2026/10004');
    const { data, info } = await sharp(previewPath).removeAlpha().raw().toBuffer({ resolveWithObject: true });
    assert.equal(info.width, Math.round(80 / 25.4 * dpi)); assert.equal(info.height, Math.round(120 / 25.4 * dpi));
    const layout = label.barcodeLayout;
    const x1 = Math.round(layout.xMm / 25.4 * dpi), x2 = Math.round((layout.xMm + layout.widthMm) / 25.4 * dpi);
    const y1 = Math.round(layout.yMm / 25.4 * dpi), y2 = Math.round((layout.yMm + layout.heightMm) / 25.4 * dpi);
    for (let y = y1; y < y2; y++) for (let x = x1; x < x2; x++) {
      const at = (y * info.width + x) * info.channels;
      assert.ok(data[at] === 0 || data[at] === 255, `Grey edge at ${dpi} dpi: ${x},${y}`);
      assert.equal(data[at], data[at + 1]); assert.equal(data[at], data[at + 2]);
    }
    const widths = [], y = Math.round((layout.yMm + layout.heightMm / 2) / 25.4 * dpi);
    let previous = 255, length = 0;
    for (let x = x1; x < x2; x++) {
      const value = data[(y * info.width + x) * info.channels];
      if (value !== previous) { widths.push(length); length = 0; previous = value; } length++;
    }
    const runs = widths.slice(1); // Exclude the left quiet zone; right quiet zone has not been pushed.
    const moduleDots = Math.floor(Math.round(72 / 25.4 * dpi) / (label.barcodeLayout.widths.reduce((a, b) => a + b, 0) + 20));
    assert.deepEqual(runs, label.barcodeLayout.widths.map(width => width * moduleDots));
  }
});
