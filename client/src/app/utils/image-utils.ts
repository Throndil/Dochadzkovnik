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
