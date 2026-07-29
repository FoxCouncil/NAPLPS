// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSSite;

/// <summary>
/// The frame-accurate APNG player served with the site.
///
/// Browsers animate APNG but expose no control over it - no seek, no pause, no rate. So the file is
/// decoded in JavaScript instead. The trick that keeps this small: rather than inflating pixel data
/// by hand, each frame's compressed data is re-wrapped as a standalone single-frame PNG and handed
/// to the browser's own decoder via createImageBitmap. No zlib implementation, no filter
/// reconstruction - just chunk surgery and a CRC.
///
/// Frames are stored as dirty rectangles (see ApngWriter), so the decoded bitmaps are small and
/// compositing forward is cheap. Seeking backwards replays from the nearest snapshot of the fully
/// composited canvas; a bounded number of those are kept so a drag on a multi-thousand-frame file
/// does not have to replay the whole animation to move one frame.
///
/// Seeks are serialised. A drag emits input events far faster than an async seek completes, and
/// letting them interleave corrupts both the canvas and the frame index.
/// </summary>
public static class Player
{
    public const string FileName = "player.js";

    public static string Script => """
        (function () {
          'use strict';

          console.log("Yip! I'm a foxie!");

          const CRC = (function () {
            const t = new Uint32Array(256);
            for (let n = 0; n < 256; n++) {
              let c = n;
              for (let k = 0; k < 8; k++) { c = (c & 1) ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1); }
              t[n] = c >>> 0;
            }
            return t;
          })();

          function crc32(bytes) {
            let c = 0xFFFFFFFF;
            for (let i = 0; i < bytes.length; i++) { c = CRC[(c ^ bytes[i]) & 0xFF] ^ (c >>> 8); }
            return (c ^ 0xFFFFFFFF) >>> 0;
          }

          function be32(v) {
            return new Uint8Array([(v >>> 24) & 255, (v >>> 16) & 255, (v >>> 8) & 255, v & 255]);
          }

          function chunk(tag, data) {
            const t = new Uint8Array([tag.charCodeAt(0), tag.charCodeAt(1), tag.charCodeAt(2), tag.charCodeAt(3)]);
            const body = new Uint8Array(4 + data.length);
            body.set(t, 0);
            body.set(data, 4);
            const out = new Uint8Array(8 + data.length + 4);
            out.set(be32(data.length), 0);
            out.set(body, 4);
            out.set(be32(crc32(body)), 8 + data.length);
            return out;
          }

          // Walk the APNG chunk stream, collecting each frame's control block and data.
          function parse(buffer) {
            const d = new Uint8Array(buffer);
            const dv = new DataView(buffer);
            let p = 8, ihdr = null, width = 0, height = 0;
            const extra = [], frames = [];
            let cur = null;

            while (p + 8 <= d.length) {
              const len = dv.getUint32(p);
              const tag = String.fromCharCode(d[p + 4], d[p + 5], d[p + 6], d[p + 7]);
              const data = d.subarray(p + 8, p + 8 + len);

              if (tag === 'IHDR') {
                ihdr = data.slice();
                width = dv.getUint32(p + 8);
                height = dv.getUint32(p + 12);
              } else if (tag === 'PLTE' || tag === 'tRNS' || tag === 'gAMA' || tag === 'sRGB') {
                extra.push({ tag: tag, data: data.slice() });
              } else if (tag === 'fcTL') {
                const v = new DataView(data.buffer, data.byteOffset, data.byteLength);
                cur = {
                  w: v.getUint32(4), h: v.getUint32(8), x: v.getUint32(12), y: v.getUint32(16),
                  dn: v.getUint16(20), dd: v.getUint16(22) || 100,
                  dispose: data[24], blend: data[25], parts: [],
                };
                frames.push(cur);
              } else if (tag === 'IDAT') {
                if (cur) { cur.parts.push(data.slice()); }
              } else if (tag === 'fdAT') {
                if (cur) { cur.parts.push(data.slice(4)); }   // first 4 bytes are the sequence number
              } else if (tag === 'IEND') {
                break;
              }

              p += 12 + len;
            }

            return { width: width, height: height, ihdr: ihdr, extra: extra, frames: frames };
          }

          // Re-wrap one frame as a complete single-frame PNG the browser can decode natively.
          function framePng(a, f) {
            const ihdr = a.ihdr.slice();
            const hv = new DataView(ihdr.buffer, ihdr.byteOffset, ihdr.byteLength);
            hv.setUint32(0, f.w);
            hv.setUint32(4, f.h);

            const parts = [new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr)];
            for (const e of a.extra) { parts.push(chunk(e.tag, e.data)); }

            let n = 0;
            for (const q of f.parts) { n += q.length; }
            const idat = new Uint8Array(n);
            let o = 0;
            for (const q of f.parts) { idat.set(q, o); o += q.length; }

            parts.push(chunk('IDAT', idat));
            parts.push(chunk('IEND', new Uint8Array(0)));

            let size = 0;
            for (const q of parts) { size += q.length; }
            const out = new Uint8Array(size);
            let off = 0;
            for (const q of parts) { out.set(q, off); off += q.length; }

            return out;
          }

          class ApngPlayer {
            constructor(root) {
              this.root = root;
              this.src = root.dataset.src;
              this.canvas = root.querySelector('canvas');
              this.ctx = this.canvas.getContext('2d', { willReadFrequently: false });
              this.bitmaps = [];
              this.index = -1;
              this.playing = false;
              // Files carry authentic 1200-baud timing, which on the smaller pieces is over
              // before you have focused on it. Start at a quarter speed and let 1x mean real.
              this.rate = 0.25;
              this.loop = false;   // deliberately off: a looping animation is hard to read
              this.timer = null;

              // Seeks are async and a drag fires input events far faster than they complete, so
              // they have to be serialised: a newer seek supersedes whatever is still running.
              this.seekToken = 0;
              this.seeking = null;
              this.dragging = false;

              // Snapshots of the fully composited canvas at intervals. Without them a backward
              // seek has to replay from frame 0, which on a 2400-frame file means thousands of
              // awaited decodes before the first drag step lands.
              this.keys = new Map();
            }

            async load() {
              const res = await fetch(this.src);
              if (!res.ok) { throw new Error('fetch failed: ' + res.status); }

              this.apng = parse(await res.arrayBuffer());
              if (!this.apng.frames.length) { throw new Error('no frames'); }

              this.canvas.width = this.apng.width;
              this.canvas.height = this.apng.height;
              this.ctx.imageSmoothingEnabled = false;

              this.total = this.apng.frames.length;
              this.duration = this.apng.frames.reduce((s, f) => s + (f.dn / f.dd), 0);

              // At most ~16 snapshots regardless of length: a full-canvas bitmap is ~3MB, so this
              // trades a bounded amount of memory for a bounded worst-case replay.
              this.keyInterval = this.total > 64 ? Math.ceil(this.total / 16) : 0;

              // Cumulative elapsed time per frame, so the readout does not re-sum on every step.
              this.elapsed = new Array(this.total);
              let acc = 0;
              for (let i = 0; i < this.total; i++) {
                this.elapsed[i] = acc;
                acc += this.apng.frames[i].dn / this.apng.frames[i].dd;
              }

              this.bind();
              await this.seek(0);
              this.play();
            }

            async bitmap(i) {
              if (!this.bitmaps[i]) {
                const png = framePng(this.apng, this.apng.frames[i]);
                this.bitmaps[i] = await createImageBitmap(new Blob([png], { type: 'image/png' }));
              }
              return this.bitmaps[i];
            }

            // Draws one frame on top of the current canvas, honouring its blend and dispose ops.
            async step(i) {
              const f = this.apng.frames[i];
              let saved = null;

              if (f.dispose === 2) { saved = this.ctx.getImageData(f.x, f.y, f.w, f.h); }
              if (f.blend === 0) { this.ctx.clearRect(f.x, f.y, f.w, f.h); }

              this.ctx.drawImage(await this.bitmap(i), f.x, f.y);

              if (f.dispose === 1) { this.ctx.clearRect(f.x, f.y, f.w, f.h); }
              else if (f.dispose === 2 && saved) { this.ctx.putImageData(saved, f.x, f.y); }
            }

            // Frames are cumulative, so going backwards means replaying - from the nearest snapshot
            // rather than from the start. Each forward step is a small blit because frames carry
            // only their changed rectangle.
            async seek(target) {
              target = Math.max(0, Math.min(this.total - 1, target));

              const token = ++this.seekToken;
              const run = (async () => {
                // Let any in-flight seek unwind first so they cannot interleave on the canvas.
                if (this.seeking) { try { await this.seeking; } catch (e) { /* superseded */ } }
                if (token !== this.seekToken) { return; }

                if (target < this.index || this.index < 0) {
                  const key = this.nearestKey(target);

                  if (key >= 0) {
                    this.ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
                    this.ctx.drawImage(this.keys.get(key), 0, 0);
                    this.index = key;
                  } else {
                    this.ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
                    this.index = -1;
                  }
                }

                for (let i = this.index + 1; i <= target; i++) {
                  await this.step(i);
                  this.index = i;

                  // A newer seek arrived mid-replay; stop here and let it take over from wherever
                  // the canvas actually is, which stays consistent because index tracks each step.
                  if (token !== this.seekToken) { return; }

                  await this.snapshot(i);
                }

                this.index = target;
                this.sync();
              })();

              this.seeking = run;
              return run;
            }

            nearestKey(target) {
              let best = -1;
              for (const k of this.keys.keys()) {
                if (k <= target && k > best) { best = k; }
              }
              return best;
            }

            async snapshot(i) {
              if (this.keyInterval <= 0 || i % this.keyInterval !== 0 || this.keys.has(i)) { return; }

              try {
                this.keys.set(i, await createImageBitmap(this.canvas));
              } catch (e) {
                // Snapshots are an optimisation; losing one only costs replay time.
              }
            }

            sync() {
              const elapsed = this.elapsed[Math.max(0, this.index)] || 0;

              // Never fight the user's thumb while they are dragging it.
              if (!this.dragging) { this.scrub.value = this.index; }

              this.label.textContent = 'Frame ' + (this.index + 1).toLocaleString() + ' / ' + this.total.toLocaleString();
              this.time.textContent = elapsed.toFixed(1) + 's / ' + this.duration.toFixed(1) + 's';
              this.playBtn.textContent = this.playing ? '⏸ Pause' : '▶ Play';
              this.playBtn.setAttribute('aria-label', this.playing ? 'Pause' : 'Play');
            }

            play() {
              // Starting from the end replays from the beginning rather than sitting there.
              if (this.index >= this.total - 1) { this.seek(0); }

              this.playing = true;
              this.sync();

              const tick = async () => {
                if (!this.playing) { return; }
                const f = this.apng.frames[this.index];
                const ms = Math.max(10, (f.dn / f.dd) * 1000 / this.rate);

                this.timer = setTimeout(async () => {
                  if (!this.playing) { return; }

                  if (this.index + 1 >= this.total) {
                    if (!this.loop) { this.pause(); return; }
                    await this.seek(0);
                  } else {
                    await this.seek(this.index + 1);
                  }

                  tick();
                }, ms);
              };

              tick();
            }

            pause() {
              this.playing = false;
              clearTimeout(this.timer);
              this.sync();
            }

            bind() {
              const q = s => this.root.querySelector(s);
              this.playBtn = q('.p-play');
              this.scrub = q('.p-scrub');
              this.label = q('.p-frame');
              this.time = q('.p-time');

              this.scrub.max = this.total - 1;
              this.scrub.disabled = false;

              this.playBtn.addEventListener('click', () => this.playing ? this.pause() : this.play());
              q('.p-prev').addEventListener('click', async () => { this.pause(); await this.seek(this.index - 1); });
              q('.p-next').addEventListener('click', async () => { this.pause(); await this.seek(this.index + 1); });
              q('.p-start').addEventListener('click', async () => { this.pause(); await this.seek(0); });
              q('.p-end').addEventListener('click', async () => { this.pause(); await this.seek(this.total - 1); });
              // pointerdown/up bracket the drag so sync() leaves the thumb alone in between;
              // 'input' fires continuously during the drag, 'change' catches keyboard and click.
              this.scrub.addEventListener('pointerdown', () => { this.dragging = true; this.pause(); });
              this.scrub.addEventListener('pointerup', () => { this.dragging = false; });
              this.scrub.addEventListener('pointercancel', () => { this.dragging = false; });
              this.scrub.addEventListener('input', () => { this.pause(); this.seek(+this.scrub.value); });
              this.scrub.addEventListener('change', () => { this.dragging = false; this.seek(+this.scrub.value); });
              q('.p-rate').addEventListener('change', e => { this.rate = parseFloat(e.target.value); });

              this.loopBtn = q('.p-loop');
              this.loopBtn.addEventListener('click', () => {
                this.loop = !this.loop;
                this.loopBtn.classList.toggle('on', this.loop);
                this.loopBtn.setAttribute('aria-pressed', this.loop ? 'true' : 'false');
                this.loopBtn.title = this.loop ? 'Looping' : 'Play once';
              });

              this.root.querySelectorAll('button, input, select').forEach(el => { el.disabled = false; });

              document.addEventListener('keydown', async e => {
                if (e.target.matches('input, select, textarea')) { return; }
                if (e.key === ' ') { e.preventDefault(); this.playing ? this.pause() : this.play(); }
                else if (e.key === 'ArrowLeft') { e.preventDefault(); this.pause(); await this.seek(this.index - 1); }
                else if (e.key === 'ArrowRight') { e.preventDefault(); this.pause(); await this.seek(this.index + 1); }
                else if (e.key === 'Home') { e.preventDefault(); this.pause(); await this.seek(0); }
                else if (e.key === 'End') { e.preventDefault(); this.pause(); await this.seek(this.total - 1); }
              });
            }
          }

          document.querySelectorAll('.player').forEach(root => {
            const player = new ApngPlayer(root);
            player.load().catch(err => {
              // Decoding is a progressive enhancement: if anything goes wrong the page still shows
              // the animation, just without transport controls.
              console.error('APNG player failed', err);
              root.classList.add('failed');
              const img = root.querySelector('.p-fallback');
              if (img) { img.hidden = false; }
            });
          });
        })();
        """;
}
