import test from 'node:test';
import assert from 'node:assert/strict';
import { parseOrderCode, matchesOrderSearch } from '../src/order-lookup.mjs';

test('scanner slash and Russian-layout dot open the same exact order and retain its year', () => {
  const expected = { number: '10049', year: '2026', route: '/by-number/10049?year=2026' };
  assert.deepEqual(parseOrderCode('2026/10049'), expected);
  assert.deepEqual(parseOrderCode(' 2026.10049\r\n'), expected);
  assert.deepEqual(parseOrderCode('2025.10049'), { ...expected, year: '2025', route: '/by-number/10049?year=2025' });
  assert.deepEqual(parseOrderCode('10049'), { number: '10049', year: null, route: '/by-number/10049' });
});

test('live search recognizes scanned numbers without Enter and never substitutes another year', () => {
  const current = { number: '10049', period: '2026-10', customer: 'Светлана Гришина', source: 'ВК', technologies: ['ДТФ'], description: 'Наш текстиль' };
  const old = { ...current, period: '2025-10' };
  assert.equal(matchesOrderSearch(current, '2026.10049'), true);
  assert.equal(matchesOrderSearch(old, '2026.10049'), false);
  assert.equal(matchesOrderSearch({ ...current, period: undefined }, '2026/10049'), false);
  assert.equal(matchesOrderSearch(old, '2025/10049'), true);
  assert.equal(matchesOrderSearch(old, '10049'), true);
  assert.equal(matchesOrderSearch(current, 'сВеТлАнА'), true);
  assert.equal(matchesOrderSearch(current, 'ВК'), true);
  assert.equal(matchesOrderSearch(current, 'ДТФ'), true);
  assert.equal(matchesOrderSearch(current, '   '), true);
});

test('legacy QR and ID labels still work; malformed codes do not trigger an order lookup', () => {
  assert.deepEqual(parseOrderCode('https://orders.example/o/abcdefghijklmnopqrstuv'), { route: '/by-label/abcdefghijklmnopqrstuv' });
  assert.deepEqual(parseOrderCode('/o/abcdefghijklmnopqrstuv?test=1'), { route: '/by-label/abcdefghijklmnopqrstuv' });
  assert.deepEqual(parseOrderCode('ID49'), { route: '/49' });
  for (const value of ['', 'Светлана', '2026.1004', '2026.100499', '202610049', '2026.10049extra', '2026/10049?year=2025'])
    assert.equal(parseOrderCode(value), null, value);
});
