/**
 * On-device document scanning — the "iPhone scanner" effect for paper
 * invoices, done entirely in the browser.
 *
 * Given a captured camera frame (or a picked image), we detect the largest
 * 4-sided paper contour, perspective-correct it (deskew) and crop to the
 * page, then return a clean JPEG. This is the single highest-value step for
 * Document AI accuracy: it gets a flat, framed page instead of a skewed
 * photo with desk/background around it.
 *
 * Heavy dependencies (OpenCV.js ~8 MB wasm + jscanify) are lazy-loaded from
 * CDN the first time a scan runs, so they never touch the main bundle and
 * are only downloaded by managers who actually use the camera scanner.
 *
 * Everything is best-effort: if the libraries fail to load, no document is
 * found, or detection looks unreliable, we fall back to the original image
 * (`cropped: false`) so capturing a page never breaks.
 */

// Pin versions so a CDN-side major bump can't silently change behaviour.
const OPENCV_SRC = 'https://docs.opencv.org/4.10.0/opencv.js';
const JSCANIFY_SRC = 'https://cdn.jsdelivr.net/npm/jscanify@1.4.0/src/jscanify.min.js';

// First load downloads + initialises ~8 MB of wasm; allow generous headroom.
// On timeout we fall back to the original frame and the next capture (cv is
// cached by then) runs fast.
const SCAN_TIMEOUT_MS = 30_000;

// A detected quad smaller than this fraction of the frame is treated as a
// false positive (e.g. a logo box) — we'd rather keep the whole photo.
const MIN_AREA_FRACTION = 0.12;

let openCvPromise: Promise<any> | null = null;
let jscanifyPromise: Promise<any> | null = null;

function loadScript(src: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const s = document.createElement('script');
    s.src = src;
    s.async = true;
    s.onload = () => resolve();
    s.onerror = () => reject(new Error(`Failed to load script: ${src}`));
    document.head.appendChild(s);
  });
}

/** Lazy-load OpenCV.js and resolve once its wasm runtime is initialised. */
function loadOpenCv(): Promise<any> {
  if (openCvPromise) return openCvPromise;
  openCvPromise = (async () => {
    const existing = (window as any).cv;
    if (existing?.Mat) return existing;
    await loadScript(OPENCV_SRC);
    const cv = (window as any).cv;
    if (!cv) throw new Error('OpenCV did not register on window');
    // Builds vary: some expose a Promise, some are ready immediately, most
    // signal readiness via onRuntimeInitialized. Normalise to a single await.
    if (typeof cv.then === 'function') return await cv;
    if (cv.Mat) return cv;
    return await new Promise<any>(resolve => {
      cv.onRuntimeInitialized = () => resolve(cv);
    });
  })();
  return openCvPromise;
}

/** Lazy-load jscanify (needs the global `cv` to already be present). */
function loadJscanify(): Promise<any> {
  if (jscanifyPromise) return jscanifyPromise;
  jscanifyPromise = (async () => {
    await loadOpenCv();
    if (!(window as any).jscanify) await loadScript(JSCANIFY_SRC);
    const lib = (window as any).jscanify;
    if (!lib) throw new Error('jscanify did not register on window');
    return lib;
  })();
  return jscanifyPromise;
}

/** Draw any image source onto a fresh canvas so OpenCV can read its pixels. */
async function toCanvas(source: HTMLCanvasElement | Blob): Promise<HTMLCanvasElement> {
  if (source instanceof HTMLCanvasElement) return source;
  const bitmap = await createImageBitmap(source);
  const canvas = document.createElement('canvas');
  canvas.width = bitmap.width;
  canvas.height = bitmap.height;
  canvas.getContext('2d')!.drawImage(bitmap, 0, 0);
  bitmap.close();
  return canvas;
}

function canvasToJpeg(canvas: HTMLCanvasElement, quality: number): Promise<Blob> {
  return new Promise((resolve, reject) =>
    canvas.toBlob(
      b => (b ? resolve(b) : reject(new Error('canvas.toBlob returned null'))),
      'image/jpeg',
      quality
    )
  );
}

/**
 * Light unsharp mask for OCR legibility: sharp = 1.6·img − 0.6·blur(σ=2).
 * Phone video frames are slightly soft (video pipeline, not the photo
 * pipeline) — this recovers the small-print edges Document AI needs without
 * introducing halos. Best-effort: failures leave the canvas untouched.
 */
function sharpenCanvas(cv: any, canvas: HTMLCanvasElement): void {
  let img: any = null;
  let blur: any = null;
  try {
    img = cv.imread(canvas);
    blur = new cv.Mat();
    cv.GaussianBlur(img, blur, new cv.Size(0, 0), 2);
    cv.addWeighted(img, 1.6, blur, -0.6, 0, img);
    cv.imshow(canvas, img);
  } catch {
    // Keep the un-sharpened crop.
  } finally {
    img?.delete?.();
    blur?.delete?.();
  }
}

function dist(a: { x: number; y: number }, b: { x: number; y: number }): number {
  return Math.hypot(a.x - b.x, a.y - b.y);
}

/** Shoelace area of the detected quad (corners in TL, TR, BR, BL order). */
function quadArea(c: Array<{ x: number; y: number }>): number {
  let area = 0;
  for (let i = 0; i < c.length; i++) {
    const p = c[i];
    const q = c[(i + 1) % c.length];
    area += p.x * q.y - q.x * p.y;
  }
  return Math.abs(area) / 2;
}

/**
 * Detect, deskew and crop the document in `source`.
 *
 * @returns `{ blob, cropped: true }` with the perspective-corrected page when
 *   a confident detection is found, otherwise `{ blob, cropped: false }` where
 *   `blob` is a JPEG of the untouched input.
 */
export async function autoCropDocument(
  source: HTMLCanvasElement | Blob,
  quality = 0.85
): Promise<{ blob: Blob; cropped: boolean }> {
  const srcCanvas = await toCanvas(source);
  const original = () => canvasToJpeg(srcCanvas, quality);

  const detect = async (): Promise<{ blob: Blob; cropped: boolean }> => {
    const cv = await loadOpenCv();
    const jscanify = await loadJscanify();
    const scanner = new jscanify();

    let img: any = null;
    let contour: any = null;
    try {
      img = cv.imread(srcCanvas);
      contour = scanner.findPaperContour(img);
      if (!contour) return { blob: await original(), cropped: false };

      const corners = scanner.getCornerPoints(contour);
      const { topLeftCorner: tl, topRightCorner: tr, bottomRightCorner: br, bottomLeftCorner: bl } =
        corners ?? {};
      const pts = [tl, tr, br, bl];
      const valid = pts.every(p => p && Number.isFinite(p.x) && Number.isFinite(p.y));
      if (!valid) return { blob: await original(), cropped: false };

      // Reject tiny / spurious detections — keep the whole photo instead.
      const frameArea = srcCanvas.width * srcCanvas.height;
      if (quadArea(pts) < frameArea * MIN_AREA_FRACTION) {
        return { blob: await original(), cropped: false };
      }

      // Output size from the detected edges so we preserve the page's aspect
      // ratio rather than squashing it into fixed dimensions.
      const outW = Math.round((dist(tl, tr) + dist(bl, br)) / 2);
      const outH = Math.round((dist(tl, bl) + dist(tr, br)) / 2);
      if (outW < 50 || outH < 50) return { blob: await original(), cropped: false };

      const result: HTMLCanvasElement = scanner.extractPaper(srcCanvas, outW, outH, corners);
      sharpenCanvas(cv, result);
      return { blob: await canvasToJpeg(result, quality), cropped: true };
    } finally {
      img?.delete?.();
      contour?.delete?.();
    }
  };

  // Race detection against a timeout; either failure path falls back to the
  // untouched frame so a capture is never lost. detect() is wrapped so a late
  // rejection on the losing side can't become an unhandled rejection.
  const safeDetect = detect().catch(async () => ({ blob: await original(), cropped: false }));
  const timeout = new Promise<{ blob: Blob; cropped: boolean }>(resolve =>
    setTimeout(() => void original().then(blob => resolve({ blob, cropped: false })), SCAN_TIMEOUT_MS)
  );
  return Promise.race([safeDetect, timeout]);
}
