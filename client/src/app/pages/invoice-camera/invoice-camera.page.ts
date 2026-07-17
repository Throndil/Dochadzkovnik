import { Component, ElementRef, OnDestroy, OnInit, computed, effect, signal, viewChild, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { InvoiceService, ScanStatus } from '../../services/invoice.service';
import { normaliseFile } from '../../utils/image-utils';

/**
 * /admin/invoices/scan — in-app camera scanner for paper invoices.
 *
 * Stage 3 of INVOICE_SCANNING_CAMERA_STAGES.md. The manager photographs
 * each page of a paper invoice; the component collects them, shows
 * per-page thumbnails with retake/delete/reorder, then submits all the
 * images to /api/invoices/upload-photos which assembles them into a PDF
 * and feeds Document AI.
 *
 * State machine:
 *   idle           — initial; show "Povoliť kameru" button + file-picker fallback
 *   streaming      — live <video> getUserMedia preview, shutter button
 *   review-page    — frozen frame; manager accepts or retakes
 *   pages-list     — between captures: thumbnails of accepted pages
 *   uploading      — POST in flight
 *   error          — getUserMedia denied / upload failed
 */
@Component({
  selector: 'app-invoice-camera',
  standalone: true,
  imports: [CommonModule, NavbarComponent, SpinnerComponent],
  templateUrl: './invoice-camera.page.html'
})
export class InvoiceCameraPage implements OnInit, OnDestroy {
  private svc = inject(InvoiceService);
  private router = inject(Router);

  // ── View refs (live <video>, hidden capture canvas, hidden file-picker fallback) ──
  videoRef       = viewChild<ElementRef<HTMLVideoElement>>('preview');
  canvasRef      = viewChild<ElementRef<HTMLCanvasElement>>('capture');
  fileInputRef   = viewChild<ElementRef<HTMLInputElement>>('fileInput');
  cameraInputRef = viewChild<ElementRef<HTMLInputElement>>('cameraInput');

  // ── State machine ──
  state = signal<'idle' | 'streaming' | 'review-page' | 'pages-list' | 'uploading' | 'error'>('idle');
  errorMsg = signal<string | null>(null);

  /** Pages already accepted by the manager. Order matters — page 1 first. */
  pages = signal<{ id: number; blob: Blob; thumbUrl: string }[]>([]);

  /**
   * Frozen frame shown on the review-page state. The photo is uploaded
   * exactly as captured — cropping/enhancement happens server-side
   * (ImageSharp normalisation) and, for hard scans, in the AI fallback.
   * In-browser OpenCV processing was removed on purpose: its full-page
   * buffers kept exhausting phone browsers (black previews, tab crashes).
   */
  pending = signal<{
    blob: Blob;
    thumbUrl: string;
    /** Quality verdict shown on the review screen (low resolution). */
    warning: string | null;
  } | null>(null);

  /** True while the auto-crop detection runs after pressing the shutter. */
  processing = signal(false);

  /**
   * Viewfinder zoom. Native camera zoom (applyConstraints) is used where the
   * browser supports it — the stream itself zooms, full sensor detail. Where
   * it isn't (older iOS), we fall back to digital zoom: the preview scales
   * via CSS and grabStill() crops the identical region, so WYSIWYG holds.
   */
  readonly zoomLevels = [1, 2, 3];
  zoom = signal(1);
  zoomNative = signal(false);

  /** Torch (camera light) — shown only when the device/browser exposes it
   *  (Android Chrome, iOS 18+ Safari). */
  torchAvailable = signal(false);
  torchOn = signal(false);
  /** Live low-light hint, sampled from the preview every ~1,2 s. */
  lowLight = signal(false);
  private lumaTimer: ReturnType<typeof setInterval> | null = null;
  private lumaCanvas: HTMLCanvasElement | null = null;

  private nextPageId = 1;
  private stream: MediaStream | null = null;
  /** Bumped on every stream (re)acquire — `stream` itself is a plain field,
   *  so without this the attach effect would NOT rerun when the camera is
   *  restarted while the <video> element stays mounted (the post-failure
   *  restart), leaving the old dead/degraded stream on screen. */
  private streamGen = signal(0);
  /** Reusable full-resolution frame buffer for the burst capture. */
  private fullFrame: HTMLCanvasElement | null = null;
  /** Set by grabStill when the captured region is too small for reliable OCR. */
  private lastCaptureLowRes = false;
  /** Rate limit for the degradation watchdog's camera restarts. */
  private lastCameraRestart = 0;
  /** Consecutive failed captures. After 2 we stop retrying the in-tab camera
   *  and steer to the native flow — the degraded→black→crash spiral on
   *  low-memory phones starts exactly here. Reset on any successful grab. */
  private grabFails = 0;
  /** Which capture flow the manager chose — "Pridať ďalšiu stranu" follows
   *  it (live viewfinder users stay in live mode even though the camera is
   *  released between shots; native users get the native camera again). */
  private preferLive = false;

  constructor() {
    // The <video #preview> lives inside @if(state === 'streaming'), so it is
    // (re)created AFTER startCamera() flips the state — and again every time
    // the manager returns from the review step. Assigning srcObject before
    // the element existed was the phone's black-preview bug (the shutter then
    // saw videoWidth 0 and silently did nothing). Attach the live stream
    // whenever the element (re)appears instead.
    effect(() => {
      this.streamGen();   // rerun on every stream (re)acquire too
      const video = this.videoRef()?.nativeElement;
      if (!video || !this.stream) return;
      if (video.srcObject !== this.stream) {
        video.srcObject = this.stream;
        // Muted + playsinline → programmatic play is allowed on iOS.
        video.play().catch(() => {});
      }
    });
  }

  // ─── Lifecycle ────────────────────────────────────────────────

  /** Pipeline health → persistent banner (quota spent / outage). */
  scanStatus = signal<ScanStatus | null>(null);

  /** Local-time label for the AI quota reset, e.g. "zajtra o 09:00". */
  scanStatusResetLabel = computed(() => {
    const until = this.scanStatus()?.aiExhaustedUntil;
    if (!until) return '';
    const d = new Date(until);
    const time = d.toLocaleTimeString('sk-SK', { hour: '2-digit', minute: '2-digit' });
    const today = new Date().toDateString() === d.toDateString();
    return today ? `dnes o ${time}` : `zajtra o ${time}`;
  });

  ngOnInit() {
    // Nothing to pre-start: the primary flow is the NATIVE camera via
    // input[capture] — no getUserMedia, no permission prompt, no live
    // stream in the tab. The in-app viewfinder is opt-in per visit.
    // Do check the pipeline health so the customer sees quota/outage
    // banners BEFORE photographing ten pages for nothing.
    this.svc.getScanStatus().then(s => this.scanStatus.set(s)).catch(() => {});
  }

  /** Primary capture: the phone's native camera app (input[capture]).
   *  Full-res stills, native night mode/flash, zero in-tab memory. */
  openNativeCamera() {
    this.preferLive = false;
    // Release any in-app viewfinder stream first — a leftover live stream would
    // otherwise reroute later navigation (e.g. "add another") into the live
    // view even though the manager is on the native camera.
    this.stopStream();
    this.cameraInputRef()?.nativeElement.click();
  }

  ngOnDestroy() {
    this.stopStream();
    // Revoke any object URLs we created so the browser doesn't keep them alive.
    for (const p of this.pages()) URL.revokeObjectURL(p.thumbUrl);
    const pend = this.pending();
    if (pend) URL.revokeObjectURL(pend.thumbUrl);
  }

  // ─── Permission + stream management ───────────────────────────

  /**
   * User-gesture trigger from the "Povoliť kameru" button. iOS Safari
   * requires the getUserMedia call to be inside a click handler.
   */
  async startCamera(allowRetry = true): Promise<void> {
    this.errorMsg.set(null);
    let stream: MediaStream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        video: {
          facingMode: { ideal: 'environment' },
          // 1080p on purpose: the in-tab camera pipeline's memory scales
          // with resolution and was crashing phones at 4K/QHD. The live
          // viewfinder is the secondary flow — the native camera button is
          // the primary and shoots at full sensor resolution regardless.
          width:  { ideal: 1920 },
          height: { ideal: 1080 }
        },
        audio: false
      });
    } catch (e: any) {
      this.errorMsg.set(this.translatePermissionError(e));
      this.state.set('error');
      return;
    }
    // Right after a restart WebKit sometimes hands back a DEGRADED stream
    // (camera not fully released yet) — detect it and re-acquire once
    // instead of leaving the manager with an extremely blurry preview.
    const s = stream.getVideoTracks()[0]?.getSettings?.();
    const acquiredEdge = Math.max(s?.width ?? 0, s?.height ?? 0);
    if (allowRetry && acquiredEdge > 0 && acquiredEdge < 1280) {
      for (const t of stream.getTracks()) t.stop();
      await new Promise(r => setTimeout(r, 800));
      return this.startCamera(false);
    }
    this.stream = stream;
    this.streamGen.update(v => v + 1);
    // The stream is attached by the constructor effect once the <video>
    // element renders (it doesn't exist until the state flips below).
    this.zoom.set(1);
    this.zoomNative.set(false);
    this.torchOn.set(false);

    const track = stream.getVideoTracks()[0];
    const caps: any = track?.getCapabilities?.() ?? {};
    this.torchAvailable.set(!!caps.torch);
    // Best-effort quality knobs (Android; ignored elsewhere): continuous
    // autofocus/exposure/white-balance and no browser-side downscaling.
    // Each advanced entry is independent, so an unsupported one is skipped.
    track?.applyConstraints({
      advanced: [
        { focusMode: 'continuous' } as any,
        { exposureMode: 'continuous' } as any,
        { whiteBalanceMode: 'continuous' } as any,
        { resizeMode: 'none' } as any
      ]
    } as any).catch(() => {});

    this.startLumaSampling();
    this.preferLive = true;
    this.state.set('streaming');
  }

  /** Camera light for dark sites/containers. Keeps the stream alive. */
  async toggleTorch() {
    const track = this.stream?.getVideoTracks()[0];
    if (!track) return;
    const want = !this.torchOn();
    try {
      await track.applyConstraints({ advanced: [{ torch: want } as any] } as any);
      this.torchOn.set(want);
    } catch {
      this.torchAvailable.set(false);
    }
  }

  /**
   * Mean-luma sampling of the live preview (32×24 thumbnail, cheap) — when
   * the scene is dark the UI suggests the torch / more light BEFORE the
   * manager wastes a capture on an unreadable photo.
   */
  private startLumaSampling() {
    this.stopLumaSampling();
    this.lumaTimer = setInterval(() => {
      if (this.state() !== 'streaming') return;
      // Degradation watchdog: WebKit silently DOWNSCALES a live camera
      // track under memory pressure and never upgrades it back — liveness
      // checks pass while the preview turns to mush. Detect it from the
      // track's actual settings and re-acquire (rate-limited).
      const set = this.stream?.getVideoTracks()[0]?.getSettings?.();
      const edge = Math.max(set?.width ?? 0, set?.height ?? 0);
      if (edge > 0 && edge < 1000 && Date.now() - this.lastCameraRestart > 10_000) {
        this.lastCameraRestart = Date.now();
        this.stopStream();
        setTimeout(() => void this.startCamera(), 500);
        return;
      }
      const video = this.videoRef()?.nativeElement;
      if (!video || !video.videoWidth) return;
      this.lumaCanvas ??= document.createElement('canvas');
      const c = this.lumaCanvas;
      c.width = 32;
      c.height = 24;
      const ctx = c.getContext('2d', { willReadFrequently: true });
      if (!ctx) return;
      ctx.drawImage(video, 0, 0, 32, 24);
      const d = ctx.getImageData(0, 0, 32, 24).data;
      let sum = 0;
      for (let i = 0; i < d.length; i += 4) sum += 0.299 * d[i] + 0.587 * d[i + 1] + 0.114 * d[i + 2];
      this.lowLight.set(sum / (d.length / 4) < 70);
    }, 1200);
  }

  private stopLumaSampling() {
    if (this.lumaTimer) {
      clearInterval(this.lumaTimer);
      this.lumaTimer = null;
    }
    this.lowLight.set(false);
  }

  /** 1× / 2× / 3× buttons — native track zoom when available, else digital. */
  async setZoom(z: number) {
    this.zoom.set(z);
    const track = this.stream?.getVideoTracks()[0];
    const caps: any = track?.getCapabilities?.();
    if (track && caps?.zoom && z >= (caps.zoom.min ?? 1) && z <= caps.zoom.max) {
      try {
        await track.applyConstraints({ advanced: [{ zoom: z } as any] } as any);
        this.zoomNative.set(true);
        return;
      } catch {
        // Constraint rejected — fall through to digital zoom.
      }
    }
    this.zoomNative.set(false);
  }

  private stopStream() {
    this.stopLumaSampling();
    this.torchOn.set(false);
    // Release the <video>'s decoded media surface, not just the camera tracks.
    // iOS Safari keeps the old GPU video layer allocated until srcObject is
    // explicitly cleared; across a few restart cycles those dead surfaces pile
    // up → degraded/black preview → tab OOM-crash. pause + null + load() forces
    // WebKit to let go. Guarded because the element may already be torn down
    // (the <video> lives inside @if(state === 'streaming')).
    const video = this.videoRef()?.nativeElement;
    if (video) {
      try {
        video.pause();
        video.srcObject = null;
        video.load();
      } catch { /* element already gone */ }
    }
    if (this.stream) {
      for (const track of this.stream.getTracks()) track.stop();
      this.stream = null;
    }
  }

  private translatePermissionError(e: any): string {
    const name = (e?.name ?? '').toString();
    if (name === 'NotAllowedError' || name === 'PermissionDeniedError')
      return 'Prístup ku kamere bol zamietnutý. Povoľte ho v nastaveniach prehliadača a skúste znova.';
    if (name === 'NotFoundError')
      return 'Na tomto zariadení sa nenašla kamera.';
    if (name === 'NotReadableError')
      return 'Kameru používa iná aplikácia. Zatvorte ju a skúste znova.';
    return 'Kameru sa nepodarilo spustiť. Použite tlačidlo nižšie a vyberte fotky zo zariadenia.';
  }

  // ─── Shutter / review / accept / retake ───────────────────────

  /**
   * Capture a still from the live video into the hidden canvas, run the
   * on-device document scanner (edge-detect + deskew + crop), and move to the
   * review-page state. The video stream stays running so retake → accept →
   * next-page is fast. Auto-crop is best-effort: if it finds no page or the
   * libraries fail to load, we keep the untouched frame.
   */
  async shutter() {
    if (this.processing()) return;
    this.processing.set(true);
    try {
      // Hard ceiling on the whole grab: ImageCapture.takePhoto() is known to
      // never settle on some Android devices — without this the overlay
      // would spin forever.
      const originalBlob = await InvoiceCameraPage
        .withTimeout(this.grabStill(), 15_000)
        .catch(() => null);
      if (!originalBlob) {
        // Two misses in a row means the in-tab camera pipeline is spiralling
        // (degraded → black → tab crash on this device). Stop before it OOMs
        // and steer the manager to the rock-solid native camera / file picker.
        if (++this.grabFails >= 2) {
          this.grabFails = 0;
          this.stopStream();
          this.errorMsg.set('Fotoaparát v aplikácii je na tomto telefóne nestabilný — použite „Odfotiť stranu“ (fotoaparát telefónu) alebo vyberte fotky zo zariadenia.');
          this.state.set('error');
          return;
        }
        this.errorMsg.set('Fotenie zlyhalo — skúste znova.');
        // The camera layer/track often dies together with the failure —
        // reset zoom and revive the preview.
        void this.setZoom(1);
        this.ensureLiveStream();
        return;
      }
      this.errorMsg.set(null);
      this.grabFails = 0;

      this.pending.set({
        blob: originalBlob,
        thumbUrl: URL.createObjectURL(originalBlob),
        warning: this.lastCaptureLowRes
          ? 'Nízke rozlíšenie — skúste 1× priblíženie.'
          : null
      });
      this.state.set('review-page');
      // Release the camera during review — the live pipeline is the single
      // biggest memory consumer in the tab, and keeping it running under
      // the review/encode work is what crashed phones after 2 captures.
      // Retake / next page restarts it in ~half a second.
      this.stopStream();
    } finally {
      // The overlay must NEVER survive the shutter — whatever went wrong.
      this.processing.set(false);
    }
  }

  /** Await a promise but give up after ms — a wedged device API must never
   *  freeze the capture flow. */
  private static withTimeout<T>(p: Promise<T>, ms: number): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      const t = setTimeout(() => reject(new Error('timeout')), ms);
      p.then(
        v => { clearTimeout(t); resolve(v); },
        e => { clearTimeout(t); reject(e); }
      );
    });
  }

  /**
   * Grab the best still the device can give. Android Chrome exposes
   * ImageCapture.takePhoto() — the true photo pipeline (full sensor
   * resolution + a fresh autofocus run). iOS Safari doesn't, so there we
   * grab the (now high-res) video frame at maximum JPEG quality — invoice
   * scans are never compressed beyond that.
   */
  private async grabStill(): Promise<Blob | null> {
    // 1) Full-resolution source frame. Android Chrome: the still-photo
    //    pipeline. iOS: the sharpest of a 3-frame burst grab.
    let source: ImageBitmap | HTMLCanvasElement | null = null;
    let sw = 0;
    let sh = 0;
    const track = this.stream?.getVideoTracks()[0] ?? null;
    if (track && 'ImageCapture' in window) {
      try {
        const ic = new (window as any).ImageCapture(track);
        // Ask for the SENSOR maximum — without explicit dimensions many
        // devices return a much smaller default still. Both calls carry
        // their own timeout: on some Androids they simply never settle,
        // and the frame-grab below is a perfectly good plan B.
        let opts: any;
        try {
          const pc = await InvoiceCameraPage.withTimeout<any>(ic.getPhotoCapabilities(), 3_000);
          if (pc?.imageWidth?.max && pc?.imageHeight?.max) {
            opts = { imageWidth: pc.imageWidth.max, imageHeight: pc.imageHeight.max };
          }
        } catch { /* use device defaults */ }
        const photo: Blob = await InvoiceCameraPage.withTimeout<Blob>(
          ic.takePhoto(opts).catch(() => ic.takePhoto()), 6_000);
        source = await createImageBitmap(photo);
        sw = source.width;
        sh = source.height;
      } catch {
        source = null;   // takePhoto hung or rejected — frame grab instead
      }
    }
    if (!source) {
      // Frame-grab path (iOS/desktop): burst 3 frames and keep the sharpest
      // — hand-shake insurance the photo pipeline would otherwise provide.
      const frame = await this.grabSharpestFrame();
      if (!frame) return null;   // stream not ready
      source = frame;
      sw = frame.width;
      sh = frame.height;
    }

    // 2) Crop to what the viewfinder showed: centre 3:4 cover region,
    //    tightened by the digital-zoom factor. Native zoom is already baked
    //    into the frame, so no extra crop for it.
    const zoomF = this.zoomNative() ? 1 : this.zoom();
    const targetAspect = 3 / 4;
    let cw = sw;
    let ch = sh;
    if (cw / ch > targetAspect) cw = ch * targetAspect;
    else ch = cw / targetAspect;
    cw /= zoomF;
    ch /= zoomF;
    let cx = (sw - cw) / 2;
    let cy = (sh - ch) / 2;

    // 3) Cut off the guide-frame borders: the overlay dims everything
    //    outside inset-x-[5%] / inset-y-[3%] of the viewfinder, and the
    //    photo keeps ONLY what the frame showed (WYSIWYG — table edges and
    //    background clutter never reach the model). Keep these fractions in
    //    sync with the template's guide element.
    cx += cw * 0.05;
    cy += ch * 0.03;
    cw *= 0.90;
    ch *= 0.94;

    const canvas = this.canvasRef()?.nativeElement;
    if (!canvas) return null;
    canvas.width = Math.round(cw);
    canvas.height = Math.round(ch);
    const ctx = canvas.getContext('2d');
    if (!ctx) return null;
    ctx.drawImage(source as CanvasImageSource, cx, cy, cw, ch, 0, 0, canvas.width, canvas.height);
    if (source instanceof ImageBitmap) source.close();
    // iOS Safari has a hard canvas-memory budget; exhausting it makes new
    // canvases silently BLACK and toBlob return null. Release the big burst
    // buffer immediately — it gets re-sized on the next shot anyway.
    if (this.fullFrame) {
      this.fullFrame.width = 0;
      this.fullFrame.height = 0;
    }
    // Consistency guard: below ~1500 px the small table print won't survive
    // OCR (happens with 3× digital zoom on a low-res stream) — the review
    // screen warns and nudges back to 1× + filling the guide frame.
    this.lastCaptureLowRes = Math.max(canvas.width, canvas.height) < 1500;
    let blob = await new Promise<Blob | null>(resolve =>
      canvas.toBlob(b => resolve(b), 'image/jpeg', 0.95)
    );
    if (!blob) {
      // Encoder under memory pressure — one more try, slightly cheaper.
      blob = await new Promise<Blob | null>(resolve =>
        canvas.toBlob(b => resolve(b), 'image/jpeg', 0.85)
      );
    }
    // Release the capture buffer too (~25 MB at 4K) — re-sized next shot.
    canvas.width = 0;
    canvas.height = 0;
    return blob;
  }

  /**
   * Burst capture for the frame-grab path: sample 3 frames ~140 ms apart,
   * score each on a 320 px copy (Tenengrad gradient energy — pure JS, no
   * OpenCV needed yet) and keep the sharpest at full resolution. One
   * reusable full-res buffer, redrawn only when the score improves.
   */
  private async grabSharpestFrame(): Promise<HTMLCanvasElement | null> {
    const video = this.videoRef()?.nativeElement;
    if (!video || !video.videoWidth) return null;
    this.fullFrame ??= document.createElement('canvas');
    const full = this.fullFrame;
    const small = document.createElement('canvas');
    small.width = 320;
    small.height = Math.max(1, Math.round(320 * video.videoHeight / video.videoWidth));
    const sctx = small.getContext('2d', { willReadFrequently: true });
    if (!sctx) return null;

    let best = -1;
    for (let i = 0; i < 2; i++) {
      sctx.drawImage(video, 0, 0, small.width, small.height);
      const score = InvoiceCameraPage.tenengrad(sctx.getImageData(0, 0, small.width, small.height));
      if (score > best) {
        best = score;
        full.width = video.videoWidth;
        full.height = video.videoHeight;
        full.getContext('2d')!.drawImage(video, 0, 0);
      }
      if (i < 1) await new Promise(r => setTimeout(r, 140));
    }
    // Release the small scoring scratch buffer now (the full-res buffer is
    // reused and freed by grabStill once it has encoded the JPEG).
    small.width = 0;
    small.height = 0;
    return best >= 0 ? full : null;
  }

  /** Gradient-energy focus score of a small grayscale copy. */
  private static tenengrad(img: ImageData): number {
    const { data, width, height } = img;
    const g = new Float32Array(width * height);
    for (let i = 0, p = 0; i < data.length; i += 4, p++) {
      g[p] = 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
    }
    let s = 0;
    for (let y = 1; y < height - 1; y++) {
      for (let x = 1; x < width - 1; x++) {
        const p = y * width + x;
        const gx = g[p + 1] - g[p - 1];
        const gy = g[p + width] - g[p - width];
        s += gx * gx + gy * gy;
      }
    }
    return s;
  }

  async acceptPending() {
    const p = this.pending();
    if (!p) return;
    // The grid gets a SMALL thumbnail, never the full-resolution image —
    // N full-res <img> bitmaps (~25 MB each decoded) were exactly the
    // memory pressure that made WebKit throttle the camera stream after a
    // few pages. The full blob is kept only for the upload.
    const thumbUrl = await this.makeThumb(p.blob);
    URL.revokeObjectURL(p.thumbUrl);
    this.pages.update(arr => [...arr, { id: this.nextPageId++, blob: p.blob, thumbUrl }]);
    this.pending.set(null);
    // After the first accepted page, default to pages-list (lets the manager
    // decide: add another page or submit). Subsequent shutters drop them
    // back to review-page until they hit "Pridať ďalšiu stranu".
    this.state.set('pages-list');
  }

  retakePending() {
    const p = this.pending();
    if (p) URL.revokeObjectURL(p.thumbUrl);
    this.pending.set(null);
    this.state.set('streaming');
    // The track (or its video layer) may have died during the heavy
    // crop/enhance work — make sure the preview is actually alive.
    this.ensureLiveStream();
  }

  /** Downscale oversized stills to ≤3000 px JPEG (one transient canvas,
   *  released immediately). Files ≤4 MB pass through untouched. */
  private async shrinkForUpload(blob: Blob): Promise<Blob> {
    if (blob.size <= 4 * 1024 * 1024) return blob;
    try {
      const bmp = await createImageBitmap(blob);
      const scale = Math.min(1, 3000 / Math.max(bmp.width, bmp.height));
      const c = document.createElement('canvas');
      c.width = Math.max(1, Math.round(bmp.width * scale));
      c.height = Math.max(1, Math.round(bmp.height * scale));
      c.getContext('2d')!.drawImage(bmp, 0, 0, c.width, c.height);
      bmp.close();
      const out = await new Promise<Blob | null>(res => c.toBlob(b => res(b), 'image/jpeg', 0.9));
      c.width = 0;
      c.height = 0;
      return out ?? blob;
    } catch {
      return blob;
    }
  }

  /** ~480 px display thumbnail — the grid must never decode full pages. */
  private async makeThumb(blob: Blob): Promise<string> {
    try {
      const bmp = await createImageBitmap(blob);
      const scale = Math.min(1, 480 / Math.max(bmp.width, bmp.height));
      const c = document.createElement('canvas');
      c.width = Math.max(1, Math.round(bmp.width * scale));
      c.height = Math.max(1, Math.round(bmp.height * scale));
      c.getContext('2d')!.drawImage(bmp, 0, 0, c.width, c.height);
      bmp.close();
      const thumb = await new Promise<Blob | null>(res => c.toBlob(b => res(b), 'image/jpeg', 0.8));
      c.width = 0;
      c.height = 0;
      return URL.createObjectURL(thumb ?? blob);
    } catch {
      return URL.createObjectURL(blob);
    }
  }

  /**
   * The camera track or its GPU video layer can die under memory pressure
   * (the preview then shows black even though the state machine is fine).
   * Restart the camera when the track is dead; re-kick playback otherwise.
   */
  private ensureLiveStream() {
    const track = this.stream?.getVideoTracks()[0];
    const set = track?.getSettings?.();
    const edge = Math.max(set?.width ?? 0, set?.height ?? 0);
    const live = !!track && track.readyState === 'live' && !track.muted;
    // A track can read as "live" while WebKit has silently DOWNSCALED it after
    // a heavy capture — that's the "next photo comes out unusably low-res"
    // case. Treat a degraded (<1000 px) stream as dead and re-acquire cleanly
    // instead of letting the manager shoot a blurry page.
    if (!live || (edge > 0 && edge < 1000)) {
      this.stopStream();
      // Give the dead track a moment to fully release the camera —
      // re-acquiring instantly tends to hand back a degraded (blurry)
      // low-resolution stream on phones.
      setTimeout(() => void this.startCamera(), 400);
      return;
    }
    this.videoRef()?.nativeElement?.play().catch(() => {});
  }

  // ─── Pages-list operations ────────────────────────────────────

  goAddAnother() {
    // Follow the flow the manager chose: live-viewfinder users go back to
    // the viewfinder (re-acquired — the camera is released between shots),
    // native-camera users get the native camera app again.
    const live = this.stream?.getVideoTracks().some(t => t.readyState === 'live') ?? false;
    if (live) {
      this.state.set('streaming');
      return;
    }
    this.stopStream();
    if (this.preferLive) {
      void this.startCamera();
      return;
    }
    this.openNativeCamera();
  }

  removePage(id: number) {
    this.pages.update(arr => {
      const removed = arr.find(p => p.id === id);
      if (removed) URL.revokeObjectURL(removed.thumbUrl);
      return arr.filter(p => p.id !== id);
    });
    // Deleted the last page → go back to the flow the manager was using: the
    // live viewfinder for live users, otherwise the idle screen (native/file
    // users retake from there instead of being dropped into the in-app view).
    if (this.pages().length === 0) {
      this.state.set(this.preferLive ? 'streaming' : 'idle');
    }
  }

  movePage(id: number, dir: -1 | 1) {
    this.pages.update(arr => {
      const idx = arr.findIndex(p => p.id === id);
      const target = idx + dir;
      if (idx < 0 || target < 0 || target >= arr.length) return arr;
      const copy = arr.slice();
      const [item] = copy.splice(idx, 1);
      copy.splice(target, 0, item);
      return copy;
    });
  }

  // ─── File-picker fallback (when camera permission denied) ─────

  openFilePicker() {
    // A file-picker user isn't on the live viewfinder — treat them like the
    // native flow for return-navigation (so removing the last page lands on
    // idle, not the in-app camera).
    this.preferLive = false;
    this.fileInputRef()?.nativeElement.click();
  }

  async onFallbackFiles(event: Event) {
    const input = event.target as HTMLInputElement;
    // FileList is LIVE in Chrome: resetting input.value empties a previously
    // captured reference too, so the loop below would see zero files and the
    // page would land on "Naskenované strany: 0" with nothing sent. Snapshot
    // the File objects into a real array BEFORE clearing the input.
    const files = Array.from(input.files ?? []);
    input.value = '';
    if (files.length === 0) return;

    // Normalise each (HEIC → PNG, anything else passes through) and add as
    // a page. Server-side ImageSharp does the real normalisation; each file
    // is fault-isolated and the overlay is cleared in finally.
    this.processing.set(true);
    try {
      const accepted: { id: number; blob: Blob; thumbUrl: string }[] = [];
      for (let i = 0; i < files.length; i++) {
        let blob: Blob;
        try {
          blob = await normaliseFile(files[i]);
        } catch {
          blob = files[i];   // HEIC conversion failed — send the original
        }
        // Native-camera stills can be 5–8 MB (HEIC→PNG even more) — a
        // multi-page invoice would blow the 40 MB upload cap. One bounded
        // downscale to ≤3000 px JPEG, only when oversized.
        blob = await this.shrinkForUpload(blob);
        accepted.push({
          id: this.nextPageId++,
          blob,
          thumbUrl: await this.makeThumb(blob)
        });
      }
      this.pages.update(arr => [...arr, ...accepted]);
      this.state.set('pages-list');
    } finally {
      this.processing.set(false);
    }
  }

  // ─── Submit ───────────────────────────────────────────────────

  async submit() {
    const all = this.pages();
    if (all.length === 0) return;
    this.state.set('uploading');
    this.errorMsg.set(null);

    // Wrap each Blob in a File so the multipart form-data has a filename
    // (some backends and some browsers care).
    const files: File[] = all.map((p, i) =>
      new File([p.blob], `page-${i + 1}.jpg`, { type: p.blob.type || 'image/jpeg' })
    );

    try {
      const doc = await this.svc.uploadPhotos(files);
      this.stopStream();
      this.router.navigate(['/admin/invoices', doc.id]);
    } catch (e: any) {
      this.errorMsg.set(this.svc.friendlyError(e, 'Odoslanie zlyhalo. Skúste znova.'));
      this.state.set('pages-list');
    }
  }

  // ─── Cancel / exit ────────────────────────────────────────────

  cancel() {
    this.stopStream();
    this.router.navigate(['/admin/invoices']);
  }
}
