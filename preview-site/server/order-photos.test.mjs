import test from 'node:test';
import assert from 'node:assert/strict';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import sharp from 'sharp';
import { createOrderPhotos, photoCount } from './order-photos.mjs';

test('Telegram thumbnails preserve aspect ratio, share one download, and survive a server restart', async t => {
  const directory = await fsp.mkdtemp(path.join(os.tmpdir(), 'order-photos-'));
  t.after(() => fsp.rm(directory, { recursive: true, force: true }));
  const image = await sharp({ create: { width: 1280, height: 800, channels: 3, background: '#d54' } }).jpeg().toBuffer();
  const row = { id: 4, rich_photo_files: '["file-1","file-2"]' };
  const requests = [];
  const fetchImpl = async (url, options) => {
    requests.push(url);
    assert.equal(options.redirect, 'error');
    return url.endsWith('/getFile')
      ? Response.json({ ok: true, result: { file_path: 'photos/file_1.jpg', file_size: image.length } })
      : new Response(image);
  };
  const store = createOrderPhotos({ dataRoot: directory, token: 'private-token', fetchImpl });
  assert.equal(photoCount(row), 2);
  const [thumbnail, repeated, original] = await Promise.all([
    store.read(row, 0, true), store.read(row, 0, true), store.read(row, 0)]);
  assert.deepEqual(thumbnail, repeated);
  const small = await sharp(thumbnail).metadata(), full = await sharp(original).metadata();
  assert.equal(small.width, 320); assert.equal(small.height, 200);
  assert.equal(full.width, 1280); assert.equal(full.height, 800);
  assert.equal(requests.length, 2);
  assert.equal(await store.read(row, -1), null); assert.equal(await store.read(row, 2), null);
  assert.equal(await store.read(row, 0.5), null);
  const restarted = createOrderPhotos({ dataRoot: directory, token: 'private-token', fetchImpl: () => { throw new Error('No network needed'); } });
  assert.deepEqual(await restarted.read(row, 0, true), thumbnail);
  await store.read(row, 1, true);
  assert.equal(requests.length, 4);
  assert.equal((await fsp.readdir(path.join(directory, 'order-photo-cache'))).some(name => name.endsWith('.tmp')), false);
});

test('local photos take precedence and Telegram errors never expose a bot token', async t => {
  const directory = await fsp.mkdtemp(path.join(os.tmpdir(), 'order-local-photos-'));
  t.after(() => fsp.rm(directory, { recursive: true, force: true }));
  await fsp.mkdir(path.join(directory, 'order-photos', '1'), { recursive: true });
  const image = await sharp({ create: { width: 600, height: 600, channels: 3, background: 'white' } }).jpeg().toBuffer();
  await fsp.writeFile(path.join(directory, 'order-photos', '1', 'photo.jpg'), image);
  const store = createOrderPhotos({ dataRoot: directory, token: 'private-token', fetchImpl: async () => { throw new Error('URL/private-token'); } });
  const row = { id: 1, local_photo_files: '["photo.jpg"]', rich_photo_files: '["old-file"]' };
  assert.deepEqual(await store.read(row, 0), image);
  assert.equal(photoCount(row), 1);
  assert.equal((await sharp(await store.read(row, 0, true)).metadata()).height, 240);
  assert.equal(await store.read({ ...row, local_photo_files: '["../secret"]' }, 0), null);
  await assert.rejects(() => store.read({ id: 2, rich_photo_files: '["remote-file"]' }, 0), error =>
    error.message.includes('Telegram') && !error.message.includes('private-token'));
});

test('Telegram download rejects oversized files and unsafe paths', async t => {
  const directory = await fsp.mkdtemp(path.join(os.tmpdir(), 'order-photo-limits-'));
  t.after(() => fsp.rm(directory, { recursive: true, force: true }));
  for (const result of [{ file_path: 'photos/../../secret' },
    { file_path: 'photos/file.jpg', file_size: 21 * 1024 * 1024 }]) {
    let calls = 0;
    const store = createOrderPhotos({ dataRoot: directory, token: 'token', fetchImpl: async () => {
      calls++; return Response.json({ ok: true, result });
    } });
    await assert.rejects(() => store.read({ id: 1, rich_photo_files: '["file"]' }, 0));
    assert.equal(calls, 1);
  }
});
