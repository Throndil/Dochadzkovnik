import heic2any from 'heic2any';

/**
 * Normalises any image file to a PNG-compatible File.
 * - HEIC/HEIF (iPhone default) → converted to PNG blob via heic2any
 * - All other types → returned as-is (backend will normalise further)
 */
export async function normaliseFile(file: File): Promise<File> {
  const isHeic =
    file.type === 'image/heic' ||
    file.type === 'image/heif' ||
    file.name.toLowerCase().endsWith('.heic') ||
    file.name.toLowerCase().endsWith('.heif');

  if (!isHeic) return file;

  const blob = await heic2any({ blob: file, toType: 'image/png', quality: 0.9 });
  const pngBlob = Array.isArray(blob) ? blob[0] : blob;
  const newName = file.name.replace(/\.(heic|heif)$/i, '.png');
  return new File([pngBlob], newName, { type: 'image/png' });
}

/**
 * Compresses an image file to JPEG at the given quality and max dimension.
 * - Resizes so the longest edge does not exceed maxDimension px
 * - Re-encodes as JPEG at the given quality (0–1, default 0.72)
 * Always call normaliseFile() first if the input might be HEIC.
 */
export function compressImage(
  file: File,
  maxDimension = 1200,
  quality = 0.72
): Promise<File> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    const objectUrl = URL.createObjectURL(file);
    img.onload = () => {
      URL.revokeObjectURL(objectUrl);
      const scale = Math.min(1, maxDimension / Math.max(img.width, img.height));
      const w = Math.round(img.width * scale);
      const h = Math.round(img.height * scale);
      const canvas = document.createElement('canvas');
      canvas.width = w;
      canvas.height = h;
      canvas.getContext('2d')!.drawImage(img, 0, 0, w, h);
      canvas.toBlob(
        blob => {
          if (!blob) { reject(new Error('Canvas toBlob failed')); return; }
          resolve(new File([blob], file.name.replace(/\.[^.]+$/, '.jpg'), { type: 'image/jpeg' }));
        },
        'image/jpeg',
        quality
      );
    };
    img.onerror = reject;
    img.src = objectUrl;
  });
}

/**
 * Reads a File and returns a data-URL string for preview thumbnails.
 */
export function fileToDataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = reject;
    reader.readAsDataURL(file);
  });
}

/**
 * Transforms a Cloudinary image URL to serve a compressed thumbnail.
 * Inserts `w_<w>,h_<h>,c_fill,q_auto,f_auto/` before the version/public-id segment.
 * Falls back to the original URL if it doesn't look like a Cloudinary upload URL.
 */
export function cloudinaryThumb(url: string, w = 400, h = 400): string {
  if (!url) return url;
  const uploadMarker = '/upload/';
  const idx = url.indexOf(uploadMarker);
  if (idx === -1) return url;
  const base = url.slice(0, idx + uploadMarker.length);
  const rest = url.slice(idx + uploadMarker.length);
  return `${base}w_${w},h_${h},c_fill,q_auto,f_auto/${rest}`;
}
