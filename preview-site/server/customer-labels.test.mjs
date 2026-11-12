import test from 'node:test';
import assert from 'node:assert/strict';
import sharp from 'sharp';
import { BinaryBitmap, HybridBinarizer, RGBLuminanceSource, MultiFormatReader, DecodeHintType, BarcodeFormat } from '@zxing/library';
import { customerLabel } from './customer-labels.mjs';
test('customer label has a large uppercase name, dates, contained photo, short sharp Code128 and no production notes', async () => {
  const photo = await sharp({ create: { width: 400, height: 200, channels: 3, background: '#90c8f0' } }).jpeg().toBuffer();
  const order = { id: 37, number: '10037', period: '2026-10', labelToken: 'abcdefghijklmnopqrstuv', customer: 'Светлана Гришина',
    createdAt: '2026-10-21T07:00:00Z', dueDate: '2026-10-25', description: 'Секретное производственное описание', technologies: ['ДТФ'] };
  for (const heightMm of [110, 120]) {
    const label = await customerLabel(order, 'https://example.test', { heightMm, photo });
    assert.equal(label.variant, 'customer'); assert.equal(label.heightMm, heightMm); assert.equal(label.widthMm, 80);
    assert.ok(label.svg.includes('СВЕТЛАНА')); assert.ok(label.svg.includes('ГРИШИНА')); assert.ok(label.svg.includes('<image'));
    assert.ok(label.svg.includes('25.10.2026')); assert.ok(!label.svg.includes(order.description)); assert.ok(!label.svg.includes('ДТФ'));
    assert.equal(label.barcodeLayout.heightMm, 10);
    const png = await sharp(Buffer.from(label.svg), { density: 203.2 }).png().toBuffer();
    const { data, info } = await sharp(png).flatten({ background: '#fff' }).greyscale().raw().toBuffer({ resolveWithObject: true });
    const reader = new MultiFormatReader(); reader.setHints(new Map([[DecodeHintType.POSSIBLE_FORMATS, [BarcodeFormat.CODE_128]]]));
    assert.equal(reader.decode(new BinaryBitmap(new HybridBinarizer(new RGBLuminanceSource(new Uint8ClampedArray(data), info.width, info.height)))).getText(), '2026/10037');
  }
  const empty = await customerLabel({ ...order, customer: '', dueDate: null }, 'https://example.test'); assert.ok(!empty.svg.includes('<image'));
});
