import test from 'node:test';
import assert from 'node:assert/strict';
import sharp from 'sharp';
import { renderMiniLabel, miniNameLines, orderMiniLabel } from './mini-labels.mjs';

test('mini label prints the actual ready date in Yekaterinburg and splits a two-word name', async () => {
  const label = await renderMiniLabel({ customer: '  Наталья   Давыдова ', completedAt: '2026-10-12T21:30:00Z', dueDate: '2026-11-01' });
  assert.deepEqual(label.nameLines, ['НАТАЛЬЯ', 'ДАВЫДОВА']);
  assert.equal(label.readyDate, '13.10.2026');
  assert.equal(label.widthMm, 45); assert.equal(label.heightMm, 22.5);
  const { data, info } = await sharp(label.png).greyscale().raw().toBuffer({ resolveWithObject: true });
  assert.equal(info.width, 360); assert.equal(info.height, 180);
  const ink = [];
  for (let y = 0; y < 180; y++) for (let x = 0; x < 360; x++) {
    if (data[y * 360 + x] < 128) ink.push({ x, y });
  }
  assert.ok(ink.length > 4000, 'Name must occupy a substantial part of the label');
  assert.ok(ink.every(({ x, y }) => x >= 29 && x < 331 && y >= 14 && y < 166), 'Text must remain inside safe margins');
  const headerInk = ink.filter(({ y }) => y < 41);
  assert.ok(headerInk.length > 0, 'Ready date is printed above the name');
  const dateLeft = Math.min(...headerInk.map(({ x }) => x));
  assert.ok(dateLeft >= 29 && dateLeft <= 31, 'Date aligns to the left safe margin');
  assert.ok(headerInk.every(({ y }) => y < 34), 'There is a clear gap between the date and name');
  const blank = await renderMiniLabel({ customer: '', completedAt: '2026-10-12T21:30:00Z' });
  const dateOnly = await sharp(blank.png).greyscale().raw().toBuffer();
  assert.deepEqual(data.subarray(0, 360 * 41), dateOnly.subarray(0, 360 * 41), 'Name cannot cover the date');
});

test('single, long, multi-word and markup names remain printable without losing words', async () => {
  const stamp = '2026-10-13T07:00:00Z';
  for (const customer of ['Эллина', 'Александра Александровская', 'Иван Петрович Ушенин', 'ИП «Лес & Море»', 'Клиент <тест>']) {
    const label = await renderMiniLabel({ customer, completedAt: stamp });
    assert.equal(label.nameLines.join(' '), customer.toLocaleUpperCase('ru-RU'));
    assert.equal((await sharp(label.png).metadata()).width, 360);
  }
  assert.deepEqual(miniNameLines('Эллина'), ['ЭЛЛИНА']);
  const blank = await renderMiniLabel({ customer: '', completedAt: stamp });
  assert.deepEqual(blank.nameLines, []); assert.equal(blank.readyDate, '13.10.2026');
});

test('work orders print planned dates or just a name without inventing a readiness date', async () => {
  const planned = await orderMiniLabel({ customer: 'Иван Петров', state: 'new', dueDate: '2026-10-20' });
  assert.equal(planned.labelDate, '20.10.2026'); assert.equal(planned.readyDate, null); assert.equal(planned.dateKind, 'due');
  const undated = await renderMiniLabel({ customer: 'Иван Петров', state: 'new' });
  assert.equal(undated.labelDate, ''); assert.equal(undated.readyDate, null); assert.equal(undated.dateKind, null);
  const nameOnly = await sharp(undated.png).greyscale().raw().toBuffer();
  assert.ok(nameOnly.some(value => value < 128));
  const empty = await renderMiniLabel({ state: 'new' });
  assert.ok((await sharp(empty.png).greyscale().raw().toBuffer()).every(value => value === 255));
  await assert.rejects(renderMiniLabel({ customer: 'Тест', completedAt: 'invalid' }), /Некорректная/);
  const label = await orderMiniLabel({ customer: 'Тест', state: 'done', completedAt: '2026-10-13T07:00:00Z', dueDate: '2026-10-20' });
  assert.equal(label.labelDate, '13.10.2026'); assert.equal(label.dateKind, 'ready');
  assert.equal(label.format, 'mini'); assert.equal(label.png, undefined);
  assert.match(label.svg, /width="45mm" height="22.5mm"/);
});
