// Bound thumbnail work to the current virtual viewport. A recycled tile can never
// receive the result of its previous request, even when an abort races completion.
export class ThumbnailLoader {
  constructor({ concurrency = 4, delay = 60, fetchImage = (...args) => fetch(...args),
    createUrl = blob => URL.createObjectURL(blob), revokeUrl = url => URL.revokeObjectURL(url) } = {}) {
    this.concurrency = concurrency;
    this.delay = delay;
    this.fetchImage = fetchImage;
    this.createUrl = createUrl;
    this.revokeUrl = revokeUrl;
    this.jobs = new Map();
    this.active = 0;
    this.timer = null;
    this.destroyed = false;
  }
  request(tile, source, priority = 0) {
    this.release(tile);
    const job = { tile, source, priority, controller: new AbortController(), started: false, url: null };
    this.jobs.set(tile, job);
    this.schedule();
  }
  prioritize(tile, priority) {
    const job = this.jobs.get(tile);
    if (job) job.priority = priority;
  }
  release(tile) {
    const job = this.jobs.get(tile);
    if (!job) return;
    this.jobs.delete(tile);
    job.controller.abort();
    tile.img.removeAttribute('src');
    tile.root.classList.remove('is-loaded', 'is-error');
    if (job.url) this.revokeUrl(job.url);
  }
  schedule() {
    if (this.destroyed) return;
    clearTimeout(this.timer);
    this.timer = setTimeout(() => { this.timer = null; this.pump(); }, this.delay);
  }
  pump() {
    const pending = [...this.jobs.values()].filter(job => !job.started).sort((a, b) => a.priority - b.priority);
    for (const job of pending) {
      if (this.active >= this.concurrency || this.destroyed) break;
      job.started = true;
      this.active++;
      this.load(job);
    }
  }
  async load(job) {
    try {
      const response = await this.fetchImage(job.source, { signal: job.controller.signal });
      if (!response.ok) throw new Error(`Thumbnail HTTP ${response.status}`);
      const blob = await response.blob();
      if (this.jobs.get(job.tile) !== job || job.controller.signal.aborted) return;
      job.url = this.createUrl(blob);
      job.tile.img.src = job.url;
    } catch (error) {
      if (this.jobs.get(job.tile) === job && !job.controller.signal.aborted)
        job.tile.root.classList.add('is-error');
    } finally {
      this.active--;
      if (!this.destroyed) this.schedule();
    }
  }
  destroy() {
    this.destroyed = true;
    clearTimeout(this.timer);
    for (const tile of [...this.jobs.keys()]) this.release(tile);
  }
}
