import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../../src/PicHarbor.Gui/WebAssets/gallery/thumbnail-loader.js', import.meta.url), 'utf8');
const { ThumbnailLoader } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const tick = () => new Promise(resolve => setImmediate(resolve));
const success = { ok: true, blob: async () => ({}) };
function tile() {
  const classes = new Set();
  return {
    img: { src: '', removeAttribute() { this.src = ''; } },
    root: { classList: { add: name => classes.add(name), remove: (...names) => names.forEach(name => classes.delete(name)) } },
    classes
  };
}
function setup(concurrency = 2) {
  const requests = [], revoked = [];
  let sequence = 0;
  const loader = new ThumbnailLoader({ concurrency, delay: 1000,
    fetchImage: (source, options) => new Promise((resolve, reject) => requests.push({ source, ...options, resolve, reject })),
    createUrl: () => `blob:${++sequence}`, revokeUrl: url => revoked.push(url) });
  return { loader, requests, revoked };
}

test('visible requests run before overscan, with bounded concurrency', async () => {
  const { loader, requests } = setup();
  try {
    loader.request(tile(), 'overscan', 900);
    loader.request(tile(), 'visible-one', 0);
    loader.request(tile(), 'visible-two', 10);
    loader.pump();
    assert.deepEqual(requests.map(r => r.source), ['visible-one', 'visible-two']);
    assert.equal(loader.active, 2);
    requests[0].resolve(success);
    await tick();
    loader.pump();
    assert.equal(requests[2].source, 'overscan');
    assert.equal(loader.active, 2);
  } finally { loader.destroy(); }
});

test('a recycled tile cannot receive stale content even if fetch ignores abort', async () => {
  const { loader, requests } = setup();
  const target = tile();
  try {
    loader.request(target, 'old');
    loader.pump();
    loader.request(target, 'new');
    assert.equal(requests[0].signal.aborted, true);
    loader.pump();
    requests[1].resolve(success);
    await tick();
    assert.equal(target.img.src, 'blob:1');
    requests[0].resolve(success);
    await tick();
    assert.equal(target.img.src, 'blob:1');
  } finally { loader.destroy(); }
});

test('scrolling discards pending requests before they consume a worker', () => {
  const { loader, requests } = setup();
  try {
    const obsolete = tile();
    loader.request(obsolete, 'old');
    loader.release(obsolete);
    loader.request(tile(), 'current');
    loader.pump();
    assert.deepEqual(requests.map(r => r.source), ['current']);
  } finally { loader.destroy(); }
});

test('releasing and destroying tiles frees blobs and aborts unfinished requests', async () => {
  const { loader, requests, revoked } = setup();
  const loaded = tile();
  loader.request(loaded, 'loaded');
  loader.request(tile(), 'pending');
  loader.pump();
  requests[0].resolve(success);
  await tick();
  loader.destroy();
  assert.equal(loaded.img.src, '');
  assert.deepEqual(revoked, ['blob:1']);
  assert.equal(requests[1].signal.aborted, true);
  assert.equal(loader.jobs.size, 0);
});

test('a failed thumbnail releases its worker for another visible image', async () => {
  const { loader, requests } = setup(1);
  const bad = tile();
  try {
    loader.request(bad, 'bad');
    loader.request(tile(), 'next');
    loader.pump();
    requests[0].resolve({ ok: false, status: 403 });
    await tick();
    assert.equal(bad.classes.has('is-error'), true);
    loader.pump();
    assert.equal(requests[1].source, 'next');
  } finally { loader.destroy(); }
});

test('rapid timeline changes only fetch the final viewport after settling', async () => {
  const requests = [];
  const loader = new ThumbnailLoader({ delay: 10, fetchImage: async source => { requests.push(source); return success; },
    createUrl: () => 'blob:current', revokeUrl: () => {} });
  const target = tile();
  try {
    for (let i = 0; i < 100; i++) loader.request(target, `position-${i}`);
    await new Promise(resolve => setTimeout(resolve, 80));
    assert.deepEqual(requests, ['position-99']);
  } finally { loader.destroy(); }
});
