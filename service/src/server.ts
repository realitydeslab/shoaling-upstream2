/**
 * Shoaling Upstream — authoring service.
 *
 * Serves the browser editor, stores journey drafts and published revisions, and runs the
 * live control bus that the operator's controller and the phone both connect to.
 *
 * Deliberately dependency-light: one package (`ws`) and Node's own http/fs. The editor is
 * served as plain ES modules with an import map, so there is no build step to go stale — and
 * neither has this file: Node 22 runs TypeScript by stripping the types, so `node
 * service/src/server.ts` starts the service with no toolchain in front of it. Stripping is not
 * checking, so `npm run typecheck` is where the types are actually verified.
 */

import http from 'node:http';
import { createReadStream, existsSync, statSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import os from 'node:os';
import type { AddressInfo } from 'node:net';
import { WebSocketServer } from 'ws';

import { JourneyStore } from './journey-store.ts';
import type { CodedError } from './journey-store.ts';
import { ControlBus } from './control-bus.ts';
import type { AckInput, CommandInput, PoseInput } from './control-bus.ts';
import { validateJourney, INTERACTIONS, SCHEMA_VERSION } from './journey-schema.ts';
import type { ScanOption } from '../../editor/src/types.ts';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
const EDITOR_DIR = path.join(ROOT, 'editor');
// Overridable so the test suite can point the service at a throwaway directory. Without this
// every end-to-end test would publish revisions into the artist's real journeys.
const DATA_DIR = process.env.JOURNEY_DIR ?? path.join(ROOT, 'data', 'journeys');
const SPLAT_DIR = path.join(ROOT, 'data', 'splats', 'proxy');
// The editor runs on localhost, where a 96 MB scan costs nothing but decode time. The proxy
// exists for web delivery; at a desk there is no reason to look at a decimated scan.
const SPLAT_FULL_DIR = path.join(ROOT, 'data', 'splats');
// Precomputed streaming LOD assets, built offline by tools/build-rad.sh. When one exists the
// editor prefers it: the level-of-detail tree is already built, and it streams in chunks
// instead of downloading the whole capture before anything appears.
const SPLAT_RAD_DIR = path.join(ROOT, 'data', 'splats', 'rad');
const AUDIO_DIR = path.join(ROOT, 'data', 'audio', 'packaged');
const MODULES_DIR = path.join(ROOT, 'service', 'node_modules');

const PORT = Number(process.env.PORT ?? 8710);
const HOST = process.env.HOST ?? '0.0.0.0';

const store = new JourneyStore(DATA_DIR);
const bus = new ControlBus();

const MIME: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.wav': 'audio/wav',
  '.mp3': 'audio/mpeg',
  '.spz': 'application/octet-stream',
  '.rad': 'application/octet-stream',
  '.radc': 'application/octet-stream',
  '.map': 'application/json; charset=utf-8',
};

/**
 * A view of whatever was thrown, for the `code` the store and Node's fs both set. Not a claim
 * that it IS an Error — every field on CodedError is optional, so a thrown string reads as
 * having no code, exactly as `err.code` did.
 */
const coded = (err: unknown): CodedError => err as CodedError;

function sendJson(res: http.ServerResponse, status: number, body: unknown): void {
  const payload = JSON.stringify(body, null, 2);
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(payload),
    'cache-control': 'no-store',
  });
  res.end(payload);
}

function sendError(
  res: http.ServerResponse,
  status: number,
  message: string,
  extra: Record<string, unknown> = {},
): void {
  sendJson(res, status, { error: message, ...extra });
}

/** Serve a file, refusing anything that escapes its base directory. */
function serveFile(
  res: http.ServerResponse,
  baseDir: string,
  relPath: string,
  { cache = false }: { cache?: boolean } = {},
): void {
  const resolved = path.resolve(baseDir, `.${path.posix.normalize(`/${relPath}`)}`);
  if (!resolved.startsWith(path.resolve(baseDir))) {
    return sendError(res, 403, 'path escapes the served directory');
  }
  if (!existsSync(resolved) || !statSync(resolved).isFile()) {
    return sendError(res, 404, `not found: ${relPath}`);
  }
  const ext = path.extname(resolved).toLowerCase();
  const stat = statSync(resolved);
  res.writeHead(200, {
    'content-type': MIME[ext] ?? 'application/octet-stream',
    'content-length': stat.size,
    'cache-control': cache ? 'public, max-age=3600' : 'no-store',
    'accept-ranges': 'bytes',
  });
  createReadStream(resolved).pipe(res);
}

async function readBody(
  req: http.IncomingMessage,
  limitBytes = 4 * 1024 * 1024,
): Promise<unknown> {
  const chunks: Buffer[] = [];
  let total = 0;
  for await (const chunk of req) {
    total += chunk.length;
    if (total > limitBytes) {
      const err: CodedError = new Error('request body too large');
      err.code = 'TOO_LARGE';
      throw err;
    }
    chunks.push(chunk);
  }
  if (total === 0) return null;
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url!, `http://${req.headers.host ?? 'localhost'}`);
  const { pathname } = url;

  res.setHeader('access-control-allow-origin', '*');
  res.setHeader('access-control-allow-methods', 'GET, PUT, POST, OPTIONS');
  res.setHeader('access-control-allow-headers', 'content-type');
  if (req.method === 'OPTIONS') { res.writeHead(204); return res.end(); }

  try {
    // ---------- API ----------
    if (pathname === '/api/health') {
      return sendJson(res, 200, {
        ok: true,
        schemaVersion: SCHEMA_VERSION,
        sessionId: bus.sessionId,
        uptimeSeconds: Math.round(process.uptime()),
      });
    }

    if (pathname === '/api/interactions') {
      return sendJson(res, 200, INTERACTIONS);
    }

    // Which representations of a scan actually exist on disk, best first. The editor asks
    // rather than probing for 404s, so a missing .rad degrades quietly to the full capture.
    if (pathname === '/api/scans') {
      const sites = await store.listSites();
      const scans: Record<string, ScanOption[]> = {};
      for (const site of sites) {
        const proxy = site.splatFile;
        if (!proxy) continue;
        const base = proxy.replace(/\.proxy\.spz$/, '');
        const options: ScanOption[] = [];
        if (existsSync(path.join(SPLAT_RAD_DIR, `${base}.rad`))) {
          options.push({ kind: 'rad', url: `/splats-rad/${base}.rad`, paged: true,
                         label: 'streaming, precomputed LOD' });
        }
        if (existsSync(path.join(SPLAT_FULL_DIR, `${base}.spz`))) {
          options.push({ kind: 'full', url: `/splats-full/${base}.spz`, paged: false,
                         label: 'full capture' });
        }
        if (existsSync(path.join(SPLAT_DIR, proxy))) {
          options.push({ kind: 'proxy', url: `/splats/${proxy}`, paged: false,
                         label: '600k proxy' });
        }
        scans[site.slug] = options;
      }
      return sendJson(res, 200, scans);
    }

    if (pathname === '/api/audio') {
      try {
        const raw = await readFile(path.join(ROOT, 'data', 'audio', 'catalogue.json'), 'utf8');
        return sendJson(res, 200, JSON.parse(raw));
      } catch {
        return sendJson(res, 200, { clips: [], note: 'run tools/package-audio.sh' });
      }
    }

    if (pathname === '/api/sites') {
      return sendJson(res, 200, await store.listSites());
    }

    // /api/sites/:slug/...
    const siteMatch = /^\/api\/sites\/([a-z0-9][a-z0-9-]{0,63})(\/.*)?$/.exec(pathname);
    if (siteMatch) {
      const slug = siteMatch[1]!;
      const rest = siteMatch[2] ?? '';

      if (rest === '/draft' && req.method === 'GET') {
        try {
          return sendJson(res, 200, await store.readDraft(slug));
        } catch {
          return sendError(res, 404, `no draft for site "${slug}"`);
        }
      }

      if (rest === '/draft' && req.method === 'PUT') {
        const body = await readBody(req);
        if (!body) return sendError(res, 400, 'empty body');
        try {
          const result = await store.writeDraft(slug, body);
          return sendJson(res, 200, { ok: true, warnings: result.warnings });
        } catch (err) {
          if (coded(err).code === 'INVALID_JOURNEY') {
            return sendError(res, 422, 'journey failed validation', { errors: coded(err).errors });
          }
          throw err;
        }
      }

      // Persist view settings (the trim box) without touching the rest of the draft.
      if (rest === '/editor-frame' && req.method === 'PUT') {
        const body = await readBody(req);
        if (!body) return sendError(res, 400, 'empty body');
        try {
          const frame = await store.patchEditorFrame(slug, body);
          return sendJson(res, 200, { ok: true, editorFrame: frame });
        } catch (err) {
          if (coded(err).code === 'INVALID_JOURNEY') {
            return sendError(res, 422, 'invalid editorFrame', { errors: coded(err).errors });
          }
          if (coded(err).code === 'ENOENT') return sendError(res, 404, `no draft for "${slug}"`);
          throw err;
        }
      }

      // The walking path, saved on its own. Separate from the beats by design.
      if (rest === '/site' && req.method === 'PUT') {
        const body = await readBody(req);
        if (!body) return sendError(res, 400, 'empty body');
        try {
          return sendJson(res, 200, { ok: true, ...(await store.patchSite(slug, body)) });
        } catch (err) {
          if (coded(err).code === 'INVALID_JOURNEY') {
            return sendError(res, 422, 'invalid site patch', { errors: coded(err).errors });
          }
          if (coded(err).code === 'ENOENT') return sendError(res, 404, `no draft for "${slug}"`);
          throw err;
        }
      }

      if (rest === '/validate' && req.method === 'POST') {
        const body = await readBody(req);
        return sendJson(res, 200, validateJourney(body));
      }

      if (rest === '/published' && req.method === 'GET') {
        try {
          return sendJson(res, 200, await store.readPublished(slug));
        } catch (err) {
          if (coded(err).code === 'NOT_PUBLISHED') return sendError(res, 404, coded(err).message);
          throw err;
        }
      }

      if (rest === '/publish' && req.method === 'POST') {
        try {
          const result = await store.publish(slug);
          bus.state.site = slug;
          bus.state.revision = result.revision;
          bus.broadcast({ type: 'published', site: slug, revision: result.revision });
          return sendJson(res, 200, {
            ok: true, revision: result.revision, warnings: result.warnings,
          });
        } catch (err) {
          if (coded(err).code === 'INVALID_JOURNEY') {
            return sendError(res, 422, 'cannot publish', { errors: coded(err).errors });
          }
          throw err;
        }
      }

      if (rest === '/revisions' && req.method === 'GET') {
        return sendJson(res, 200, await store.listRevisions(slug));
      }

      const revMatch = /^\/revisions\/(\d+)$/.exec(rest);
      if (revMatch && req.method === 'GET') {
        try {
          return sendJson(res, 200, await store.readRevision(slug, Number(revMatch[1])));
        } catch {
          return sendError(res, 404, 'no such revision');
        }
      }
    }

    // Control state over plain HTTP: a reachability probe and a resync hook that is trivially
    // checkable with curl. Not a polling fallback — polling would not help with the real
    // failure, which is a phone on cellular that cannot route to this machine at all.
    if (pathname === '/api/control/state' && req.method === 'GET') {
      return sendJson(res, 200, { state: bus.state, serverNowMs: Date.now() });
    }

    if (pathname === '/api/control/command' && req.method === 'POST') {
      const body = await readBody(req) as { action?: unknown } | null;
      if (!body?.action) return sendError(res, 400, 'action is required');
      return sendJson(res, 200, bus.issue(body as CommandInput));
    }

    // ---------- static ----------
    if (pathname.startsWith('/splats-rad/')) {
      return serveFile(res, SPLAT_RAD_DIR, pathname.slice('/splats-rad/'.length), { cache: true });
    }
    if (pathname.startsWith('/splats-full/')) {
      return serveFile(res, SPLAT_FULL_DIR, pathname.slice('/splats-full/'.length), { cache: true });
    }
    if (pathname.startsWith('/splats/')) {
      return serveFile(res, SPLAT_DIR, pathname.slice('/splats/'.length), { cache: true });
    }
    if (pathname.startsWith('/audio/')) {
      return serveFile(res, AUDIO_DIR, pathname.slice('/audio/'.length), { cache: true });
    }
    if (pathname.startsWith('/vendor/')) {
      return serveFile(res, MODULES_DIR, pathname.slice('/vendor/'.length), { cache: true });
    }
    if (pathname === '/' || pathname === '') {
      return serveFile(res, EDITOR_DIR, 'index.html');
    }
    if (pathname === '/control') {
      return serveFile(res, EDITOR_DIR, 'control.html');
    }
    return serveFile(res, EDITOR_DIR, pathname);
  } catch (err) {
    if (err instanceof SyntaxError) return sendError(res, 400, 'body is not valid JSON');
    if (coded(err).code === 'TOO_LARGE') return sendError(res, 413, 'request body too large');
    console.error('[server]', err);
    return sendError(res, 500, 'internal error');
  }
});

// ---------- WebSocket ----------
const wss = new WebSocketServer({ server, path: '/ws' });

/** Whatever a client sent, once it parsed as JSON. Never trusted beyond `type`. */
interface ClientMessage {
  type?: string;
  device?: string;
  os?: string;
  build?: string;
  [key: string]: unknown;
}

wss.on('connection', (socket, req) => {
  const url = new URL(req.url!, 'http://localhost');
  const role = url.searchParams.get('role') === 'device' ? 'device' : 'operator';
  const id = bus.add(socket, role);

  socket.on('message', (raw) => {
    let msg: ClientMessage;
    try {
      msg = JSON.parse(raw.toString());
    } catch {
      return bus.send(socket, { type: 'error', message: 'message is not valid JSON' });
    }

    switch (msg.type) {
      case 'status':
        bus.applyStatus(id, msg);
        break;
      case 'hello':
        bus.describeDevice(id, {
          device: msg.device ?? null,
          os: msg.os ?? null,
          build: msg.build ?? null,
        });
        break;
      case 'heartbeat':
        bus.touch(id);
        bus.send(socket, { type: 'pong', serverNowMs: Date.now() });
        break;
      case 'ack':
        bus.acknowledge(id, msg as unknown as AckInput);
        break;
      case 'command':
        // Operators issue commands over the socket too, so the UI has one code path.
        if (role === 'operator') bus.issue(msg as unknown as CommandInput);
        break;
      case 'pose':
        // The editor's walk simulation, streamed to any device following along at a desk.
        if (role === 'operator') bus.streamPose(msg as PoseInput);
        break;
      default:
        bus.send(socket, { type: 'error', message: `unknown message type: ${msg.type}` });
    }
  });

  socket.on('close', () => bus.remove(id));
  socket.on('error', () => bus.remove(id));
});

// A device that stops sending heartbeats should stop looking connected in the operator UI.
setInterval(() => bus.broadcastPresence(), 4000).unref();

function lanAddresses(): { name: string; address: string }[] {
  const out: { name: string; address: string }[] = [];
  for (const [name, addrs] of Object.entries(os.networkInterfaces())) {
    for (const addr of addrs ?? []) {
      if (addr.family === 'IPv4' && !addr.internal) out.push({ name, address: addr.address });
    }
  }
  return out;
}

server.listen(PORT, HOST, () => {
  const addrs = lanAddresses();
  // The bound port, not the requested one: PORT=0 asks the OS to pick, which is how the tests
  // avoid colliding with a dev server that is already running.
  const port = (server.address() as AddressInfo).port;
  console.log(`\n  Shoaling Upstream — authoring service`);
  console.log(`  schema ${SCHEMA_VERSION} · session ${bus.sessionId}\n`);
  console.log(`  editor      http://localhost:${port}/`);
  console.log(`  controller  http://localhost:${port}/control`);
  for (const { name, address } of addrs) {
    console.log(`  on ${name.padEnd(10)} http://${address}:${port}/`);
  }
  if (addrs.length > 0) {
    console.log(`\n  phone connects to  ws://${addrs[0]!.address}:${port}/ws?role=device`);
  }
  console.log('');
});

export { server, bus, store };
