/**
 * The armed-zones scrubber.
 *
 * Drag along the reach and see, at every position, which beats are live, which one wins,
 * and where the gates overlap. None of the seven locative authoring tools surveyed has this,
 * and with six zones on a 34 m creek an author simply cannot reason about trigger behaviour
 * from a map view — the exit bands overlap everywhere and the map shows only circles.
 *
 * It draws the same evaluation the runtime performs, so what you see here is what the phone
 * will do.
 */
import { evaluateAt, centrelineLength } from './geom.js';
const COLOUR = {
    bg: '#0A0D0B',
    hair: '#262F29',
    water: '#6DA7AD',
    accent: '#D4707F',
    moss: '#93A76B',
    muted: '#55625A',
    amber: '#D19A45',
    ink: '#B4BFB2',
};
const PAD = { left: 10, right: 10, top: 6, bottom: 16 };
export class Scrubber {
    canvas;
    ctx;
    onScrub;
    beats = [];
    length = 0;
    s = 0;
    selectedId = null;
    dragging = false;
    constructor(canvas, { onScrub } = {}) {
        this.canvas = canvas;
        // getContext('2d') is null only when the canvas already holds an incompatible context. The
        // scrubber owns its canvas, so this is an invariant rather than a case to handle.
        this.ctx = canvas.getContext('2d');
        this.onScrub = onScrub;
        canvas.addEventListener('pointerdown', (e) => {
            this.dragging = true;
            canvas.setPointerCapture(e.pointerId);
            this.#scrubTo(e);
        });
        canvas.addEventListener('pointermove', (e) => { if (this.dragging)
            this.#scrubTo(e); });
        canvas.addEventListener('pointerup', (e) => {
            this.dragging = false;
            canvas.releasePointerCapture(e.pointerId);
        });
        canvas.addEventListener('pointercancel', () => { this.dragging = false; });
        const ro = new ResizeObserver(() => this.draw());
        ro.observe(canvas);
    }
    setJourney(journey) {
        this.beats = journey?.beats ?? [];
        this.length = journey?.site?.centreline
            ? centrelineLength(journey.site.centreline)
            : 0;
        this.draw();
    }
    setSelected(id) { this.selectedId = id; this.draw(); }
    setS(s) { this.s = s; this.draw(); }
    #plotWidth() {
        return this.canvas.clientWidth - PAD.left - PAD.right;
    }
    #sToX(s) {
        if (this.length <= 0)
            return PAD.left;
        return PAD.left + (s / this.length) * this.#plotWidth();
    }
    #scrubTo(event) {
        const rect = this.canvas.getBoundingClientRect();
        const x = event.clientX - rect.left;
        const frac = (x - PAD.left) / Math.max(1, this.#plotWidth());
        const s = Math.max(0, Math.min(this.length, frac * this.length));
        this.s = s;
        this.draw();
        this.onScrub?.(s);
    }
    /** What is armed right now — also used by the app to drive the readout and rail. */
    evaluate() {
        return evaluateAt(this.s, this.beats, { respectOrder: false });
    }
    draw() {
        const canvas = this.canvas;
        const dpr = Math.min(devicePixelRatio, 2);
        const w = canvas.clientWidth;
        const h = canvas.clientHeight;
        if (w === 0 || h === 0)
            return;
        if (canvas.width !== w * dpr || canvas.height !== h * dpr) {
            canvas.width = w * dpr;
            canvas.height = h * dpr;
        }
        const ctx = this.ctx;
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, w, h);
        ctx.fillStyle = COLOUR.bg;
        ctx.fillRect(0, 0, w, h);
        if (this.length <= 0) {
            ctx.fillStyle = COLOUR.muted;
            ctx.font = '11px ui-monospace, monospace';
            ctx.fillText('no centreline', PAD.left, h / 2);
            return;
        }
        const top = PAD.top;
        const bandH = h - PAD.top - PAD.bottom;
        const axisY = h - PAD.bottom;
        // ---- metre ticks -------------------------------------------------
        ctx.strokeStyle = COLOUR.hair;
        ctx.fillStyle = COLOUR.muted;
        ctx.font = '9px ui-monospace, monospace';
        ctx.lineWidth = 1;
        const step = this.length > 80 ? 20 : this.length > 40 ? 10 : 5;
        for (let s = 0; s <= this.length + 0.01; s += step) {
            const x = Math.round(this.#sToX(s)) + 0.5;
            ctx.beginPath();
            ctx.moveTo(x, axisY);
            ctx.lineTo(x, axisY + 4);
            ctx.stroke();
            ctx.fillText(`${s.toFixed(0)}`, x + 2, axisY + 12);
        }
        ctx.fillText('m upstream →', this.#sToX(this.length) - 66, axisY + 12);
        ctx.strokeStyle = COLOUR.hair;
        ctx.beginPath();
        ctx.moveTo(PAD.left, axisY + 0.5);
        ctx.lineTo(w - PAD.right, axisY + 0.5);
        ctx.stroke();
        // ---- one lane per beat ------------------------------------------
        const lanes = Math.max(1, this.beats.length);
        const laneH = Math.max(3, Math.min(9, (bandH - 6) / lanes));
        const gap = Math.max(1, (bandH - laneH * lanes) / Math.max(1, lanes - 1));
        const { armed, winner } = evaluateAt(this.s, this.beats, { respectOrder: false });
        const armedIds = new Set(armed.map((a) => a.beat.id));
        this.beats.forEach((beat, i) => {
            const y = top + i * (laneH + gap);
            const enter = beat.trigger?.enterRadiusM ?? 0;
            const exit = beat.trigger?.exitRadiusM ?? enter;
            const isSelected = beat.id === this.selectedId;
            const isArmed = armedIds.has(beat.id);
            const isWinner = winner?.beat.id === beat.id;
            const x0 = this.#sToX(Math.max(0, beat.s - exit));
            const x1 = this.#sToX(Math.min(this.length, beat.s + exit));
            const e0 = this.#sToX(Math.max(0, beat.s - enter));
            const e1 = this.#sToX(Math.min(this.length, beat.s + enter));
            // exit band — the hysteresis margin
            ctx.fillStyle = isSelected ? 'rgba(212,112,127,0.16)' : 'rgba(147,167,107,0.12)';
            ctx.fillRect(x0, y, Math.max(1, x1 - x0), laneH);
            // enter band — where the beat actually fires
            ctx.fillStyle = isWinner
                ? COLOUR.accent
                : isArmed
                    ? (isSelected ? 'rgba(212,112,127,0.75)' : 'rgba(147,167,107,0.6)')
                    : (isSelected ? 'rgba(212,112,127,0.45)' : 'rgba(147,167,107,0.34)');
            ctx.fillRect(e0, y, Math.max(1, e1 - e0), laneH);
            // centre mark
            const cx = Math.round(this.#sToX(beat.s)) + 0.5;
            ctx.strokeStyle = isSelected ? COLOUR.accent : COLOUR.moss;
            ctx.beginPath();
            ctx.moveTo(cx, y - 1);
            ctx.lineTo(cx, y + laneH + 1);
            ctx.stroke();
        });
        // ---- overlap hazard strip ---------------------------------------
        // Where two enter bands overlap, two beats can fire in the same place. The runtime
        // resolves it winner-take-all, but the author should see it.
        for (let i = 1; i < this.beats.length; i += 1) {
            const a = this.beats[i - 1];
            const b = this.beats[i];
            const aEnd = a.s + (a.trigger?.enterRadiusM ?? 0);
            const bStart = b.s - (b.trigger?.enterRadiusM ?? 0);
            if (aEnd > bStart) {
                const x0 = this.#sToX(bStart);
                const x1 = this.#sToX(aEnd);
                ctx.fillStyle = 'rgba(209,154,69,0.5)';
                ctx.fillRect(x0, axisY - 3, Math.max(1.5, x1 - x0), 3);
            }
        }
        // ---- playhead ----------------------------------------------------
        const px = Math.round(this.#sToX(this.s)) + 0.5;
        ctx.strokeStyle = COLOUR.water;
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        ctx.moveTo(px, top - 4);
        ctx.lineTo(px, axisY + 2);
        ctx.stroke();
        ctx.fillStyle = COLOUR.water;
        ctx.beginPath();
        ctx.moveTo(px, top - 5);
        ctx.lineTo(px - 4, top - 10);
        ctx.lineTo(px + 4, top - 10);
        ctx.closePath();
        ctx.fill();
    }
}
//# sourceMappingURL=scrubber.js.map