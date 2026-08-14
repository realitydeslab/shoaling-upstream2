/**
 * Journey storage: a mutable draft per site, plus append-only published revisions.
 *
 * Revisions are immutable for a research reason rather than an engineering one. A field
 * session is only interpretable against the exact content that produced it, so "revision 7"
 * has to mean one thing forever.
 */

import { readFile, writeFile, mkdir, readdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { validateJourney, projectToCentreline } from './journey-schema.mjs';

export class JourneyStore {
  /** @param {string} root directory holding one subdirectory per site */
  constructor(root) {
    this.root = root;
  }

  siteDir(slug) {
    if (!/^[a-z0-9][a-z0-9-]{0,63}$/.test(slug)) {
      throw new Error(`invalid site slug: ${JSON.stringify(slug)}`);
    }
    return path.join(this.root, slug);
  }

  async listSites() {
    if (!existsSync(this.root)) return [];
    const entries = await readdir(this.root, { withFileTypes: true });
    const sites = [];
    for (const entry of entries) {
      if (!entry.isDirectory()) continue;
      const draft = await this.readDraft(entry.name).catch(() => null);
      const revisions = await this.listRevisions(entry.name);
      sites.push({
        slug: entry.name,
        title: draft?.title ?? entry.name,
        beats: draft?.beats?.length ?? 0,
        calibrated: draft?.editorFrame?.calibrated ?? false,
        latestRevision: revisions.at(-1) ?? null,
        splatFile: draft?.editorFrame?.splatFile ?? null,
      });
    }
    return sites.sort((a, b) => a.slug.localeCompare(b.slug));
  }

  async readDraft(slug) {
    const file = path.join(this.siteDir(slug), 'draft.json');
    return JSON.parse(await readFile(file, 'utf8'));
  }

  /**
   * Write the draft. Validation errors reject the write; warnings do not, because an author
   * mid-edit is routinely in a state they intend to leave.
   */
  async writeDraft(slug, doc) {
    const result = validateJourney(doc);
    if (!result.ok) {
      const err = new Error('journey failed validation');
      err.code = 'INVALID_JOURNEY';
      err.errors = result.errors;
      throw err;
    }
    const dir = this.siteDir(slug);
    await mkdir(dir, { recursive: true });
    await writeFile(path.join(dir, 'draft.json'), `${JSON.stringify(doc, null, 2)}\n`, 'utf8');
    return result;
  }

  /**
   * Merge a partial `editorFrame` into the stored draft.
   *
   * Deliberately its own operation rather than a full draft write. The trim box is a view
   * setting the author adjusts by dragging, and it should survive a reload whether or not
   * they remembered to save — but a full draft save would also commit whatever half-finished
   * beat edit happens to be open, and would be rejected outright if that edit does not yet
   * validate. Patching one subtree keeps the two concerns apart.
   */
  async patchEditorFrame(slug, patch) {
    const draft = await this.readDraft(slug);
    draft.editorFrame = { ...draft.editorFrame, ...patch };

    const result = validateJourney(draft);
    if (!result.ok) {
      const err = new Error('editorFrame patch would make the journey invalid');
      err.code = 'INVALID_JOURNEY';
      err.errors = result.errors;
      throw err;
    }

    const dir = this.siteDir(slug);
    await mkdir(dir, { recursive: true });
    await writeFile(path.join(dir, 'draft.json'), `${JSON.stringify(draft, null, 2)}\n`, 'utf8');
    return draft.editorFrame;
  }

  /**
   * Merge a partial `site` into the stored draft — in practice, the walking path.
   *
   * The path is the route the simulated walker follows; it is not where the points of
   * interest are. Those are placed independently in space. Keeping the two in separate
   * write paths means dragging the route never disturbs a beat, and vice versa.
   */
  async patchSite(slug, patch) {
    const draft = await this.readDraft(slug);
    draft.site = { ...draft.site, ...patch };

    // Beat `s` is a derived value: the beat's projection onto the walking path. Moving the
    // path therefore changes every beat's position along it, and leaving stale values would
    // desynchronise the scrubber from the scene.
    if (patch.centreline && Array.isArray(draft.beats)) {
      for (const beat of draft.beats) {
        beat.s = Number(projectToCentreline(beat.position, draft.site.centreline).s.toFixed(2));
      }
    }

    const result = validateJourney(draft);
    if (!result.ok) {
      const err = new Error('site patch would make the journey invalid');
      err.code = 'INVALID_JOURNEY';
      err.errors = result.errors;
      throw err;
    }

    const dir = this.siteDir(slug);
    await mkdir(dir, { recursive: true });
    await writeFile(path.join(dir, 'draft.json'), `${JSON.stringify(draft, null, 2)}\n`, 'utf8');
    return { site: draft.site, beats: draft.beats };
  }

  async listRevisions(slug) {
    const dir = path.join(this.siteDir(slug), 'revisions');
    if (!existsSync(dir)) return [];
    const files = await readdir(dir);
    return files
      .map((f) => /^r(\d+)\.json$/.exec(f))
      .filter(Boolean)
      .map((m) => Number(m[1]))
      .sort((a, b) => a - b);
  }

  async readRevision(slug, n) {
    const file = path.join(this.siteDir(slug), 'revisions', `r${String(n).padStart(6, '0')}.json`);
    return JSON.parse(await readFile(file, 'utf8'));
  }

  async readPublished(slug) {
    const revisions = await this.listRevisions(slug);
    if (revisions.length === 0) {
      const err = new Error(`site "${slug}" has no published revision`);
      err.code = 'NOT_PUBLISHED';
      throw err;
    }
    return this.readRevision(slug, revisions.at(-1));
  }

  /** Publish the current draft as the next revision. Append-only: never overwrites. */
  async publish(slug) {
    const draft = await this.readDraft(slug);
    const result = validateJourney(draft);
    if (!result.ok) {
      const err = new Error('cannot publish: journey failed validation');
      err.code = 'INVALID_JOURNEY';
      err.errors = result.errors;
      throw err;
    }

    const revisions = await this.listRevisions(slug);
    const next = (revisions.at(-1) ?? 0) + 1;

    const published = { ...draft, revision: next, publishedAt: new Date().toISOString() };

    const dir = path.join(this.siteDir(slug), 'revisions');
    await mkdir(dir, { recursive: true });
    await writeFile(
      path.join(dir, `r${String(next).padStart(6, '0')}.json`),
      `${JSON.stringify(published, null, 2)}\n`,
      'utf8'
    );

    // Keep the draft's revision number in step so the editor shows what it published.
    await this.writeDraft(slug, { ...draft, revision: next });

    return { revision: next, warnings: result.warnings, document: published };
  }
}
