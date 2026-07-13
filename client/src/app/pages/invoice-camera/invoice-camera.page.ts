import { Component, ElementRef, OnDestroy, effect, signal, viewChild, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { InvoiceService } from '../../services/invoice.service';
import { normaliseFile } from '../../utils/image-utils';
import { autoCropDocument } from '../../utils/document-scan';

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
export class InvoiceCameraPage implements OnDestroy {
  private svc = inject(InvoiceService);
  private router = inject(Router);

  // ── View refs (live <video>, hidden capture canvas, hidden file-picker fallback) ──
  videoRef     = viewChild<ElementRef<HTMLVideoElement>>('preview');
  canvasRef    = viewChild<ElementRef<HTMLCanvasElement>>('capture');
  fileInputRef = viewChild<ElementRef<HTMLInputElement>>('fileInput');

  // ── State machine ──
  state = signal<'idle' | 'streaming' | 'review-page' | 'pages-list' | 'uploading' | 'error'>('idle');
  errorMsg = signal<string | null>(null);

  /** Pages already accepted by the manager. Order matters — page 1 first. */
  pages = signal<{ id: number; blob: Blob; thumbUrl: string }[]>([]);

  /**
   * Frozen frame shown on the review-page state. Holds both the untouched
   * photo and the auto-cropped version (when detection succeeded) so the
   * manager can toggle between them. `useProcessed` drives which one is
   * shown and ultimately accepted; `thumbUrl` always points at the active one.
   */
  pending = signal<{
    originalBlob: Blob;
    processedBlob: Blob | null;
    useProcessed: boolean;
    thumbUrl: string;
  } | null>(null);

  /** True while the auto-crop detection runs after pressing the shutter. */
  processing = signal(false);

  /**
   * Manager's auto-crop preference, remembered across captures. Starts on so
   * scans are cleaned by default; flipping the review-page toggle persists the
   * choice for subsequent pages.
   */
  autoCropEnabled = signal(true);

  private nextPageId = 1;
  private stream: MediaStream | null = null;

  constructor() {
    // The <video #preview> lives inside @if(state === 'streaming'), so it is
    // (re)created AFTER startCamera() flips the state — and again every time
    // the manager returns from the review step. Assigning srcObject before
    // the element existed was the phone's black-preview bug (the shutter then
    // saw videoWidth 0 and silently did nothing). Attach the live stream
    // whenever the element (re)appears instead.
    effect(() => {
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
  async startCamera() {
    this.errorMsg.set(null);
    try {
      this.stream = await navigator.mediaDevices.getUserMedia({
        video: {
          facingMode: { ideal: 'environment' },
          // Paper OCR lives or dies by pixels: WITHOUT these iOS hands out a
          // 640×480 stream and the text is mush. Browsers clamp "ideal" to
          // the camera's best supported video mode (1080p/4K on phones).
          width:  { ideal: 3840 },
          height: { ideal: 2160 }
        },
        audio: false
      });
    } catch (e: any) {
      this.errorMsg.set(this.translatePermissionError(e));
      this.state.set('error');
      return;
    }
    // The stream is attached by the constructor effect once the <video>
    // element renders (it doesn't exist until the state flips below).
    this.state.set('streaming');
  }

  private stopStream() {
    if (!this.stream) return;
    for (const track of this.stream.getTracks()) track.stop();
    this.stream = null;
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
    this.processing.set(true);
    const originalBlob = await this.grabStill();
    if (!originalBlob) {
      this.processing.set(false);
      return;
    }

    // Run auto-crop + sharpen. The indicator matters on the first run, which
    // downloads the OpenCV wasm; later runs are fast (libraries cached).
    let processedBlob: Blob | null = null;
    try {
      const res = await autoCropDocument(originalBlob, 0.95);
      if (res.cropped) processedBlob = res.blob;
    } catch {
      // Ignore — fall back to the untouched frame below.
    }
    this.processing.set(false);

    const useProcessed = processedBlob != null && this.autoCropEnabled();
    const activeBlob = useProcessed ? processedBlob! : originalBlob;
    this.pending.set({
      originalBlob,
      processedBlob,
      useProcessed,
      thumbUrl: URL.createObjectURL(activeBlob)
    });
    this.state.set('review-page');
  }

  /**
   * Grab the best still the device can give. Android Chrome exposes
   * ImageCapture.takePhoto() — the true photo pipeline (full sensor
   * resolution + a fresh autofocus run). iOS Safari doesn't, so there we
   * grab the (now high-res) video frame at maximum JPEG quality — invoice
   * scans are never compressed beyond that.
   */
  private async grabStill(): Promise<Blob | null> {
    const track = this.stream?.getVideoTracks()[0] ?? null;
    if (track && 'ImageCapture' in window) {
      try {
        return await new (window as any).ImageCapture(track).takePhoto();
      } catch {
        // Some devices reject takePhoto mid-stream — use the frame grab.
      }
    }
    const video = this.videoRef()?.nativeElement;
    const canvas = this.canvasRef()?.nativeElement;
    if (!video || !canvas) return null;
    const w = video.videoWidth;
    const h = video.videoHeight;
    if (!w || !h) return null;     // stream not ready
    canvas.width = w;
    canvas.height = h;
    const ctx = canvas.getContext('2d');
    if (!ctx) return null;
    ctx.drawImage(video, 0, 0, w, h);
    return await new Promise<Blob | null>(resolve =>
      canvas.toBlob(b => resolve(b), 'image/jpeg', 0.95)
    );
  }

  /** Switch the review preview between the auto-cropped and original image. */
  toggleProcessed() {
    const p = this.pending();
    if (!p || !p.processedBlob) return;
    URL.revokeObjectURL(p.thumbUrl);
    const useProcessed = !p.useProcessed;
    const activeBlob = useProcessed ? p.processedBlob : p.originalBlob;
    // Remember the choice so the next captured page defaults the same way.
    this.autoCropEnabled.set(useProcessed);
    this.pending.set({ ...p, useProcessed, thumbUrl: URL.createObjectURL(activeBlob) });
  }

  acceptPending() {
    const p = this.pending();
    if (!p) return;
    const blob = p.useProcessed && p.processedBlob ? p.processedBlob : p.originalBlob;
    // The active thumbUrl already renders `blob`, so hand it to the page as-is
    // (don't revoke it — the page now owns it; ngOnDestroy/removePage will).
    this.pages.update(arr => [...arr, { id: this.nextPageId++, blob, thumbUrl: p.thumbUrl }]);
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
  }

  // ─── Pages-list operations ────────────────────────────────────

  goAddAnother() {
    // "Pridať ďalšiu stranu" can be pressed when no camera stream exists
    // (PC / file-picker flow), or when the phone killed the track after
    // backgrounding the browser — jumping to the streaming state would show
    // a dead black preview. Start the camera properly; on failure the error
    // state offers the file-picker fallback.
    const live = this.stream?.getVideoTracks().some(t => t.readyState === 'live') ?? false;
    if (!live) {
      this.stopStream();
      this.startCamera();
      return;
    }
    this.state.set('streaming');
  }

  removePage(id: number) {
    this.pages.update(arr => {
      const removed = arr.find(p => p.id === id);
      if (removed) URL.revokeObjectURL(removed.thumbUrl);
      return arr.filter(p => p.id !== id);
    });
    // If they deleted the last page, bounce back to streaming.
    if (this.pages().length === 0) this.state.set('streaming');
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

    // Normalise each (HEIC → PNG, anything else passes through), then run the
    // same auto-crop as live captures. Detection is best-effort per image —
    // a photo where no page is found keeps its normalised original.
    this.processing.set(true);
    const accepted: { id: number; blob: Blob; thumbUrl: string }[] = [];
    for (let i = 0; i < files.length; i++) {
      const normalised = await normaliseFile(files[i]);
      let blob: Blob = normalised;
      if (this.autoCropEnabled()) {
        try {
          const res = await autoCropDocument(normalised, 0.95);
          if (res.cropped) blob = res.blob;
        } catch {
          // Ignore — keep the normalised original.
        }
      }
      accepted.push({
        id: this.nextPageId++,
        blob,
        thumbUrl: URL.createObjectURL(blob)
      });
    }
    this.processing.set(false);
    this.pages.update(arr => [...arr, ...accepted]);
    this.state.set('pages-list');
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
      const msg =
        typeof e?.error === 'string' ? e.error :
        typeof e?.error?.error === 'string' ? e.error.error :
        e?.message ?? 'Odoslanie zlyhalo. Skúste znova.';
      this.errorMsg.set(msg);
      this.state.set('pages-list');
    }
  }

  // ─── Cancel / exit ────────────────────────────────────────────

  cancel() {
    this.stopStream();
    this.router.navigate(['/admin/invoices']);
  }
}
