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

function scaleCanvas(src: HTMLCanvasElement, scale: number): HTMLCanvasElement {
  const c = document.createElement('canvas');
  c.width = Math.max(1, Math.round(src.width * scale));
  c.height = Math.max(1, Math.round(src.height * scale));
  const ctx = c.getContext('2d')!;
  ctx.imageSmoothingEnabled = true;
  ctx.imageSmoothingQuality = 'high';
  ctx.drawImage(src, 0, 0, c.width, c.height);
  return c;
}

// Detection resolution (page-contour finding doesn't need more) and the
// normalised output band: every device — iPhone 4K frames, 8 MP tablet
// stills — delivers the same class of scan (~250 DPI for A4) to Document AI.
// Small crops are UPSCALED (max 2×, high-quality resampling): OCR engines
// read 2×-upscaled small print measurably better than the original.
const DETECT_MAX_EDGE = 1200;
const OUTPUT_MAX_EDGE = 3000;
const OUTPUT_MIN_EDGE = 1800;

/** Bring the page into the [OUTPUT_MIN_EDGE, OUTPUT_MAX_EDGE] band. */
function normaliseCanvasSize(src: HTMLCanvasElement): HTMLCanvasElement {
  const edge = Math.max(src.width, src.height);
  if (edge > OUTPUT_MAX_EDGE) return scaleCanvas(src, OUTPUT_MAX_EDGE / edge);
  if (edge < OUTPUT_MIN_EDGE) return scaleCanvas(src, Math.min(2, OUTPUT_MIN_EDGE / edge));
  return src;
}

/**
 * Return a canvas's backing buffer to the pool. iOS Safari has a hard
 * canvas-memory budget — exhausting it makes new canvases silently BLACK
 * and toBlob return null, so every big intermediate must be released the
 * moment it's no longer needed.
 */
function releaseCanvas(c: HTMLCanvasElement | null | undefined): void {
  if (!c) return;
  c.width = 0;
  c.height = 0;
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

/**
 * Even out uneven lighting (window shadows, lamp falloff) by dividing the
 * image by its own low-frequency background — the shadow bands that wreck
 * OCR disappear while text edges stay. Applies itself ONLY when the
 * background is measurably uneven (relative σ ≥ 0.10); an evenly lit scan
 * passes through untouched. The background is estimated on a downscaled
 * copy so the big-σ blur stays fast on 4K crops. Best-effort.
 */
function flattenIllumination(cv: any, canvas: HTMLCanvasElement): void {
  let img: any = null;
  let gray: any = null;
  let small: any = null;
  let bgSmall: any = null;
  let bg: any = null;
  let bgColor: any = null;
  let mean: any = null;
  let std: any = null;
  let out: any = null;
  try {
    img = cv.imread(canvas);
    cv.cvtColor(img, img, cv.COLOR_RGBA2RGB);
    gray = new cv.Mat();
    cv.cvtColor(img, gray, cv.COLOR_RGB2GRAY);

    // Background = heavy blur of a 1/8-scale gray copy, resized back.
    const ds = Math.max(1, Math.round(Math.max(gray.cols, gray.rows) / 300));
    small = new cv.Mat();
    cv.resize(gray, small, new cv.Size(Math.max(1, Math.round(gray.cols / ds)), Math.max(1, Math.round(gray.rows / ds))), 0, 0, cv.INTER_AREA);
    bgSmall = new cv.Mat();
    cv.GaussianBlur(small, bgSmall, new cv.Size(0, 0), 12);

    mean = new cv.Mat();
    std = new cv.Mat();
    cv.meanStdDev(bgSmall, mean, std);
    const m = mean.data64F[0];
    const sd = std.data64F[0];
    if (m <= 1 || sd / m < 0.10) return;   // evenly lit — leave the crop alone

    bg = new cv.Mat();
    cv.resize(bgSmall, bg, new cv.Size(gray.cols, gray.rows), 0, 0, cv.INTER_LINEAR);
    bgColor = new cv.Mat();
    cv.cvtColor(bg, bgColor, cv.COLOR_GRAY2RGB);

    // Integer division with scale — NO float conversion. The 32F route
    // needed three ~80 MB float mats for a 3000 px page and the resulting
    // memory spike killed the camera's video layer (black preview) and
    // then the whole tab on phones. 8U divide peaks ~4× lower and the
    // ±1 rounding difference is invisible to OCR.
    out = new cv.Mat();
    cv.divide(img, bgColor, out, 235);               // paper lands ≈ 235
    cv.imshow(canvas, out);
  } catch {
    // Keep the original crop.
  } finally {
    img?.delete?.();
    gray?.delete?.();
    small?.delete?.();
    bgSmall?.delete?.();
    bg?.delete?.();
    bgColor?.delete?.();
    mean?.delete?.();
    std?.delete?.();
    out?.delete?.();
  }
}

/**
 * CLAHE-based local contrast boost for washed-out photos (faded thermal
 * receipts, dim shots). Gated hard: it runs only when the page's global RMS
 * contrast is LOW (σ < 35) — on a normal crisp scan CLAHE would amplify
 * paper texture and hurt OCR, which is exactly what the literature warns
 * about. Works on the luminance channel so colours survive. Best-effort.
 */
function boostLocalContrast(cv: any, canvas: HTMLCanvasElement): void {
  let img: any = null;
  let ycrcb: any = null;
  let channels: any = null;
  let mean: any = null;
  let std: any = null;
  let clahe: any = null;
  try {
    img = cv.imread(canvas);
    cv.cvtColor(img, img, cv.COLOR_RGBA2RGB);
    ycrcb = new cv.Mat();
    cv.cvtColor(img, ycrcb, cv.COLOR_RGB2YCrCb);
    channels = new cv.MatVector();
    cv.split(ycrcb, channels);
    const y = channels.get(0);

    mean = new cv.Mat();
    std = new cv.Mat();
    cv.meanStdDev(y, mean, std);
    const sd = std.data64F[0];
    if (sd >= 35) { y.delete(); return; }   // contrast is fine — do nothing

    clahe = new cv.CLAHE(2.0, new cv.Size(8, 8));
    clahe.apply(y, y);
    channels.set(0, y);
    cv.merge(channels, ycrcb);
    cv.cvtColor(ycrcb, img, cv.COLOR_YCrCb2RGB);
    cv.imshow(canvas, img);
    y.delete();
  } catch {
    // Keep the un-boosted image.
  } finally {
    img?.delete?.();
    ycrcb?.delete?.();
    channels?.delete?.();
    mean?.delete?.();
    std?.delete?.();
    clahe?.delete?.();
  }
}

/**
 * Focus metric: variance of the Laplacian on a ≤1000 px grayscale copy.
 * Sharp document photos land in the hundreds; motion blur drops below ~55.
 * Returns undefined when the measurement fails.
 */
function laplacianSharpness(cv: any, canvas: HTMLCanvasElement): number | undefined {
  let img: any = null;
  let gray: any = null;
  let small: any = null;
  let lap: any = null;
  let mean: any = null;
  let std: any = null;
  try {
    img = cv.imread(canvas);
    gray = new cv.Mat();
    cv.cvtColor(img, gray, cv.COLOR_RGBA2GRAY);
    const scale = Math.min(1, 1000 / Math.max(gray.cols, gray.rows));
    small = new cv.Mat();
    if (scale < 1) {
      cv.resize(gray, small, new cv.Size(Math.round(gray.cols * scale), Math.round(gray.rows * scale)), 0, 0, cv.INTER_AREA);
    } else {
      gray.copyTo(small);
    }
    lap = new cv.Mat();
    cv.Laplacian(small, lap, cv.CV_64F);
    mean = new cv.Mat();
    std = new cv.Mat();
    cv.meanStdDev(lap, mean, std);
    const sd = std.data64F[0];
    return sd * sd;
  } catch {
    return undefined;
  } finally {
    img?.delete?.();
    gray?.delete?.();
    small?.delete?.();
    lap?.delete?.();
    mean?.delete?.();
    std?.delete?.();
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

export interface AutoCropResult {
  blob: Blob;
  cropped: boolean;
  /**
   * Variance-of-Laplacian focus metric of the (cropped) page — undefined
   * when it couldn't be measured. Below ~55 the photo is likely too blurred
   * for OCR and the UI should suggest a retake.
   */
  sharpness?: number;
}

/**
 * Detect, deskew and crop the document in `source`, then enhance it for OCR
 * (shadow flattening when lighting is uneven + a light unsharp mask) and
 * measure its focus.
 *
 * @returns `{ blob, cropped: true, sharpness }` with the perspective-corrected
 *   page when a confident detection is found, otherwise
 *   `{ blob, cropped: false, sharpness? }` where `blob` is a JPEG of the
 *   untouched input.
 */
export async function autoCropDocument(
  source: HTMLCanvasElement | Blob,
  quality = 0.85
): Promise<AutoCropResult> {
  const srcCanvas = await toCanvas(source);
  const original = () => canvasToJpeg(srcCanvas, quality);

  const detect = async (): Promise<AutoCropResult> => {
    const cv = await loadOpenCv();
    const jscanify = await loadJscanify();
    const scanner = new jscanify();

    // Contour detection runs on a ≤1200 px copy — identical detections, an
    // order of magnitude faster on 8 MP tablet stills / 4K phone frames —
    // and the corners scale back up for the full-resolution extraction.
    const detScale = Math.min(1, DETECT_MAX_EDGE / Math.max(srcCanvas.width, srcCanvas.height));
    const detCanvas = detScale < 1 ? scaleCanvas(srcCanvas, detScale) : srcCanvas;

    let img: any = null;
    let contour: any = null;
    try {
      img = cv.imread(detCanvas);
      contour = scanner.findPaperContour(img);
      if (!contour) return { blob: await original(), cropped: false, sharpness: laplacianSharpness(cv, detCanvas) };

      const corners = scanner.getCornerPoints(contour);
      const { topLeftCorner: tl, topRightCorner: tr, bottomRightCorner: br, bottomLeftCorner: bl } =
        corners ?? {};
      const pts = [tl, tr, br, bl];
      const valid = pts.every(p => p && Number.isFinite(p.x) && Number.isFinite(p.y));
      if (!valid) return { blob: await original(), cropped: false, sharpness: laplacianSharpness(cv, detCanvas) };

      // Reject tiny / spurious detections — keep the whole photo instead.
      const frameArea = detCanvas.width * detCanvas.height;
      if (quadArea(pts) < frameArea * MIN_AREA_FRACTION) {
        return { blob: await original(), cropped: false, sharpness: laplacianSharpness(cv, detCanvas) };
      }

      // Corners back to full resolution; output size from the detected edges
      // so the page keeps its aspect ratio.
      const f = 1 / detScale;
      const up = (p: { x: number; y: number }) => ({ x: p.x * f, y: p.y * f });
      const fullCorners = {
        topLeftCorner: up(tl),
        topRightCorner: up(tr),
        bottomRightCorner: up(br),
        bottomLeftCorner: up(bl)
      };
      const outW = Math.round((dist(fullCorners.topLeftCorner, fullCorners.topRightCorner) + dist(fullCorners.bottomLeftCorner, fullCorners.bottomRightCorner)) / 2);
      const outH = Math.round((dist(fullCorners.topLeftCorner, fullCorners.bottomLeftCorner) + dist(fullCorners.topRightCorner, fullCorners.bottomRightCorner)) / 2);
      if (outW < 50 || outH < 50) return { blob: await original(), cropped: false, sharpness: laplacianSharpness(cv, detCanvas) };

      const extracted: HTMLCanvasElement = scanner.extractPaper(srcCanvas, outW, outH, fullCorners);
      // Normalise the page into a consistent size band across devices
      // BEFORE measuring and enhancing, so thresholds and the unsharp
      // radius mean the same thing on every camera.
      const result = normaliseCanvasSize(extracted);
      if (result !== extracted) releaseCanvas(extracted);
      // Measure focus BEFORE enhancement (sharpening would inflate the
      // metric and mask genuine motion blur).
      const sharpness = laplacianSharpness(cv, result);
      flattenIllumination(cv, result);
      boostLocalContrast(cv, result);
      sharpenCanvas(cv, result);
      const blob = await canvasToJpeg(result, quality);
      releaseCanvas(result);
      return { blob, cropped: true, sharpness };
    } finally {
      img?.delete?.();
      contour?.delete?.();
      if (detCanvas !== srcCanvas) releaseCanvas(detCanvas);
    }
  };

  // Race detection against a timeout; either failure path falls back to the
  // untouched frame so a capture is never lost. detect() is wrapped so a late
  // rejection on the losing side can't become an unhandled rejection.
  const safeDetect = detect()
    .catch(async () => ({ blob: await original(), cropped: false }))
    // If even the fallback encode fails (e.g. srcCanvas already released
    // because the race settled long ago), swallow it — the result of a
    // losing branch is never observed.
    .catch(() => ({ blob: new Blob([], { type: 'image/jpeg' }), cropped: false }));
  const timeout = new Promise<AutoCropResult>(resolve =>
    setTimeout(() => void original().then(blob => resolve({ blob, cropped: false })).catch(() => { /* raced out */ }), SCAN_TIMEOUT_MS)
  );
  const out = await Promise.race([safeDetect, timeout]);
  // We own srcCanvas only when we created it from a Blob — release it then.
  // (A late-losing detect branch may still touch it; its result is discarded
  // and every path in it is wrapped, so the worst case is a silent no-op.)
  if (!(source instanceof HTMLCanvasElement)) releaseCanvas(srcCanvas);
  return out;
}
